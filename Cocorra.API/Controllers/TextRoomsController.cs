using System;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize]
    public sealed class TextRoomsController : ControllerBase
    {
        private readonly ITextRoomService _textRoomService;

        public TextRoomsController(ITextRoomService textRoomService)
        {
            _textRoomService = textRoomService;
        }

        [HttpGet(Router.TextRoomRouting.List)]
        public async Task<IActionResult> GetActiveRooms()
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            var response = await _textRoomService.GetActiveRoomsAsync(userId, User.IsTextRoomStaff());
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomRouting.ById)]
        public async Task<IActionResult> GetRoom([FromRoute] Guid roomId)
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            var response = await _textRoomService.GetRoomConfigAsync(roomId, userId, User.IsTextRoomStaff());
            return StatusCode((int)response.StatusCode, response);
        }

        [HttpGet(Router.TextRoomRouting.Messages)]
        public async Task<IActionResult> GetMessages(
            [FromRoute] Guid roomId,
            [FromQuery] long? beforeId = null,
            [FromQuery] int limit = TextRoomLimits.HistoryPageDefault)
        {
            if (!User.TryGetUserId(out var userId))
                return Unauthorized();

            var response = await _textRoomService.GetMessagesAsync(roomId, userId, beforeId, limit);
            return StatusCode((int)response.StatusCode, response);
        }
    }
}
