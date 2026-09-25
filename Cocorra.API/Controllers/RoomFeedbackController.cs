using Cocorra.BLL.Services.RoomFeedbackService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.RoomFeedbackDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize]
    public class RoomFeedbackController : ControllerBase
    {
        private readonly IRoomFeedbackService _feedbackService;

        public RoomFeedbackController(IRoomFeedbackService feedbackService)
        {
            _feedbackService = feedbackService;
        }

        [HttpPost(Router.RoomRouting.Feedback)]
        public async Task<IActionResult> Submit([FromRoute] Guid roomId, [FromBody] SubmitRoomFeedbackDto dto)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _feedbackService.SubmitFeedbackAsync(roomId, userId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpGet(Router.RoomRouting.FeedbackMe)]
        public async Task<IActionResult> GetMine([FromRoute] Guid roomId)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            var result = await _feedbackService.GetMyFeedbackStatusAsync(roomId, userId);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpGet(Router.RoomRouting.FeedbackSummary)]
        public async Task<IActionResult> GetSummary([FromRoute] Guid roomId)
        {
            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(userIdString, out Guid userId)) return Unauthorized();

            // Admin-or-host is decided in the service, which knows the room's host.
            var result = await _feedbackService.GetSummaryAsync(roomId, userId, User.IsInRole("Admin"));
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Roles = "Admin")]
        [HttpGet(Router.AdminRouting.Feedback)]
        public async Task<IActionResult> GetAll(
            [FromQuery] Guid? roomId,
            [FromQuery] int? rating,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, 50);

            var result = await _feedbackService.GetAdminFeedbackAsync(roomId, rating, pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
