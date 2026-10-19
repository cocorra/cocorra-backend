using Cocorra.BLL.Services.NotificationService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.NotificationDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    // Admin only: a broadcast reaches every active user, which is beyond a moderator's remit.
    [Authorize(Roles = "Admin")]
    public class AdminNotificationsController : ControllerBase
    {
        private readonly IBroadcastNotificationService _broadcastService;

        public AdminNotificationsController(IBroadcastNotificationService broadcastService)
        {
            _broadcastService = broadcastService;
        }

        [HttpPost(Router.AdminRouting.BroadcastNotification)]
        public async Task<IActionResult> Broadcast([FromBody] BroadcastNotificationDto model)
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminId))
                return Unauthorized();

            var result = await _broadcastService.BroadcastAsync(model, adminId);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
