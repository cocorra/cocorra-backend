namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// AN-029 / M-701: social graph health. Reciprocity before volume — an accepted request is
    /// a connection, a sent one is only an attempt, and reporting sends as "social activity"
    /// would let a spam wave read as engagement.
    /// </summary>
    public class SocialGraphDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int FriendRequestsSent { get; set; }
        public int FriendRequestsAccepted { get; set; }
        public double? AcceptanceRatePercent { get; set; }
        public double? MedianHoursToAccept { get; set; }

        public int DistinctSenders { get; set; }

        /// <summary>
        /// Requests sent by the single most active sender. A high share here means the
        /// acceptance rate is describing one person's behaviour, not the platform's.
        /// </summary>
        public int MaxRequestsBySingleSender { get; set; }

        public int MessagesSent { get; set; }
        public int DistinctMessageSenders { get; set; }

        /// <summary>
        /// Messages that opened a new conversation rather than continuing one. Available only
        /// from AN-030 onward; null for periods before that instrumentation existed.
        /// </summary>
        public int? ConversationsStarted { get; set; }

        public DateTime? DataAvailableFromUtc { get; set; }
    }

    /// <summary>
    /// AN-037 / M-702: MBTI tested as four dichotomies rather than sixteen types.
    ///
    /// Sixteen buckets over Cocorra's user volume gives cells too small to say anything, and
    /// testing sixteen hypotheses invites finding one "significant" result by chance. Four
    /// binary splits keep the cells large enough to be worth reading.
    /// </summary>
    public class MbtiDichotomyStatDto
    {
        /// <summary>"E/I", "S/N", "T/F", "J/P".</summary>
        public string Dichotomy { get; set; } = string.Empty;

        public string LeftTrait { get; set; } = string.Empty;
        public int LeftUsers { get; set; }
        public int LeftUsersWhoSpoke { get; set; }
        public double? LeftSpeakingRatePercent { get; set; }

        public string RightTrait { get; set; } = string.Empty;
        public int RightUsers { get; set; }
        public int RightUsersWhoSpoke { get; set; }
        public double? RightSpeakingRatePercent { get; set; }

        /// <summary>
        /// Difference in percentage points. Descriptive only — this is observational data with
        /// no randomisation, so a gap here is an association and never a cause.
        /// </summary>
        public double? DifferencePercentagePoints { get; set; }
    }

    public class MbtiAnalysisDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int UsersWithMbti { get; set; }
        public int UsersWithoutMbti { get; set; }

        public IEnumerable<MbtiDichotomyStatDto> Dichotomies { get; set; } = [];

        public string InterpretationCaveat { get; set; } =
            "Observational and self-selected: users choose their own MBTI and choose whether to speak. " +
            "A difference here is an association, not evidence that personality type causes speaking behaviour.";
    }

    /// <summary>AN-038 / M-103: weekly cohort retention grid.</summary>
    public class CohortGridRowDto
    {
        public DateTime CohortWeekStartUtc { get; set; }
        public int CohortSize { get; set; }

        /// <summary>
        /// Index 0 is the cohort week itself (always 100%). Later entries are the share still
        /// joining rooms in each subsequent week. Shorter for recent cohorts — a cohort cannot
        /// have week-4 retention until four weeks have passed.
        /// </summary>
        public IEnumerable<double?> WeeklyRetentionPercent { get; set; } = [];
    }

    public class CohortGridDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public IEnumerable<CohortGridRowDto> Cohorts { get; set; } = [];

        /// <summary>
        /// Full weeks of history behind this grid. Below about 8 the grid is too sparse to read
        /// as a trend; the caller should say so rather than draw a curve through three points.
        /// </summary>
        public int WeeksOfHistory { get; set; }

        public bool HasSufficientHistory { get; set; }

        public DateTime? DataAvailableFromUtc { get; set; }
    }
}
