using Cocorra.BLL.Services.RoomInviteService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.RoomInviteDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize]
    public class RoomInviteController : ControllerBase
    {
        private readonly IRoomInviteService _inviteService;

        public RoomInviteController(IRoomInviteService inviteService)
        {
            _inviteService = inviteService;
        }

        [HttpPost(Router.RoomRouting.Invites)]
        public async Task<IActionResult> Create(
            [FromRoute] Guid roomId,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateRoomInviteDto? dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _inviteService.CreateInviteAsync(roomId, userId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        // Anonymous so the landing page can show whether a link still works before sign-in.
        // Rate limited because it is an unauthenticated oracle for code guessing.
        [AllowAnonymous]
        [EnableRateLimiting("invites")]
        [HttpGet(Router.InviteRouting.Resolve)]
        public async Task<IActionResult> Resolve([FromRoute] string inviteCode)
        {
            var result = await _inviteService.ResolveInviteAsync(inviteCode);
            return StatusCode((int)result.StatusCode, result);
        }

        [EnableRateLimiting("invites")]
        [HttpPost(Router.InviteRouting.Accept)]
        public async Task<IActionResult> Accept([FromRoute] string inviteCode)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _inviteService.AcceptInviteAsync(inviteCode, userId);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpDelete(Router.InviteRouting.Revoke)]
        public async Task<IActionResult> Revoke([FromRoute] string inviteCode)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            // Inviter-or-host-or-admin is decided in the service, which knows the room's host.
            var result = await _inviteService.RevokeInviteAsync(inviteCode, userId, User.IsInRole("Admin"));
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpGet(Router.AdminRouting.InviteStats)]
        public async Task<IActionResult> GetStats(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromQuery] Guid? roomId,
            [FromQuery] Guid? inviterUserId)
        {
            var result = await _inviteService.GetStatsAsync(from, to, roomId, inviterUserId);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
