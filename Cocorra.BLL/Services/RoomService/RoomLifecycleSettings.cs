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
    }
}
