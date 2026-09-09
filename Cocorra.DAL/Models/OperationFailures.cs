namespace Cocorra.DAL.Models
{
    /// <summary>
    /// AN-041 — closed vocabularies for <see cref="EventTypes.OperationFailed"/>.
    ///
    /// These exist as constants rather than free strings for one reason: an open vocabulary is
    /// how a narrowly scoped failure event turns into an unqueryable error log. Every value a
    /// consumer can encounter is declared here, so a dashboard can enumerate the reasons without
    /// discovering new ones in production, and adding a value is a deliberate act with a
    /// documentation obligation attached.
    ///
    /// <para>
    /// <b>Never put an exception message in this event.</b> Reason codes are drawn from this
    /// file only. Exception text is written by developers for developers, is not a closed set,
    /// and can carry user data into an analytics table with a 180-day retention.
    /// </para>
    /// </summary>
    public static class TrackedOperations
    {
        /// <summary>Connecting to a live room over the hub, after the REST-side join.</summary>
        public const string RoomJoin = "room_join";

        /// <summary>A host promoting a participant to the stage.</summary>
        public const string StagePromotion = "stage_promotion";
    }

    /// <summary>
    /// Why a tracked operation did not complete. Closed set — see <see cref="TrackedOperations"/>.
    /// </summary>
    public static class OperationFailureReasons
    {
        /// <summary>The room is scheduled, ended, or missing. The user arrived at the wrong time.</summary>
        public const string RoomNotLive = "room_not_live";

        /// <summary>
        /// No RoomParticipant row: the client reached the hub without completing the REST join.
        /// The one reason in this set that indicates a client-side sequencing bug rather than a
        /// product state, which is exactly why it is worth being able to count.
        /// </summary>
        public const string NotAParticipant = "not_a_participant";

        /// <summary>Waiting on the host to approve the join request. A product state, not an error.</summary>
        public const string PendingHostApproval = "pending_host_approval";

        /// <summary>Kicked or rejected. An enforcement outcome.</summary>
        public const string BlockedFromRoom = "blocked_from_room";

        /// <summary>
        /// The stage was already at <c>Room.StageCapacity</c>. Distinguishes "the host ignored
        /// the raised hand" from "the host could not act on it", which are different products
        /// problems with different fixes.
        /// </summary>
        public const string StageAtCapacity = "stage_at_capacity";
    }

    /// <summary>
    /// Why a room stopped being Live — the <c>endReason</c> property of
    /// <see cref="EventTypes.RoomEnded"/>. Closed set, for the same reason as
    /// <see cref="OperationFailureReasons"/>.
    ///
    /// <para>
    /// This field previously carried the literal "host_ended" at its single call site, so a room
    /// killed by a dropped socket was indistinguishable from one the coach chose to finish. Every
    /// duration and completion metric read from this event inherited that conflation, and the
    /// rate of accidental endings could not be measured at all — the data said it never happened.
    /// </para>
    /// </summary>
    public static class RoomEndReasons
    {
        /// <summary>The host deliberately ended the room, via the hub or the REST endpoint.</summary>
        public const string HostEnded = "host_ended";

        /// <summary>
        /// The host's connection dropped and did not come back inside the grace window, so
        /// HostReconnectGraceService ended the room. Distinct from <see cref="HostEnded"/>: this
        /// one is a session the coach did not choose to finish, and a rising count is a
        /// connectivity problem, not a usage pattern.
        /// </summary>
        public const string HostDisconnected = "host_disconnected";

        /// <summary>
        /// The host deleted their account, so their rooms were closed with them. Neither a
        /// choice to finish the session nor a connectivity failure, and counting it as either
        /// would misattribute the cause.
        /// </summary>
        public const string HostAccountDeleted = "host_account_deleted";
    }
}
