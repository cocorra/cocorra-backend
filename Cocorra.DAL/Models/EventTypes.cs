namespace Cocorra.DAL.Models
{
    public static class EventTypes
    {
        public const string UserRegistered             = "user_registered";
        public const string EmailConfirmed              = "email_confirmed";
        public const string VoiceVerificationSubmitted = "voice_verification_submitted";
        public const string VoiceVerificationResult    = "voice_verification_result";

        /// <summary>
        /// AN-011. ApplicationUser has no UpdatedAt and there is no status-history table, so
        /// this event is the ONLY durable record that a status transition happened, who made it,
        /// and what the previous value was. Reverting its emission loses those transitions
        /// permanently.
        /// </summary>
        public const string UserStatusChanged          = "user_status_changed";
        public const string MbtiSubmitted               = "mbti_submitted";
        public const string ActivationCompleted         = "activation_completed";
        public const string RoomCreateStarted           = "room_create_started";
        public const string RoomCreated                 = "room_created";
        public const string RoomJoinRequested           = "room_join_requested";
        public const string RoomJoinApproved            = "room_join_approved";
        public const string RoomJoined                  = "room_joined";
        public const string RoomLeft                    = "room_left";
        public const string MicActivated                = "mic_activated";
        public const string SpeakingTimeLogged          = "speaking_time_logged";
        public const string RoomEnded                   = "room_ended";

        // ── AN-017: P0 core-loop events, low-frequency increment ────────────
        // Emitted behind Analytics:EnableNewEventEmission. Low frequency means these fire on
        // host actions, not on every participant interaction, so they add little channel load.

        /// <summary>
        /// A room actually went live, as opposed to being scheduled. Emitted from BOTH start
        /// paths: create-as-live and start-a-scheduled-room.
        /// </summary>
        public const string RoomWentLive                = "room_went_live";

        /// <summary>
        /// A participant was promoted to the stage. UserId is the PROMOTED PARTICIPANT, not the
        /// host who approved them — deliberately the opposite convention to room_join_approved
        /// (tracked against the host at RoomService.cs:311), because the stage funnel measures
        /// what happened to the listener. Following the older precedent here would break M-400
        /// in a way no metric test would catch.
        /// </summary>
        public const string StagePromoted               = "stage_promoted";

        /// <summary>A participant was moved back to the audience. UserId is the participant.</summary>
        public const string StageDemoted                = "stage_demoted";

        /// <summary>A participant was removed by the host. UserId is the removed participant.</summary>
        public const string ParticipantKicked           = "participant_kicked";

        /// <summary>The host granted a speaker additional time.</summary>
        public const string SpeakerTimeExtended         = "speaker_time_extended";

        /// <summary>
        /// A speaker's allotted time ran out. Emitted BEFORE the throw that rejects the unmute,
        /// because the rejection IS the fact being recorded — if it were emitted afterwards it
        /// would never fire at all.
        /// </summary>
        public const string SpeakerTimeExhausted        = "speaker_time_exhausted";

        // ── AN-018: P0 core-loop events, high-frequency increment ───────────
        // Separately flagged behind Analytics:EnableHighFrequencyEvents. These scale with
        // engagement and land hardest on the busiest rooms, so they must be revertible without
        // touching the AN-017 increment.

        /// <summary>A participant asked for the stage. The demand side of the stage funnel.</summary>
        public const string HandRaised                  = "hand_raised";

        /// <summary>
        /// A hand went down. wasApproved distinguishes "got the stage" from "gave up waiting" —
        /// the same state change with opposite meanings, and the whole reason this event is not
        /// just the inverse of hand_raised.
        /// </summary>
        public const string HandLowered                 = "hand_lowered";

        /// <summary>
        /// A speaker's mic closed, carrying the segment duration. Emitted from ALL THREE close
        /// sites (self-mute, demotion, kick); emitting from only the first would silently
        /// under-count every segment that ended because someone else acted.
        /// </summary>
        public const string MicDeactivated              = "mic_deactivated";
        public const string MessageSent                 = "message_sent";
        public const string FriendRequestSent           = "friend_request_sent";
        public const string FriendRequestAccepted       = "friend_request_accepted";
        public const string SessionStarted              = "session_started";
        public const string FeatureViewed               = "feature_viewed";
        public const string UserReported                = "user_reported";
        public const string UserBlocked                 = "user_blocked";
        public const string AccountDeleted              = "account_deleted";
        public const string NotificationOpened          = "notification_opened";

        // ── AN-024: push delivery outcome ───────────────────────────────────
        // Commit dc1c933 fixed reversed FCM delivery. An identical regression today would be
        // completely invisible: the FCM response is logged and discarded, with no counter and
        // no queryable record. These two events are a regression guard for a defect class that
        // has already occurred once.

        /// <summary>Emitted immediately before the FCM call.</summary>
        public const string PushSendAttempted           = "push_send_attempted";

        /// <summary>
        /// Emitted after the FCM call returns or throws. Correlates with the attempt via
        /// CorrelationId, so attempted-vs-result counts reconcile and a silent hang is visible
        /// as attempts without results.
        /// </summary>
        public const string PushSendResult              = "push_send_result";

        // ── P2 additions ────────────────────────────────────────────────────

        /// <summary>
        /// AN-026. A reminder was set or cleared. RoomReminder rows are DELETED on un-toggle,
        /// so the relational table only ever shows reminders that still stand — reminder-to-join
        /// conversion computed from it reads optimistically, counting only the people who never
        /// changed their mind. The event is the only record that a reminder was withdrawn.
        /// </summary>
        public const string RoomReminderToggled         = "room_reminder_toggled";

        /// <summary>
        /// AN-034. A moderation decision was taken on a report. Enforcement outcomes are
        /// currently unrecorded: Report.Status moves to Resolved or Rejected with no note of
        /// what action followed, so "we acted on it" and "we dismissed it" look identical.
        /// </summary>
        public const string ModerationActionTaken       = "moderation_action_taken";

        // ── P3: failure paths and media telemetry ───────────────────────────

        /// <summary>
        /// AN-041. A user-facing operation failed. Cocorra has no error tracking of any kind,
        /// so every funnel currently measures only the happy path: a drop-off caused by a bug
        /// is indistinguishable from a user changing their mind.
        /// </summary>
        public const string OperationFailed             = "operation_failed";

        /// <summary>
        /// AN-040. A LiveKit room lifecycle or participant event, ingested from the webhook.
        /// Zero media telemetry exists today, so a room that failed to connect looks exactly
        /// like a room nobody attended.
        /// </summary>
        public const string MediaSessionEvent           = "media_session_event";
    }
}
