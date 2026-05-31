using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Models
{
    public class BlockedDevices : BaseEntity
    {
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }   
        public string? DeviceModel { get; set; }   
        public string? DeviceType { get; set; }   
        public string? DeviceOs { get; set; }
        public bool IsBlocked { get; set; } = true;
        public Guid ApplicationUserId { get; set; }   
        public ApplicationUser? ApplicationUser { get; set; }     
        public virtual ICollection<UserBlock>? UserBlocks { get; set; }
    }
}