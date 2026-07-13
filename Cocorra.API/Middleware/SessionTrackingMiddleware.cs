using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace Cocorra.API.Middleware
{
    public class SessionTrackingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IMemoryCache _cache;

        public SessionTrackingMiddleware(RequestDelegate next, IMemoryCache cache)
        {
            _next = next;
            _cache = cache;
        }

        public async Task InvokeAsync(HttpContext context, IEventTracker tracker)
        {
            const string SessionCookieName = "CocorraSessionId";
            if (!context.Request.Cookies.TryGetValue(SessionCookieName, out var sessionIdStr) || !Guid.TryParse(sessionIdStr, out var sessionId))
            {
                sessionId = Guid.NewGuid();
                context.Response.Cookies.Append(SessionCookieName, sessionId.ToString(), new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Strict,
                    Expires = DateTimeOffset.UtcNow.AddDays(7)
                });
            }

            context.Items["SessionId"] = sessionId;

            // Run authentication / rest of pipeline first so context.User is populated
            await _next(context);

            // Log session_started once per session ID if authenticated
            if (context.User.Identity?.IsAuthenticated == true)
            {
                var userIdString = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (Guid.TryParse(userIdString, out var userId))
                {
                    var cacheKey = $"session_logged:{sessionId}";
                    if (!_cache.TryGetValue(cacheKey, out _))
                    {
                        _cache.Set(cacheKey, true, TimeSpan.FromDays(1)); // Session lifetime
                        
                        tracker.Track(EventTypes.SessionStarted, userId, new
                        {
                            sessionId = sessionId
                        });
                    }
                }
            }
        }
    }
}
