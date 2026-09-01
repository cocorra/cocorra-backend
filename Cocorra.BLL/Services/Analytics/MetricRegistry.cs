using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// AN-012: In-code executable Metric Registry providing contracts, formulas,
    /// exclusions, and trust levels for all Cocorra metrics.
    ///
    /// Every metric served by the analytics API must appear here. `08a` graded 1 of 12 shipped
    /// metrics as VERIFIED while all twelve rendered identically; a contract is what lets a
    /// reader tell those cases apart. MetricRegistryContractTests fails the build if a served
    /// metric has no contract or a contract is missing a mandatory field.
    /// </summary>
    public class MetricRegistry : IMetricRegistry
    {
        // Metric keys, named so a call site cannot drift from the registry silently.
        public const string WeeklyParticipatingUsers = "M-100";
        public const string SpeakingConversion = "M-101";
        public const string WeeklyReturnRate = "M-102";
        public const string LegacyRetentionCohort = "M-102-LEGACY";
        public const string ActiveHosts = "M-200";
        public const string ReportRate = "M-300";
        public const string OpenReportBacklog = "M-303";
        public const string PlatformSummary = "M-500";
        public const string UserRegistrations = "M-501";
        public const string UserStatusHistory = "M-502";
        public const string RoomParticipation = "M-503";
        public const string RoomAnalytics = "M-504";
        public const string MostActiveRooms = "M-505";
        public const string PeakActiveHours = "M-506";
        public const string ActivationFunnel = "M-507";
        public const string VoiceVerificationFunnel = "M-508";
        public const string HostRetention = "M-201";
        public const string HostConcentration = "M-202";
        public const string ReportRateByCategory = "M-301";
        public const string ReviewLatency = "M-302";
        public const string SupportVolume = "M-601";
        public const string SocialGraph = "M-701";
        public const string MbtiSpeakingAssociation = "M-702";
        public const string CohortGrid = "M-103";

        private static readonly Dictionary<string, MetricContract> Contracts = new(StringComparer.OrdinalIgnoreCase)
        {
            {
                WeeklyParticipatingUsers, new MetricContract
                {
                    MetricKey = WeeklyParticipatingUsers,
                    Name = "Weekly Participating Users (WPU)",
                    BusinessPurpose = "North Star metric: measures active community engagement in voice rooms.",
                    TechnicalDefinition = "Distinct non-host users with at least one room_joined event in a rolling 7-day window.",
                    Formula = "COUNT(DISTINCT UserId) WHERE EventType = 'room_joined' AND UserId != Room.HostId AND OccurredAtUtc >= now - 7d",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Room Host joining own room", "Anonymous non-authenticated joins" },
                    Limitations = { "Bounded by 180-day raw event retention unless aggregated into read models" },
                    ValidationMethod = "Reconciles against DailyRoomMetrics and live room participant count."
                }
            },
            {
                SpeakingConversion, new MetricContract
                {
                    MetricKey = SpeakingConversion,
                    Name = "Speaking Conversion Rate",
                    BusinessPurpose = "Measures the share of listeners who take the mic on stage.",
                    TechnicalDefinition = "Distinct non-host users with a mic_activated event / distinct non-host joiners in the period.",
                    Formula = "COUNT(DISTINCT NonHostMicActivators) / COUNT(DISTINCT NonHostJoiners) * 100",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Room Host (excluded from numerator and denominator)" },
                    Limitations = { "Derived from mic_activated, which fires only on an unmuted transition, so a participant who never mutes is counted once" },
                    ValidationMethod = "Assert no UserId appears in both speaker and passive sets simultaneously."
                }
            },
            {
                WeeklyReturnRate, new MetricContract
                {
                    MetricKey = WeeklyReturnRate,
                    Name = "Weekly Return Rate",
                    BusinessPurpose = "Measures organic repeat participation, the demand-side health signal.",
                    TechnicalDefinition = "Of non-host users with a room_joined event in week N, the share with a room_joined event in any later week.",
                    Formula = "COUNT(DISTINCT ReturningJoiners) / COUNT(DISTINCT CohortJoiners) * 100",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Room hosts", "session_started is not consulted: the signal is server-emitted room_joined" },
                    Limitations = { "Hard deletes remove non-returners from the denominator, biasing the rate upward until AN-013 lands" },
                    ValidationMethod = "A user joining in week 1 and again in week 3 must count as returned."
                }
            },
            {
                ActiveHosts, new MetricContract
                {
                    MetricKey = ActiveHosts,
                    Name = "Rooms Gone Live",
                    BusinessPurpose = "Measures platform voice supply and coach activity.",
                    TechnicalDefinition = "Total count of rooms that transitioned to Live status in the window.",
                    Formula = "COUNT(Rooms) WHERE Status != Scheduled AND CreatedAt IN window",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Scheduled rooms that were cancelled before starting" },
                    Limitations = { "Derived from Rooms.CreatedAt, which is relational and never purged, so history is complete" },
                    ValidationMethod = "Exact count from Rooms table where Status != Scheduled."
                }
            },
            {
                ReportRate, new MetricContract
                {
                    MetricKey = ReportRate,
                    Name = "Report Insights",
                    BusinessPurpose = "Cocorra's primary safety signal; drives moderation and duty-of-care decisions.",
                    TechnicalDefinition = "Counts of Report rows by status and reason in the window.",
                    Formula = "COUNT(Reports) GROUP BY Status, Reason",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Report.Status is a free-form string; only Open, Resolved and Rejected are written today" },
                    ValidationMethod = "Status counts sum to the total report count for the window."
                }
            },
            {
                OpenReportBacklog, new MetricContract
                {
                    MetricKey = OpenReportBacklog,
                    Name = "Pending Verification Queue Depth",
                    BusinessPurpose = "Monitors moderator/admin review operational backlog.",
                    TechnicalDefinition = "Daily snapshot count of users with Status = Pending.",
                    Formula = "COUNT(AspNetUsers) WHERE Status = 'Pending'",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Cannot be backfilled; captured daily by StateSnapshotService, and missing dates render as gaps" },
                    ValidationMethod = "Reconciles against DailyStateSnapshots."
                }
            },
            {
                PlatformSummary, new MetricContract
                {
                    MetricKey = PlatformSummary,
                    Name = "Platform Summary",
                    BusinessPurpose = "Single composite overview for the admin landing page.",
                    TechnicalDefinition = "Composite of the registration, room, participation and report metrics.",
                    Formula = "Composite; see the component contracts.",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Inherited from each component metric" },
                    Limitations = { "Inherits the weakest trust level of its components; read the component contracts before acting on it" },
                    ValidationMethod = "Each component reconciles against its own endpoint."
                }
            },
            {
                UserRegistrations, new MetricContract
                {
                    MetricKey = UserRegistrations,
                    Name = "User Registrations",
                    BusinessPurpose = "Tracks intake volume, the top of the growth funnel.",
                    TechnicalDefinition = "Count of ApplicationUser rows bucketed by CreatedAt.",
                    Formula = "COUNT(*) GROUP BY bucket(CreatedAt)",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Hard deletes remove users retroactively, so historical buckets can shrink until AN-013 lands" },
                    ValidationMethod = "Cumulative registrations must never decrease between runs (test 61)."
                }
            },
            {
                UserStatusHistory, new MetricContract
                {
                    MetricKey = UserStatusHistory,
                    Name = "User Status At Time",
                    BusinessPurpose = "Shows what the verification funnel looked like historically, rather than projecting today's statuses backwards.",
                    TechnicalDefinition = "Per user, the most recent voice_verification_result at or before each bucket boundary; no event means Pending.",
                    Formula = "status(user, t) = last(voice_verification_result WHERE OccurredAtUtc <= t) ?? 'Pending'",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Users with no status event are reported as Pending, not omitted" },
                    Limitations =
                    {
                        "Reconstructed from events, so it reaches back only as far as the 180-day raw retention window",
                        "Users whose status changed before event tracking existed appear as Pending"
                    },
                    ValidationMethod = "Reconstructed current status must equal AspNetUsers.Status for every user with a status event."
                }
            },
            {
                RoomParticipation, new MetricContract
                {
                    MetricKey = RoomParticipation,
                    Name = "Room Participation",
                    BusinessPurpose = "Volume and depth of audience participation in rooms.",
                    TechnicalDefinition = "RoomParticipant rows in the window, excluding each room's own host.",
                    Formula = "COUNT(*) WHERE JoinedAt IN window AND UserId != Room.HostId",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Room host in their own room" },
                    Limitations =
                    {
                        "TotalSpokenSeconds includes idle open-mic time for anyone on stage, so spoken-time averages overstate speech",
                        "Top speakers and hand-raise counts are not returned: neither is measurable from current data"
                    },
                    ValidationMethod = "No UserId may appear in both the speaker and passive sets for the same window (test 4)."
                }
            },
            {
                RoomAnalytics, new MetricContract
                {
                    MetricKey = RoomAnalytics,
                    Name = "Room Analytics",
                    BusinessPurpose = "Supply-side view of how many rooms are created and how many go live.",
                    TechnicalDefinition = "Rooms bucketed by CreatedAt with status and category breakdowns.",
                    Formula = "COUNT(*) GROUP BY bucket(CreatedAt), Status, Category",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "None" },
                    Limitations = { "AvgDurationHours derives from the scheduled StartDate rather than the actual go-live time, so it measures schedule length, not airtime" },
                    ValidationMethod = "Room counts reconcile against DailyPlatformMetrics.RoomsCreated."
                }
            },
            {
                MostActiveRooms, new MetricContract
                {
                    MetricKey = MostActiveRooms,
                    Name = "Most Active Rooms",
                    BusinessPurpose = "Identifies which rooms and categories actually draw an audience.",
                    TechnicalDefinition = "Rooms ranked by count of room_joined events in the window.",
                    Formula = "COUNT(room_joined) GROUP BY RoomId ORDER BY count DESC",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Counts joins, not concurrent presence: a room with churn can outrank a room with a stable audience" },
                    ValidationMethod = "Join counts reconcile against RoomParticipant rows for the same rooms."
                }
            },
            {
                PeakActiveHours, new MetricContract
                {
                    MetricKey = PeakActiveHours,
                    Name = "Peak Active Hours",
                    BusinessPurpose = "Informs when to schedule rooms and when to staff admin review.",
                    TechnicalDefinition = "room_joined events grouped by UTC hour of day.",
                    Formula = "COUNT(room_joined) GROUP BY HOUR(OccurredAtUtc)",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "None" },
                    Limitations = { "Reported in UTC while the user base is predominantly UTC+2/+3, so displayed peaks are shifted from local time" },
                    ValidationMethod = "Hour buckets sum to the total join count for the window."
                }
            },
            {
                ActivationFunnel, new MetricContract
                {
                    MetricKey = ActivationFunnel,
                    Name = "Sequential Activation Funnel",
                    BusinessPurpose = "Locates where new users stall on the way to their first room.",
                    TechnicalDefinition = "Sequential funnel: a user counts at step N only if every earlier step has an earlier-or-equal first occurrence.",
                    Formula = "|{u : first(step_1) <= ... <= first(step_N)}|",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Events with no UserId" },
                    Limitations = { "Only measures instrumented steps; an uninstrumented step is absent from the response rather than reported as zero" },
                    ValidationMethod = "Each step count must be <= the previous step count for any input (test 6)."
                }
            },
            {
                VoiceVerificationFunnel, new MetricContract
                {
                    MetricKey = VoiceVerificationFunnel,
                    Name = "Voice Verification Drop-off",
                    BusinessPurpose = "Measures the manual review gate that throttles all intake.",
                    TechnicalDefinition = "Counts at each voice-verification stage from submission to result.",
                    Formula = "COUNT(DISTINCT UserId) per verification stage",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Conversion only: it shows how many users cleared each stage, not how long they waited" },
                    ValidationMethod = "Stage counts reconcile against the AspNetUsers.Status distribution."
                }
            },
            {
                LegacyRetentionCohort, new MetricContract
                {
                    MetricKey = LegacyRetentionCohort,
                    Name = "Legacy Retention Cohort (deprecated)",
                    BusinessPurpose = "Retained only so existing dashboard panels keep rendering until the M-102 cutover.",
                    TechnicalDefinition = "Share of a cohort with an activity event on exactly day N after their cohort date.",
                    Formula = "|{u : exists activity where days(activity - cohort) = N}| / |cohort|",
                    TrustLevel = MetricTrustLevel.Unreliable,
                    Exclusions = { "None" },
                    Limitations =
                    {
                        "Exact-day matching: a user active on days 2 and 5 counts for neither D1 nor D7",
                        "The default activity signal is session_started, which is cookie-derived and unvalidated on the Flutter client",
                        "Superseded by M-102. Do not use for decisions"
                    },
                    ValidationMethod = "None. This metric is graded UNRELIABLE and is retained only for continuity; validate against M-102 instead."
                }
            },
            {
                HostRetention, new MetricContract
                {
                    MetricKey = HostRetention,
                    Name = "Host Second-Room Rate",
                    BusinessPurpose = "Whether a first-time host comes back. Recruiting hosts is wasted if they run one room and stop.",
                    TechnicalDefinition = "Of hosts whose FIRST room falls in the window, the share who created a second room at any later point.",
                    Formula = "COUNT(hosts with >=2 rooms and first room in window) / COUNT(hosts with first room in window) * 100",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "Hosts whose first room predates the window: they are not new hosts" },
                    Limitations = { "A host whose second room falls after the query window still counts, so the figure can rise as later data arrives" },
                    ValidationMethod = "Reconciles against Rooms grouped by HostId ordered by CreatedAt."
                }
            },
            {
                HostConcentration, new MetricContract
                {
                    MetricKey = HostConcentration,
                    Name = "Host Concentration",
                    BusinessPurpose = "Key-person risk on the supply side: how much of the platform rests on a handful of hosts.",
                    TechnicalDefinition = "Share of rooms run by the top host and top 3 hosts, plus how many hosts it takes to cover half of all rooms.",
                    Formula = "top-N room count / total rooms; smallest k where sum(top k) >= total/2",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions = { "None" },
                    Limitations = { "Counts rooms, not audience: a host running many small rooms outranks one running a few large ones" },
                    ValidationMethod = "Per-host room counts sum to the window total."
                }
            },
            {
                ReportRateByCategory, new MetricContract
                {
                    MetricKey = ReportRateByCategory,
                    Name = "Report Rate by Room Category",
                    BusinessPurpose = "Cocorra's highest-stakes safety analysis. Relationships and MentalHealth carry duty-of-care obligations a general social product does not.",
                    TechnicalDefinition = "Reports naming a room in each category, per 1,000 distinct non-host joins of rooms in that category.",
                    Formula = "reports_in_category / non_host_joins_in_category * 1000",
                    TrustLevel = MetricTrustLevel.Verified,
                    Exclusions =
                    {
                        "Reports with no room context are excluded, not bucketed into Others",
                        "Host joins are excluded from the exposure denominator"
                    },
                    Limitations =
                    {
                        "Measures reports filed, not incidents occurred: under-reporting in a sensitive category would read as safety",
                        "A category with no joins returns null rather than a zero rate"
                    },
                    ValidationMethod = "Per-category counts sum to the count of reports carrying room context."
                }
            },
            {
                ReviewLatency, new MetricContract
                {
                    MetricKey = ReviewLatency,
                    Name = "Voice Verification Review Latency",
                    BusinessPurpose = "Measures the manual approval gate, the hard serialisation point on the entire growth funnel.",
                    TechnicalDefinition = "Hours between a user's first voice_verification_submitted and their first voice_verification_result.",
                    Formula = "percentile(result_time - submit_time) over reviewed users",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Submissions still awaiting a result: they have no latency yet, and counting them as zero would understate the wait" },
                    Limitations =
                    {
                        "Bounded by the 180-day raw event retention window",
                        "Percentiles only, by contract: NO MEAN is returned, because a mean over a bimodal wait describes nobody",
                        "Pending submissions are excluded, so a growing backlog does not move this figure — read it beside the queue depth"
                    },
                    ValidationMethod = "Exact percentiles reproduced from a known fixture; response asserted to contain no mean field."
                }
            },
            {
                SupportVolume, new MetricContract
                {
                    MetricKey = SupportVolume,
                    Name = "Support Volume and Response Time",
                    BusinessPurpose = "Cocorra's only systematic reliability signal, and the response times users actually experience.",
                    TechnicalDefinition = "Support tickets by type per 1,000 active users; first-admin-reply and resolution times from SupportChat and SupportMessage.",
                    Formula = "tickets_of_type / active_users * 1000; percentile(first_admin_message - chat_created)",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "None. Anonymous tickets are included: a user who cannot log in is the signal, not noise" },
                    Limitations =
                    {
                        "PROXY MEASURE — no error tracking, structured logging sink or APM exists, so this counts problems users bothered to report, not failures that occurred",
                        "SupportTicket.Status is a free-form string, so resolution state is not reliably machine-readable"
                    },
                    ValidationMethod = "Ticket type counts sum to the window total; chats closed <= chats opened."
                }
            },
            {
                SocialGraph, new MetricContract
                {
                    MetricKey = SocialGraph,
                    Name = "Social Graph Health",
                    BusinessPurpose = "Whether Cocorra produces real connection or only activity.",
                    TechnicalDefinition = "Friend requests sent and accepted, acceptance rate, median time to accept, and message volume.",
                    Formula = "accepted / sent * 100; percentile(accepted_at - sent_at)",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "None" },
                    Limitations =
                    {
                        "Reciprocity, not volume: sends alone would let a spam wave read as engagement, which is why acceptance is reported beside them",
                        "conversationsStarted is null before AN-030 instrumentation rather than zero",
                        "Request origin defaults to friend_list; room-originated requests are not yet distinguishable"
                    },
                    ValidationMethod = "Accepted count never exceeds sent count over the same window."
                }
            },
            {
                MbtiSpeakingAssociation, new MetricContract
                {
                    MetricKey = MbtiSpeakingAssociation,
                    Name = "MBTI Dichotomy vs Speaking",
                    BusinessPurpose = "Explores whether personality type is associated with taking the mic, to inform room format rather than to target users.",
                    TechnicalDefinition = "For each of the four MBTI dichotomies, the share of non-host joiners with that trait who emitted mic_activated.",
                    Formula = "speakers_with_trait / joiners_with_trait * 100, per dichotomy",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Users with no MBTI recorded", "Users who never joined a room in the window", "Room hosts" },
                    Limitations =
                    {
                        "OBSERVATIONAL AND SELF-SELECTED: users choose their own MBTI and choose whether to speak. A gap is an association, never evidence of cause",
                        "Four dichotomies rather than sixteen types, deliberately: sixteen cells are too small at Cocorra's volume, and testing sixteen hypotheses invites a chance finding"
                    },
                    ValidationMethod = "Left and right trait counts sum to the population with a recorded MBTI."
                }
            },
            {
                CohortGrid, new MetricContract
                {
                    MetricKey = CohortGrid,
                    Name = "Weekly Cohort Retention Grid",
                    BusinessPurpose = "Shows whether retention is improving for newer cohorts, which a single blended rate cannot reveal.",
                    TechnicalDefinition = "Users grouped by the week of their FIRST room join; each later cell is the share of that cohort joining in that week.",
                    Formula = "active_in_week_N / cohort_size * 100",
                    TrustLevel = MetricTrustLevel.ConditionallyReliable,
                    Exclusions = { "Room hosts" },
                    Limitations =
                    {
                        "Needs roughly 8 weeks of history to read as a trend; hasSufficientHistory reports whether that bar is met",
                        "Recent cohorts have shorter rows by construction — a short row is missing data, not a collapse in retention",
                        "Hard deletes remove non-returners, biasing every row upward until AN-013 lands"
                    },
                    ValidationMethod = "Every cohort's first cell is 100%; each user appears in exactly one cohort."
                }
            }
        };

        public MetricContract? GetContract(string metricKey)
        {
            return Contracts.TryGetValue(metricKey, out var contract) ? contract : null;
        }

        public IReadOnlyList<MetricContract> GetAllContracts()
        {
            return Contracts.Values.ToList();
        }
    }
}
