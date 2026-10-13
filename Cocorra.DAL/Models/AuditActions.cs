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

        public static readonly IReadOnlySet<string> All = new HashSet<string>
        {
            UserStatusChanged,
            UserDeviceAndEmailBlocked,
            UserRolesChanged,
            UserCreatedWithRole,
            NotificationBroadcastSent
        };
    }
}
