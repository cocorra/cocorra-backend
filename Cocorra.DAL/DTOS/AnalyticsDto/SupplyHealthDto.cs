namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// AN-020 / B-1: how many distinct hosts actually ran rooms per period.
    /// Cocorra's leading indicator — no rooms means no product, whatever demand does.
    /// </summary>
    public class ActiveHostsDataPointDto
    {
        public string Period { get; set; } = string.Empty;
        public int DistinctHosts { get; set; }
        public int RoomsCreated { get; set; }
        public int RoomsGoneLive { get; set; }
    }

    /// <summary>AN-020 / B-2: does a host who runs one room run another?</summary>
    public class HostRetentionDto
    {
        /// <summary>Hosts whose first room falls in the window.</summary>
        public int NewHosts { get; set; }

        /// <summary>Of those, how many created a second room at any later point.</summary>
        public int HostsWithSecondRoom { get; set; }

        public double SecondRoomRatePercent { get; set; }

        /// <summary>Median days between a host's first and second room. Null if none returned.</summary>
        public double? MedianDaysToSecondRoom { get; set; }
    }

    /// <summary>
    /// AN-020 / B-2: concentration of supply. A high share carried by a handful of hosts is a
    /// key-person risk that a total host count hides entirely.
    /// </summary>
    public class HostConcentrationDto
    {
        public int TotalHostsInWindow { get; set; }
        public int TotalRoomsInWindow { get; set; }

        /// <summary>Share of all rooms run by the busiest host.</summary>
        public double TopHostSharePercent { get; set; }

        /// <summary>Share of all rooms run by the top 3 hosts.</summary>
        public double Top3SharePercent { get; set; }

        /// <summary>
        /// Hosts needed to cover half of all rooms. A value of 1 or 2 means the platform's
        /// supply rests on that many people.
        /// </summary>
        public int HostsCoveringHalfOfRooms { get; set; }
    }

    /// <summary>
    /// AN-020 / B-3: when rooms actually run, in UTC, with the offset a reader should apply.
    /// </summary>
    public class HostSchedulePointDto
    {
        public int HourUtc { get; set; }
        public int RoomsStarted { get; set; }
    }

    public class SupplyHealthDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public string Granularity { get; set; } = string.Empty;

        public IEnumerable<ActiveHostsDataPointDto> ActiveHosts { get; set; } = [];
        public HostRetentionDto HostRetention { get; set; } = new();
        public HostConcentrationDto Concentration { get; set; } = new();
        public IEnumerable<HostSchedulePointDto> ScheduleByHourUtc { get; set; } = [];

        /// <summary>
        /// Minutes to add to the UTC hours above for the platform's predominant local time
        /// (UTC+3). Returned rather than applied server-side so the caller decides how to
        /// present it and the underlying figure stays unambiguous.
        /// </summary>
        public int SuggestedDisplayOffsetMinutes { get; set; } = 180;

        /// <summary>
        /// Earliest room creation backing this series. Rooms are relational and never purged,
        /// so unlike the event-derived metrics this reaches back to the platform's first day.
        /// </summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
