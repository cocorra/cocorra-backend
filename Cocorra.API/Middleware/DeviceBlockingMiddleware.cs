using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Services.BlockedDevicesService;

namespace Cocorra.API.Middleware
{
    public class DeviceBlockingMiddleware
    {
        private readonly RequestDelegate _next;
        public DeviceBlockingMiddleware(RequestDelegate next)
        {
            _next = next;
        }
        public async Task InvokeAsync(HttpContext context, IBlockedDevicesService blockedDevicesService)
        {
            if (context.Request.Headers.TryGetValue("X-Device-Id", out var deviceId))
            {
                string deviceIdValue = deviceId.ToString();
                if (!string.IsNullOrWhiteSpace(deviceIdValue) && await blockedDevicesService.IsDeviceBlockedAsync(deviceIdValue))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                    context.Response.ContentType = "application/json";
                    var errorResponse = new
                    {
                        StatusCode = context.Response.StatusCode,
                        Message = "This device has been permanently blocked from accessing the system."
                    };

                    await context.Response.WriteAsJsonAsync(errorResponse);
                    return;
                }
            }

            await _next(context);
        }
    }
}