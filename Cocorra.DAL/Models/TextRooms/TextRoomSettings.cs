namespace Cocorra.DAL.Models
{
    /// <summary>
    /// Every tunable rule of a text room. Owned by <see cref="TextRoom"/>, <see cref="TextRoomSchedule"/>
    /// (copied onto each room it spawns) and <see cref="TextRoomGlobalSettings"/> (dashboard defaults),
    /// so the three can never drift apart in shape.
    /// </summary>
    public class TextRoomSettings
    {
        /// <summary>True: open 24/7 until closed by staff. False: closes <see cref="DurationMinutes"/> after opening.</summary>
        public bool IsAlwaysOn { get; set; }

        public int? DurationMinutes { get; set; } = 60;

        /// <summary>Always-on rooms only. Null disables rotation.</summary>
        public int? RotateTopicEveryMinutes { get; set; }

        /// <summary>Hides earlier messages from clients on rotation. Rows stay until retention purges them.</summary>
        public bool ClearHistoryOnRotate { get; set; }

        /// <summary>Null means unlimited.</summary>
        public int? MaxParticipants { get; set; }

        public bool AutoOverflowEnabled { get; set; }

        /// <summary>Online count at which new joiners go to an overflow instance. Null falls back to <see cref="MaxParticipants"/>.</summary>
        public int? OverflowTriggerCount { get; set; }

        /// <summary>Null means up to <c>TextRoomLimits.HardMaxOverflowInstances</c>.</summary>
        public int? MaxOverflowInstances { get; set; }

        /// <summary>Staff always see counts; regular users see them only when this is true.</summary>
        public bool ShowOnlineCountToUsers { get; set; }

        public bool AllowPrivateChatFromRoom { get; set; } = true;

        /// <summary>Per-user cooldown between messages. 0 disables.</summary>
        public int SlowModeSeconds { get; set; }

        public int MaxMessageLength { get; set; } = 500;

        /// <summary>Un-reported messages older than this are purged. Null uses the global default.</summary>
        public int? MessageRetentionHours { get; set; }

        /// <summary>Null uses the global default.</summary>
        public int? ReportContextMessagesBefore { get; set; }

        /// <summary>Null uses the global default.</summary>
        public int? ReportContextMessagesAfter { get; set; }

        public TextRoomSettings Clone() => (TextRoomSettings)MemberwiseClone();
    }
}
