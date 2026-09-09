using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// Doubles as the device registry and the device blocklist.
    /// A row with IsBlocked=false is a device we have seen this user authenticate from
    /// (written on login/refresh); IsBlocked=true is a device an admin has banned.
    /// Enforcement (DeviceBlockingMiddleware) only ever matches on IsBlocked=true, so
    /// registry rows are inert until a block promotes them.
    /// </summary>
    public class BlockedDevices : BaseEntity
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? DeviceModel { get; set; }
        public string? DeviceType { get; set; }
        public string? DeviceOs { get; set; }

        /// <summary>
        /// Defaults to false: rows are now created on ordinary login, so a forgotten
        /// assignment must fail towards "not blocked" rather than locking out an innocent user.
        /// Every blocking path sets this explicitly.
        /// </summary>
        public bool IsBlocked { get; set; } = false;

        /// <summary>When the block was applied. Null while the row is only a registration.</summary>
        public DateTime? BlockedAt { get; set; }

        /// <summary>Last time this user authenticated from this device. Refreshed on login/refresh.</summary>
        public DateTime? LastSeenAt { get; set; }

        public Guid ApplicationUserId { get; set; }
        public ApplicationUser? ApplicationUser { get; set; }
        public virtual ICollection<UserBlock>? UserBlocks { get; set; }
    }
}
