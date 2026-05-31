using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models
{
    [Index(nameof(BlockerId), nameof(BlockedId), IsUnique = true)]
    public class UserBlock : BaseEntity
    {
        public Guid BlockerId { get; set; }
        [ForeignKey(nameof(BlockerId))]
        public virtual ApplicationUser? Blocker { get; set; }

        public Guid BlockedId { get; set; }
        [ForeignKey(nameof(BlockedId))]
        public virtual ApplicationUser? Blocked { get; set; }

        public Guid? BlockedDeviceId { get; set; }
        [ForeignKey(nameof(BlockedDeviceId))]
        public virtual BlockedDevices? BlockedDevice { get; set; }

    }
}
