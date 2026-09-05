namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    /// <summary>
    /// AN-021 / M-301: report rate per room category, normalised by exposure.
    ///
    /// Absolute counts alone would just rank categories by popularity. Two of Cocorra's three
    /// categories are Relationships and MentalHealth, which carry duty-of-care obligations a
    /// general social product does not, so the rate matters more here than almost anywhere.
    /// </summary>
    public class ReportRateByCategoryDto
    {
        public string Category { get; set; } = string.Empty;

        /// <summary>Reports naming a room in this category.</summary>
        public int ReportCount { get; set; }

        /// <summary>Distinct non-host joins of rooms in this category — the exposure denominator.</summary>
        public int RoomJoins { get; set; }

        public int RoomsInCategory { get; set; }

        /// <summary>Reports per 1,000 joins. Null when there were no joins to normalise against.</summary>
        public double? ReportsPer1000Joins { get; set; }
    }

    public class ReportRateInsightsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public IEnumerable<ReportRateByCategoryDto> Categories { get; set; } = [];

        /// <summary>
        /// Reports with no room context. EXCLUDED from the per-category figures rather than
        /// bucketed into "Others", which would inflate that category with reports that have
        /// nothing to do with it.
        /// </summary>
        public int ReportsWithoutRoomContext { get; set; }

        public int TotalReports { get; set; }
    }

    /// <summary>
    /// AN-022 / M-302: how long the manual review gate takes.
    ///
    /// Percentiles only, no mean — deliberately. If most reviews take 20 minutes and 15% take
    /// three days, the mean describes nobody and hides exactly the users being harmed.
    /// </summary>
    public class ReviewLatencyDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int ReviewsMeasured { get; set; }

        public double? P50Hours { get; set; }
        public double? P90Hours { get; set; }
        public double? P99Hours { get; set; }

        /// <summary>Latency split by the UTC day of week the submission arrived.</summary>
        public IEnumerable<ReviewLatencyByBucketDto> ByDayOfWeekUtc { get; set; } = [];

        /// <summary>Latency split by the UTC hour the submission arrived.</summary>
        public IEnumerable<ReviewLatencyByBucketDto> ByHourUtc { get; set; } = [];

        /// <summary>
        /// Current pending-verification queue depth from the daily state snapshot, so a reader
        /// sees the backlog beside the latency. Null when no snapshot has been captured yet.
        /// </summary>
        public double? CurrentPendingQueueDepth { get; set; }

        public DateTime? QueueDepthAsOfUtc { get; set; }

        /// <summary>Earliest submission event backing this measurement.</summary>
        public DateTime? DataAvailableFromUtc { get; set; }
    }

    public class ReviewLatencyByBucketDto
    {
        public string Bucket { get; set; } = string.Empty;
        public int ReviewsMeasured { get; set; }
        public double? P50Hours { get; set; }
        public double? P90Hours { get; set; }
    }

    /// <summary>AN-023 / M-601: support volume, Cocorra's only systematic reliability signal.</summary>
    public class SupportTypeStatDto
    {
        public string Type { get; set; } = string.Empty;
        public int TicketCount { get; set; }
        public double? TicketsPer1000ActiveUsers { get; set; }
    }

    public class SupportAnalyticsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalTickets { get; set; }
        public int AnonymousTickets { get; set; }
        public IEnumerable<SupportTypeStatDto> ByType { get; set; } = [];

        public int ChatsOpened { get; set; }
        public int ChatsClosed { get; set; }

        /// <summary>Median minutes from chat creation to the first admin message.</summary>
        public double? MedianFirstResponseMinutes { get; set; }
        public double? P90FirstResponseMinutes { get; set; }

        /// <summary>Median hours from chat creation to close, over closed chats only.</summary>
        public double? MedianResolutionHours { get; set; }

        /// <summary>
        /// PROXY MEASURE. Cocorra has no error tracking, no structured logging sink and no APM,
        /// so TechnicalProblem ticket volume is the closest available signal for product
        /// reliability. It measures what users bothered to report, not what actually broke.
        /// </summary>
        public string ReliabilityCaveat { get; set; } =
            "Proxy measure — no error tracking exists. Ticket volume reflects reported problems, not actual failure rate.";
    }
}
