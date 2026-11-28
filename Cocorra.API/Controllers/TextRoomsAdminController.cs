using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.TextRoomDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthPolicies.AdminOrModerator)]
    public sealed class TextRoomsAdminController : ControllerBase
    {
        private readonly ITextRoomAdminService _adminService;
        private readonly ITextRoomModerationService _moderationService;

        public TextRoomsAdminController(
            ITextRoomAdminService adminService,
            ITextRoomModerationService moderationService)
        {
            _adminService = adminService;
            _moderationService = moderationService;
        }

        [HttpGet(Router.TextRoomAdminRouting.Overview)]
        public async Task<IActionResult> GetOverview()
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.GetOverviewAsync(actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomAdminRouting.Presets)]
        public IActionResult GetPresets()
        {
            var presets = _adminService.GetPresets();
            var response = new Response<IReadOnlyList<TextRoomPresetDto>>(presets, "Operation Successful")
            {
                StatusCode = HttpStatusCode.OK
            };
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomAdminRouting.Settings)]
        public async Task<IActionResult> GetSettings()
        {
            var response = await _adminService.GetGlobalSettingsAsync();
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPut(Router.TextRoomAdminRouting.Settings)]
        [Authorize(Policy = AuthPolicies.AdminOnly)]
        public async Task<IActionResult> UpdateSettings([FromBody] TextRoomGlobalSettingsDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.UpdateGlobalSettingsAsync(dto, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomAdminRouting.ById)]
        public async Task<IActionResult> GetRoom([FromRoute] Guid roomId)
        {
            var response = await _adminService.GetRoomAsync(roomId);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.Create)]
        public async Task<IActionResult> CreateRoom([FromBody] TextRoomUpsertDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.CreateRoomAsync(dto, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPut(Router.TextRoomAdminRouting.ById)]
        public async Task<IActionResult> UpdateRoom([FromRoute] Guid roomId, [FromBody] TextRoomUpsertDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.UpdateRoomAsync(roomId, dto, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.Close)]
        public async Task<IActionResult> CloseRoom(
            [FromRoute] Guid roomId,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] StaffReasonDto? dto = null)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.CloseRoomAsync(roomId, dto?.Reason, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomAdminRouting.Schedules)]
        public async Task<IActionResult> GetSchedules()
        {
            var response = await _adminService.GetSchedulesAsync();
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.Schedules)]
        public async Task<IActionResult> CreateSchedule([FromBody] TextRoomScheduleUpsertDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.CreateScheduleAsync(dto, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPut(Router.TextRoomAdminRouting.ScheduleById)]
        public async Task<IActionResult> UpdateSchedule(
            [FromRoute] Guid scheduleId, [FromBody] TextRoomScheduleUpsertDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.UpdateScheduleAsync(scheduleId, dto, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpDelete(Router.TextRoomAdminRouting.ScheduleById)]
        public async Task<IActionResult> DeleteSchedule([FromRoute] Guid scheduleId)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.DeleteScheduleAsync(scheduleId, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPut(Router.TextRoomAdminRouting.ScheduleEnabled)]
        public async Task<IActionResult> SetScheduleEnabled(
            [FromRoute] Guid scheduleId, [FromBody] ToggleEnabledDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _adminService.SetScheduleEnabledAsync(scheduleId, dto.IsEnabled, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.DeleteMessage)]
        public async Task<IActionResult> DeleteMessage(
            [FromRoute] long messageId, [FromBody] StaffReasonDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _moderationService.DeleteMessageAsync(messageId, dto?.Reason, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.MuteUser)]
        public async Task<IActionResult> MuteUser(
            [FromRoute] Guid roomId, [FromRoute] Guid userId, [FromBody] TextRoomMuteDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _moderationService.MuteUserAsync(roomId, userId, dto.Minutes, dto.Reason, actor);
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpPost(Router.TextRoomAdminRouting.KickUser)]
        public async Task<IActionResult> KickUser(
            [FromRoute] Guid roomId, [FromRoute] Guid userId, [FromBody] StaffReasonDto dto)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var response = await _moderationService.KickUserAsync(roomId, userId, dto?.Reason, actor);
            return StatusCode((int)response.StatusCode, response);
        }
    }
}
