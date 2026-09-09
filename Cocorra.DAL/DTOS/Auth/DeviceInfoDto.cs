namespace Cocorra.DAL.DTOS.Auth
{
    /// <summary>
    /// Device descriptor read from the X-Device-* request headers at login/refresh.
    /// Only DeviceId matters for enforcement; the rest is operator-facing metadata so an
    /// admin can tell "Pixel 7" from "iPhone 14" when reviewing a user's devices.
    /// Self-reported by the client, so treat it as a hint, not an attestation.
    /// </summary>
    public class DeviceInfoDto
    {
        public string DeviceId { get; set; } = null!;
        public string? DeviceName { get; set; }
        public string? DeviceModel { get; set; }
        public string? DeviceType { get; set; }
        public string? DeviceOs { get; set; }
    }
}
