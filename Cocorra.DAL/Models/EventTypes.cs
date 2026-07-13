namespace Cocorra.DAL.Models
{
    public static class EventTypes
    {
        public const string UserRegistered             = "user_registered";
        public const string EmailConfirmed              = "email_confirmed";
        public const string VoiceVerificationSubmitted = "voice_verification_submitted";
        public const string VoiceVerificationResult    = "voice_verification_result";
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
        public const string MessageSent                 = "message_sent";
        public const string FriendRequestSent           = "friend_request_sent";
        public const string FriendRequestAccepted       = "friend_request_accepted";
        public const string SessionStarted              = "session_started";
        public const string FeatureViewed               = "feature_viewed";
        public const string UserReported                = "user_reported";
        public const string UserBlocked                 = "user_blocked";
        public const string AccountDeleted              = "account_deleted";
        public const string NotificationOpened          = "notification_opened";
    }
}
