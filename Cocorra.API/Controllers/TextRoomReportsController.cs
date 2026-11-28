using System;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.ReportDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    public class TextRoomReportsController : ControllerBase
    {
        private readonly ITextRoomReportService _reportService;

        public TextRoomReportsController(ITextRoomReportService reportService)
        {
            _reportService = reportService;
        }

        [Authorize]
        [HttpPost(Router.TextRoomRouting.ReportMessage)]
        public async Task<IActionResult> ReportMessage([FromRoute] long messageId, [FromBody] ReportTextRoomMessageDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (!User.TryGetUserId(out var reporterId))
                return Unauthorized();

            var result = await _reportService.ReportMessageAsync(reporterId, messageId, dto);
            return StatusCode((int)result.StatusCode, result);
        }

        [Authorize(Policy = AuthPolicies.AdminOrModerator)]
        [HttpGet(Router.SupportRouting.AdminReportMessageContext)]
        public async Task<IActionResult> GetReportMessageContext([FromRoute] Guid id)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _reportService.GetReportContextAsync(id, actor);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
