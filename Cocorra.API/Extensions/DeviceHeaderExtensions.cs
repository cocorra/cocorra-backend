using System;
using Cocorra.DAL.DTOS.Auth;
using Microsoft.AspNetCore.Http;

namespace Cocorra.API.Extensions
{
    /// <summary>
    /// Single place that knows the X-Device-* header contract shared by the mobile client,
    /// DeviceBlockingMiddleware (enforcement) and the auth endpoints (registration).
    /// </summary>
    public static class DeviceHeaderExtensions
    {
        public const string DeviceIdHeader = "X-Device-Id";
        public const string DeviceNameHeader = "X-Device-Name";
        public const string DeviceModelHeader = "X-Device-Model";
        public const string DeviceTypeHeader = "X-Device-Type";
        public const string DeviceOsHeader = "X-Device-Os";

        /// <summary>Longest device id we will store. Anything longer is a client bug or an attack.</summary>
        private const int MaxDeviceIdLength = 200;
        private const int MaxMetadataLength = 100;

        /// <summary>
        /// Builds a device descriptor from the request headers, or null when the client sent
        /// no usable X-Device-Id. Callers treat null as "nothing to register" — never as an error,
        /// since older app builds don't send these headers at all.
        /// </summary>
        public static DeviceInfoDto? GetDeviceInfo(this HttpRequest request)
        {
            var deviceId = Read(request, DeviceIdHeader, MaxDeviceIdLength);
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            return new DeviceInfoDto
            {
                DeviceId = deviceId,
                DeviceName = Read(request, DeviceNameHeader, MaxMetadataLength),
                DeviceModel = Read(request, DeviceModelHeader, MaxMetadataLength),
                DeviceType = Read(request, DeviceTypeHeader, MaxMetadataLength),
                DeviceOs = Read(request, DeviceOsHeader, MaxMetadataLength)
            };
        }

        private static string? Read(HttpRequest request, string header, int maxLength)
        {
            if (!request.Headers.TryGetValue(header, out var values))
                return null;

            var value = values.ToString().Trim();
            if (string.IsNullOrEmpty(value))
                return null;

            return value.Length > maxLength ? value.Substring(0, maxLength) : value;
        }
    }
}
