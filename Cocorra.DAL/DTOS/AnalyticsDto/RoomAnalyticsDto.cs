namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public class RoomCategoryStatDto
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class TopRoomDto
    {
        public Guid RoomId { get; set; }
        public string RoomTitle { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int ParticipantCount { get; set; }

        // TRUST-09: DurationHours removed. See the note on RoomAnalyticsDto below — the field
        // read Room.DurationHours, which is a host-typed scheduling input, not an observed
        // duration.
    }

    public class RoomAnalyticsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalRooms { get; set; }
        public int ScheduledRooms { get; set; }
        public int ActiveRooms { get; set; }
        public int EndedRooms { get; set; }

        public int PrivateRooms { get; set; }
        public int PublicRooms { get; set; }

        /// <summary>
        /// M-205. Rooms created in the window that reached any status other than Scheduled.
        ///
        /// Added explicitly rather than left for the caller to derive from
        /// <see cref="ActiveRooms"/> + <see cref="EndedRooms"/>. This response declares M-205 in
        /// its trust envelope, and a metric key that names a figure the payload does not actually
        /// contain is the defect class that produced the M-200 mis-attachment: the contract was
        /// well-formed and simply about something else.
        /// </summary>
        public int RoomsGoneLive { get; set; }

        public double AvgParticipantsPerRoom { get; set; }

        // ── TRUST-09: AvgDurationHours removed, not zeroed ───────────────────
        //
        // The field averaged Room.DurationHours, which is `public int DurationHours { get; set; }
        // = 2;` — a host-configured SCHEDULING field with a default, not a measurement. It never
        // described how long a room actually ran; it described the number hosts typed into a
        // form, and because most accept the default it sat close to a constant 2.0 while being
        // presented as "average room duration".
        //
        // Removed rather than caveated, following AN-005's treatment of TopSpeakers and for the
        // same reason: a warned-but-visible wrong number still gets screenshotted into a deck
        // without its warning, so server-side removal is the only enforcement that survives
        // contact with real users. Required by the Definition of Done and by frontend contract
        // test 7 in 20-dashboard-implementation-blueprint.md.
        //
        // The honest replacement is `room_ended.actualDurationSeconds` (AN-019), measured against
        // room_went_live rather than the scheduled StartDate. It is behind
        // Analytics:EnableNewEventEmission and therefore has no history yet, which is why nothing
        // is returned here in the meantime: absent is correct, 0 or 2.0 would not be.

        public IEnumerable<RoomCategoryStatDto> RoomsByCategory { get; set; } = [];

        /// <summary>Top 10 rooms by participant count in the period.</summary>
        public IEnumerable<TopRoomDto> TopRooms { get; set; } = [];
    }
}
