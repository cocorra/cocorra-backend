using Cocorra.BLL.Services.SupportService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.SupportDto;
using Cocorra.DAL.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Cocorra.API.Hubs;
using Cocorra.DAL.DTOS.SupportChatDto;

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

        [AllowAnonymous]
        [HttpPost(Router.SupportRouting.SubmitTicket)]
        public async Task<IActionResult> SubmitTicket([FromForm] SubmitSupportTicketDto dto)
        {
            Guid? userId = null;
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            
            if (!string.IsNullOrEmpty(userIdString) && Guid.TryParse(userIdString, out Guid parsedId))
            {
                userId = parsedId;
            }

            var result = await _supportService.SubmitTicketAsync(userId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize]
        [HttpPost(Router.SupportRouting.SubmitReport)]
        public async Task<IActionResult> SubmitReport([FromForm] SubmitReportDto dto)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid reporterId)) return Unauthorized();

            var result = await _supportService.SubmitReportAsync(reporterId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpGet(Router.SupportRouting.AdminReports)]
        public async Task<IActionResult> GetReports([FromQuery] ReportCategory? category, [FromQuery] string? status)
        {
            var result = await _supportService.GetFilteredReportsAsync(category, status);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpPut(Router.SupportRouting.AdminUpdateReportStatus)]
        public async Task<IActionResult> UpdateReportStatus(Guid id, [FromBody] UpdateReportStatusDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _supportService.UpdateReportStatusAsync(id, dto.Status);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost(Router.SupportRouting.AdminTakeReportAction)]
        public async Task<IActionResult> TakeReportAction(Guid id, [FromBody] TakeReportActionDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var result = await _supportService.TakeActionOnReportAsync(id, dto);
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
                if (result.Data.IsNewChat)
                {
                    // New pending chat created, alert admins
                    await _supportHub.Clients.Group("Admins").SendAsync("NewPendingChatAlert");
                }
                else
                {
                    // For follow-up Pending messages or Active messages, fire both alerts.
                    await _supportHub.Clients.Group("Admins").SendAsync("NewPendingChatAlert");
                    await _supportHub.Clients.Group("Admins").SendAsync("ReceiveSupportMessage", result.Data.Message);
                }
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/claim")]
        public async Task<IActionResult> ClaimChat(Guid chatId)
        {
            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var result = await _supportService.ClaimChatAsync(chatId, adminIdString);
            
            if (result.StatusCode == System.Net.HttpStatusCode.OK)
            {
                // Notify other admins to remove from pending
                await _supportHub.Clients.Group("Admins").SendAsync("ChatClaimed", chatId);
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/reply")]
        public async Task<IActionResult> AdminReply(Guid chatId, [FromBody] SendMessageDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var result = await _supportService.AdminReplyAsync(chatId, adminIdString, dto);

            if (result.StatusCode == System.Net.HttpStatusCode.OK && result.Data != null)
            {
                // Notify the specific user based on UserId (CR-2 Option A)
                await _supportHub.Clients.User(result.Data.UserId).SendAsync("ReceiveSupportMessage", result.Data.Message);
            }

            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpPost(Router.SupportRouting.Prefix + "/chat/{chatId:guid}/close")]
        public async Task<IActionResult> CloseChat(Guid chatId)
        {
            var adminIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdString)) return Unauthorized();

            var result = await _supportService.CloseChatAsync(chatId, adminIdString);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpGet(Router.SupportRouting.Prefix + "/chat/pending")]
        public async Task<IActionResult> GetPendingChats([FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 10)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var result = await _supportService.GetPendingChatsAsync(pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
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
