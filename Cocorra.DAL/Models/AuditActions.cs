using System.Collections.Generic;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// The closed set of values stored in <see cref="AuditLog.Action"/>. The admin dashboard
    /// filters on these exact strings, so renaming one is an API break.
    /// </summary>
    public static class AuditActions
    {
        public const string UserStatusChanged = "User.StatusChanged";
        public const string UserDeviceAndEmailBlocked = "User.DeviceAndEmailBlocked";
        public const string UserRolesChanged = "User.RolesChanged";
        public const string UserCreatedWithRole = "User.CreatedWithRole";
        public const string NotificationBroadcastSent = "Notification.BroadcastSent";
        public const string RoleDisplayNameChanged = "Role.DisplayNameChanged";
        // A presigned URL to a private file was handed to a staff member. Details list each file.
        public const string VoiceUrlIssued = "Storage.VoiceUrlIssued";
        public const string ScreenshotUrlIssued = "Storage.ScreenshotUrlIssued";

        public static readonly IReadOnlySet<string> All = new HashSet<string>
        {
            UserStatusChanged,
            UserDeviceAndEmailBlocked,
            UserRolesChanged,
            UserCreatedWithRole,
            NotificationBroadcastSent,
            RoleDisplayNameChanged,
            VoiceUrlIssued,
            ScreenshotUrlIssued
        };
    }
}
