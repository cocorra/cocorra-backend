using System;
using System.Collections.Generic;
using System.Security.Claims;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize]
    public class EventsController : ControllerBase
    {
        private readonly IEventTracker _eventTracker;

        /// <summary>
        /// Events a client is permitted to emit. Server-owned lifecycle events
        /// (activation_completed, user_registered, room_created, …) are NOT here — those
        /// are fired server-side after the real action, so clients can't forge the funnel.
        /// </summary>
        private static readonly HashSet<string> ClientAllowedEvents = new()
        {
            EventTypes.RoomCreateStarted,
            EventTypes.NotificationOpened,
            EventTypes.FeatureViewed,
        };

        public EventsController(IEventTracker eventTracker)
        {
            _eventTracker = eventTracker;
        }

        /// <summary>
        /// Track client-side user events (e.g. room_create_started, notification_opened, feature_viewed).
        /// </summary>
        [HttpPost("api/events/track")]
        public IActionResult Track([FromBody] TrackEventDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.EventType))
            {
                return BadRequest(new { succeeded = false, message = "EventType is required." });
            }

            if (!ClientAllowedEvents.Contains(dto.EventType))
            {
                return BadRequest(new { succeeded = false, message = "EventType is not permitted from clients." });
            }

            var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            Guid? userId = Guid.TryParse(userIdString, out var guid) ? guid : null;

            _eventTracker.Track(dto.EventType, userId, dto.Properties);

            return Ok(new { succeeded = true });
        }
    }

    public class TrackEventDto
    {
        public string EventType { get; set; } = string.Empty;
        public object? Properties { get; set; }
    }
}
