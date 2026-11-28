using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.SupportService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Cocorra.API.Controllers
{
    [ApiController]
    public class SupportController : ControllerBase
    {
        private readonly ISupportService _supportService;
        private readonly IHubContext<SupportHub> _supportHub;

        public SupportController(ISupportService supportService, IHubContext<SupportHub> supportHub)
        {
            _supportService = supportService;
            _supportHub = supportHub;
        }

        private bool TryGetCallerId(out Guid callerId) =>
            Guid.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out callerId);

        [AllowAnonymous]
        [HttpPost(Router.SupportRouting.SubmitTicket)]
        public async Task<IActionResult> SubmitTicket([FromForm] SubmitSupportTicketDto dto)
        {
            Guid? userId = TryGetCallerId(out var parsedId) ? parsedId : null;

            var result = await _supportService.SubmitTicketAsync(userId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpPost(Router.SupportRouting.SubmitReport)]
        public async Task<IActionResult> SubmitReport([FromForm] SubmitReportDto dto)
        {
            if (!TryGetCallerId(out var reporterId)) return Unauthorized();

            var result = await _supportService.SubmitReportAsync(reporterId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminReports)]
        public async Task<IActionResult> GetReports(
            [FromQuery] ReportCategory? category,
            [FromQuery] string? status,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] ModerationSeverity? severity = null,
            [FromQuery] string? sort = null)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 100);

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var prioritySort = string.Equals(sort, "priority", StringComparison.OrdinalIgnoreCase);
            var result = await _supportService.GetFilteredReportsAsync(
                new ReportListQuery(category, status, severity, prioritySort, pageNumber, pageSize), callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminReportById)]
        public async Task<IActionResult> GetReportById(Guid id)
        {
            if (!TryGetCallerId(out var callerId)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.GetReportByIdAsync(id, callerId, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        // Every URL issued is recorded in the audit log.
        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminReportScreenshotUrl)]
        public async Task<IActionResult> GetReportScreenshotUrl(Guid id)
        {
            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.GetReportScreenshotUrlAsync(id, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        // General tickets (POST Support/Ticket) live in their own table, so admin/reports never lists them.
        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminTickets)]
        public async Task<IActionResult> GetTickets(
            [FromQuery] SupportTicketType? type,
            [FromQuery] SupportTicketStatus? status,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.GetAdminTicketsAsync(type, status, pageNumber, pageSize, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminTicketById)]
        public async Task<IActionResult> GetTicketById(Guid id)
        {
            if (!TryGetCallerId(out var callerId)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.GetTicketByIdAsync(id, callerId, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPut(Router.SupportRouting.AdminUpdateTicketStatus)]
        public async Task<IActionResult> UpdateTicketStatus(Guid id, [FromBody] UpdateTicketStatusDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!TryGetCallerId(out var callerId)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.UpdateTicketStatusAsync(id, dto.Status!.Value, dto.Reason, callerId, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        // Every URL issued is recorded in the audit log.
        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminTicketScreenshotUrl)]
        public async Task<IActionResult> GetTicketScreenshotUrl(Guid id)
        {
            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.GetTicketScreenshotUrlAsync(id, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPut(Router.SupportRouting.AdminUpdateReportStatus)]
        public async Task<IActionResult> UpdateReportStatus(Guid id, [FromBody] UpdateReportStatusDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!TryGetCallerId(out var callerId)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.UpdateReportStatusAsync(id, dto.Status, dto.Reason, callerId, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPost(Router.SupportRouting.AdminTakeReportAction)]
        public async Task<IActionResult> TakeReportAction(Guid id, [FromBody] TakeReportActionDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (!TryGetCallerId(out var callerId)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.TakeActionOnReportAsync(id, dto, callerId, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        // --- Chat Support Endpoints ---

        [Authorize]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/send")]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString)) return Unauthorized();

            var result = await _supportService.SendMessageAsync(userIdString, dto);

            if (result.StatusCode == System.Net.HttpStatusCode.OK && result.Data != null)
            {
                if (!string.IsNullOrEmpty(result.Data.ClaimedByAdminId))
                {
                    // Claimed chats are private to the claiming staff member and Admins; never broadcast to all Moderators.
                    await _supportHub.Clients.User(result.Data.ClaimedByAdminId)
                        .SendAsync("ReceiveSupportMessage", result.Data.Message);
                    await _supportHub.Clients.Group(SupportHub.SupportAdminsGroup)
                        .SendAsync("ReceiveSupportMessage", result.Data.Message);
                }
                else if (result.Data.IsNewChat)
                {
                    await _supportHub.Clients.Group(SupportHub.AdminsGroup).SendAsync("NewPendingChatAlert");
                }
                else
                {
                    await _supportHub.Clients.Group(SupportHub.AdminsGroup).SendAsync("NewPendingChatAlert");
                    await _supportHub.Clients.Group(SupportHub.AdminsGroup)
                        .SendAsync("ReceiveSupportMessage", result.Data.Message);
                }
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/claim")]
        public async Task<IActionResult> ClaimChat(Guid chatId)
        {
            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var result = await _supportService.ClaimChatAsync(chatId, adminIdString);

            if (result.StatusCode == System.Net.HttpStatusCode.OK)
            {
                await _supportHub.Clients.Group(SupportHub.AdminsGroup).SendAsync("ChatClaimed", chatId);
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/reply")]
        public async Task<IActionResult> AdminReply(Guid chatId, [FromBody] SendMessageDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.AdminReplyAsync(chatId, adminIdString, dto, callerIsAdmin);

            if (result.StatusCode == System.Net.HttpStatusCode.OK && result.Data != null)
            {
                await _supportHub.Clients.User(result.Data.UserId).SendAsync("ReceiveSupportMessage", result.Data.Message);
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/close")]
        public async Task<IActionResult> CloseChat(Guid chatId)
        {
            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var callerIsAdmin = User.IsInRole(AppRoles.Admin);
            var result = await _supportService.CloseChatAsync(chatId, adminIdString, callerIsAdmin);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.Prefix + "/chat/pending")]
        public async Task<IActionResult> GetPendingChats([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var result = await _supportService.GetPendingChatsAsync(pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.Prefix + "/chat/active")]
        public async Task<IActionResult> GetAdminActiveChats([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
        {
            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var result = await _supportService.GetAdminActiveChatsAsync(adminIdString, pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpGet(Router.SupportRouting.Prefix + "/chat/history")]
        public async Task<IActionResult> GetUserChatHistory([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString)) return Unauthorized();

            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var result = await _supportService.GetUserChatHistoryAsync(userIdString, pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpGet(Router.SupportRouting.MyChat)]
        public async Task<IActionResult> GetMyChat()
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdString)) return Unauthorized();

            var result = await _supportService.GetUserOpenChatAsync(userIdString);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
