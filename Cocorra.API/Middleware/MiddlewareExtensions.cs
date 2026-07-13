using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.API.Middleware
{
    public static class MiddlewareExtensions
    {
        public static IApplicationBuilder UseDeviceBlocking(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<DeviceBlockingMiddleware>();
        }

        public static IApplicationBuilder UseSessionTracking(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<SessionTrackingMiddleware>();
        }
    }
}