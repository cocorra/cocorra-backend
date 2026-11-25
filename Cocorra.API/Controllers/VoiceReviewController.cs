using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.API.Hubs;
using Cocorra.BLL.Services.VoiceReviewService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.VoiceReviewDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthPolicies.AdminOrRecordReviewer)]
    public class VoiceReviewController : ControllerBase
    {
        private const int MaxPageSize = 50;

        private readonly IVoiceReviewService _voiceReviewService;
        private readonly IStaffConnectionRevoker _staffConnectionRevoker;

        public VoiceReviewController(
            IVoiceReviewService voiceReviewService,
            IStaffConnectionRevoker staffConnectionRevoker)
        {
            _voiceReviewService = voiceReviewService;
            _staffConnectionRevoker = staffConnectionRevoker;
        }

        [HttpGet(Router.AdminRouting.VoiceReviewsPending)]
        public async Task<IActionResult> GetPending(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 20)
        {
            pageNumber = Math.Max(1, pageNumber);
            pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

            var result = await _voiceReviewService.GetPendingAsync(pageNumber, pageSize);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpGet(Router.AdminRouting.VoiceReviewVoiceUrl)]
        public async Task<IActionResult> GetVoiceUrl([FromRoute(Name = "id")] string reviewId)
        {
            var result = await _voiceReviewService.GetVoiceUrlAsync(reviewId);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPost(Router.AdminRouting.VoiceReviewDecision)]
        public async Task<IActionResult> Decide(
            [FromRoute(Name = "id")] string reviewId,
            [FromBody] VoiceReviewDecisionDto model)
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorUserId))
            {
                return BadRequest(new { succeeded = false, message = "Could not resolve the acting reviewer identity." });
            }

            var result = await _voiceReviewService.DecideAsync(reviewId, model, actorUserId);
            if (!result.Succeeded)
                return StatusCode((int)result.StatusCode, result);

            if (result.RevokedUserId.HasValue)
            {
                await _staffConnectionRevoker.RevokeAsync(result.RevokedUserId.Value);
            }

            return Ok(result);
        }
    }
}
