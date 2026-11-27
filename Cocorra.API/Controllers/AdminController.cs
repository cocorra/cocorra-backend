using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.AdminService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthPolicies.AdminOrModerator)]
    public class AdminController : ControllerBase
    {
        private readonly IAdminService _adminService;
        private readonly IHubContext<RoomHub> _roomHubContext;
        private readonly IStaffConnectionRevoker _staffConnectionRevoker;

        public AdminController(
            IAdminService adminService,
            IHubContext<RoomHub> roomHubContext,
            IStaffConnectionRevoker staffConnectionRevoker)
        {
            _adminService = adminService;
            _roomHubContext = roomHubContext;
            _staffConnectionRevoker = staffConnectionRevoker;
        }

        [HttpGet(Router.AdminRouting.GetAll)]
        public async Task<IActionResult> GetAllUsers(
            [FromQuery] string? search,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 10)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var result = await _adminService.GetAllUsersAsync(
                search,
                page,
                pageSize,
                callerIsAdmin: User.IsInRole(AppRoles.Admin));
            return Ok(result);
        }

        [HttpGet(Router.AdminRouting.GetById)]
        public async Task<IActionResult> GetUserById([FromRoute] Guid id)
        {
            var result = await _adminService.GetUserByIdAsync(id, callerIsAdmin: User.IsInRole(AppRoles.Admin));
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        [Authorize(Policy = AuthPolicies.AdminOnly)]
        [HttpGet(Router.AdminRouting.VoiceUrl)]
        public async Task<IActionResult> GetUserVoiceUrl([FromRoute] Guid id)
        {
            var result = await _adminService.GetUserVoiceUrlAsync(id);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPut(Router.AdminRouting.ChangeStatus)]
        public async Task<IActionResult> ChangeStatus([FromRoute] Guid id, [FromBody] ChangeStatusDto model)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (!model.NewStatus.HasValue)
            {
                return BadRequest(new { succeeded = false, message = "NewStatus is required." });
            }

            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorGuid))
            {
                return BadRequest(new { succeeded = false, message = "Could not resolve the acting admin identity." });
            }

            if (actorGuid == id)
            {
                return BadRequest(new { succeeded = false, message = "You cannot change your own status." });
            }

            var newStatus = model.NewStatus.Value;
            var result = await _adminService.ChangeUserStatusAsync(
                id,
                newStatus,
                model.Reason,
                actorGuid,
                callerIsAdmin: User.IsInRole(AppRoles.Admin));
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            if (newStatus is UserStatus.Banned or UserStatus.Rejected)
            {
                var disconnectReason = newStatus == UserStatus.Banned
                    ? "Your account has been banned."
                    : "Your account has been rejected.";
                await DisconnectUserFromRoomHubAsync(id, disconnectReason);
            }

            if (result.SessionRevoked)
            {
                await _staffConnectionRevoker.RevokeAsync(id);
            }

            return Ok(result);
        }

        [Authorize(Policy = AuthPolicies.AdminOnly)]
        [HttpPut(Router.AdminRouting.BulkChangeStatus)]
        public async Task<IActionResult> BulkChangeStatus([FromBody] BulkChangeStatusDto model)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId);

            var result = await _adminService.BulkChangeUserStatusAsync(model, adminId);
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            var succeededItems = result.Data!.Results.Where(r => r.Succeeded).ToList();
            if (model.NewStatus is UserStatus.Banned or UserStatus.Rejected)
            {
                var disconnectReason = model.NewStatus == UserStatus.Banned
                    ? "Your account has been banned."
                    : "Your account has been rejected.";

                foreach (var item in succeededItems)
                {
                    await DisconnectUserFromRoomHubAsync(item.UserId, disconnectReason);
                }
            }

            foreach (var item in succeededItems.Where(r => r.SessionRevoked))
            {
                await _staffConnectionRevoker.RevokeAsync(item.UserId);
            }

            return Ok(result);
        }

        [HttpGet(Router.AdminRouting.Stats)]
        public async Task<IActionResult> GetDashboardStats()
        {
            var result = await _adminService.GetDashboardStatsAsync(callerIsAdmin: User.IsInRole(AppRoles.Admin));
            return Ok(result);
        }

        [Authorize(Policy = AuthPolicies.AdminOnly)]
        [HttpPost(Router.AdminRouting.Prefix + "/BlockDeviceAndEmail")]
        public async Task<IActionResult> BlockDeviceAndEmail([FromBody] BlockDeviceAndEmailDto model)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var adminEmail = User.FindFirstValue(ClaimTypes.Email);
            if (string.Equals(adminEmail, model.Email, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { succeeded = false, message = "You cannot perform this action on yourself." });
            }

            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminGuid))
            {
                return BadRequest(new { succeeded = false, message = "Could not resolve the acting admin identity." });
            }

            var result = await _adminService.BlockDeviceAndEmailAsync(model, adminGuid);
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            var bannedUserId = result.Data!.UserId;
            await DisconnectUserFromRoomHubAsync(bannedUserId, "Your account has been banned.");
            await _staffConnectionRevoker.RevokeAsync(bannedUserId);

            return Ok(result);
        }

        [Authorize(Policy = AuthPolicies.AdminOnly)]
        [HttpPost(Router.AdminRouting.BroadcastNotification)]
        [HttpPost(Router.AdminRouting.Prefix + "/Notifications/Broadcast")]
        public async Task<IActionResult> BroadcastNotification([FromBody] SendBroadcastNotificationDto model)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (string.IsNullOrWhiteSpace(model.Body))
            {
                return BadRequest(new { succeeded = false, message = "Notification body or message is required." });
            }

            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminGuid))
            {
                return BadRequest(new { succeeded = false, message = "Could not resolve the acting admin identity." });
            }

            var result = await _adminService.BroadcastNotificationAsync(model, adminGuid);
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            return Ok(result);
        }

        private async Task DisconnectUserFromRoomHubAsync(Guid userId, string reason)
        {
            var connectionIds = RoomHub.GetConnectionsForUser(userId);
            foreach (var connId in connectionIds)
            {
                await _roomHubContext.Clients.Client(connId)
                    .SendAsync("ForceDisconnect", new { Reason = reason });
            }
            RoomHub.PurgeUserConnections(userId);
        }
    }
}
