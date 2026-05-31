using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.DTOS.BlockedDevicesDto
{
    public class BlockedDevicesDto
    {
        public string DeviceId { get; set; } = null!;
        public string DeviceName { get; set; } = null!;
        public string DeviceModel { get; set; } = null!;
        public string DeviceType { get; set; } = null!;
        public string DeviceOs { get; set; } = null!;
        public Guid ApplicationUserId { get; set; }
        public DateTime BlockedAt { get; set; }
    }
}