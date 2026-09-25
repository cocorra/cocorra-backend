namespace Cocorra.DAL.DTOS.RoomInviteDto
{
    /// <summary>
    /// Lifecycle counts come from the RoomInvites table and are authoritative. The three
    /// event counts come from the analytics event store and are null when new-event emission
    /// is switched off, since zero would then be indistinguishable from "nobody did it".
    /// </summary>
    public class RoomInviteStatsDto
    {
        public int TotalCreated { get; set; }
        public int Active { get; set; }
        public int Used { get; set; }
        public int Expired { get; set; }
        public int Revoked { get; set; }

        /// <summary>Equals <see cref="Used"/>: invites are single-use, one Used row per join.</summary>
        public int SuccessfulAcceptances { get; set; }

        public int UniqueAcceptedUsers { get; set; }

        public bool EventCountsAvailable { get; set; }
        public long? Resolved { get; set; }
        public long? AcceptAttempts { get; set; }
        public long? FailedAcceptances { get; set; }
    }
}
