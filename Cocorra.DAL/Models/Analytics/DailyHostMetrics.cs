using System;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models.Analytics
{
    /// <summary>
    /// RM-3: Daily host supply metrics retained indefinitely.
    /// Grain: One row per (Date, HostId).
    /// </summary>
    public class DailyHostMetrics
    {
        public long Id { get; set; }

        [Column(TypeName = "date")]
        public DateTime Date { get; set; }

        public Guid HostId { get; set; }

        public int RoomsCreated { get; set; }
        public int RoomsGoneLive { get; set; }
        public int TotalJoinersAcrossRooms { get; set; }
        public int ReportsAboutHostRooms { get; set; }

        [Column(TypeName = "datetime2")]
        public DateTime ComputedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
