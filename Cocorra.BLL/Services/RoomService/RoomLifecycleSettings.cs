namespace Cocorra.BLL.Services.RoomService
{
    /// <summary>
    /// Bound from the "RoomLifecycle" configuration section.
    /// </summary>
    public class RoomLifecycleSettings
    {
        public const string SectionName = "RoomLifecycle";

        /// <summary>
        /// How long a Live room is held open after the host's connection drops, before it is
        /// ended for everyone.
        ///
        /// <para>
        /// 90 seconds by default. SignalR's automatic-reconnect ladder retries at 0, 2, 10 and
        /// 30 seconds, so its final attempt begins at 42 — anything shorter than that ends the
        /// room while the client is still legitimately trying to come back. The rest is margin
        /// for the blips that actually happen on a phone: a lift, a tunnel, a Wi-Fi to cellular
        /// handoff. Much longer and the audience sits in silence rather than leaving.
        /// </para>
        /// </summary>
        public int HostReconnectGraceSeconds { get; set; } = 90;

        /// <summary>
        /// How often the grace window is swept. The real end time is therefore
        /// HostReconnectGraceSeconds plus up to one interval, which is why the interval is
        /// short relative to the window.
        /// </summary>
        public int GraceSweepIntervalSeconds { get; set; } = 10;

        /// <summary>
        /// How long a room may overrun its booked DurationHours before it is closed for it.
        ///
        /// <para>
        /// 15 minutes by default. Cutting a coaching session off at exactly the two-hour mark
        /// would end it mid-sentence, and hosts routinely need a moment to wrap up; this is the
        /// wrap-up allowance. It is a bounded one, though — the hard ceiling this produces
        /// (3h booked + 15m = 3h15m) has to stay comfortably under
        /// LiveKitSettings.TokenTtlMinutes, or participants start being unable to reconnect
        /// before the room ends.
        /// </para>
        /// </summary>
        public int RoomOvertimeGraceMinutes { get; set; } = 15;

        /// <summary>
        /// How often rooms are checked against their duration. A minute is ample precision for
        /// a deadline measured in hours, and keeps the query off the hot path of the 10-second
        /// host-reconnect sweep.
        /// </summary>
        public int DurationSweepIntervalSeconds { get; set; } = 60;
    }
}
