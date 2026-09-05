using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Cocorra.DAL.Models.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.BLL.Services.Analytics
{
    public interface IDecisionCenterService
    {
        Task<DecisionCenterDto> GetDecisionCenterAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// AN-039 — Decision Center.
    ///
    /// Change detection across the watched signals, gated hard on having a baseline. The gate
    /// is the design: detection without a baseline produces alerts on ordinary variance, and a
    /// dashboard that cries wolf in its first month is ignored permanently — a harder outcome
    /// to reverse than a delayed launch. Until the gate is met every signal reports Unknown and
    /// nothing is marked significant.
    ///
    /// Reads exclusively from DailyPlatformMetrics, so it inherits whatever history the
    /// aggregation service has accumulated and never re-derives a metric a second way.
    /// </summary>
    public class DecisionCenterService : IDecisionCenterService
    {
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Weeks of history before any signal may be called significant. Four to six weeks was
        /// the gate in the rollout plan; four is the floor.
        /// </summary>
        public const int RequiredBaselineWeeks = 4;

        /// <summary>
        /// Significance threshold in standard deviations of the signal's own weekly history.
        /// A fixed percentage would fire constantly on a low-volume signal and never on a
        /// high-volume one, which is the same as not having a threshold.
        /// </summary>
        private const double SignificanceSigma = 2.0;

        public DecisionCenterService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public async Task<DecisionCenterDto> GetDecisionCenterAsync(CancellationToken cancellationToken = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var today = DateTime.UtcNow.Date;

            var daily = await db.DailyPlatformMetrics
                .AsNoTracking()
                .Where(m => m.Date <= today)
                .OrderBy(m => m.Date)
                .ToListAsync(cancellationToken);

            var weeks = daily
                .GroupBy(m => StartOfWeekUtc(m.Date))
                .OrderBy(g => g.Key)
                .Select(g => new WeeklyRollup
                {
                    WeekStart = g.Key,
                    DistinctJoiningUsers = g.Sum(m => m.DistinctJoiningUsers),
                    DistinctSpeakingUsers = g.Sum(m => m.DistinctSpeakingUsers),
                    DistinctActiveHosts = g.Sum(m => m.DistinctActiveHosts),
                    RoomsGoneLive = g.Sum(m => m.RoomsGoneLive),
                    NewRegistrations = g.Sum(m => m.NewRegistrations),
                    VoiceSubmitted = g.Sum(m => m.VoiceVerificationsSubmitted),
                    VoiceApproved = g.Sum(m => m.VoiceVerificationsApproved)
                })
                .ToList();

            // The current week is partial by definition; comparing a Tuesday against a full week
            // would report a collapse every Monday.
            var completeWeeks = weeks.Where(w => w.WeekStart < StartOfWeekUtc(today)).ToList();
            var weeksOfHistory = completeWeeks.Count;
            var hasBaseline = weeksOfHistory >= RequiredBaselineWeeks;

            var definitions = new (string Key, string Name, string MetricKey, Func<WeeklyRollup, double?> Select, bool HigherIsBetter, string Decision)[]
            {
                ("weekly_participating_users", "Weekly Participating Users", MetricRegistry.WeeklyParticipatingUsers,
                    w => w.DistinctJoiningUsers, true,
                    "Whether the core loop is growing; the North Star for every roadmap trade-off."),

                ("speaking_conversion", "Speaking Conversion", MetricRegistry.SpeakingConversion,
                    w => w.DistinctJoiningUsers > 0 ? (double)w.DistinctSpeakingUsers / w.DistinctJoiningUsers * 100 : null, true,
                    "Whether listeners become participants, which drives stage format and capacity decisions."),

                ("active_hosts", "Distinct Active Hosts", MetricRegistry.ActiveHosts,
                    w => w.DistinctActiveHosts, true,
                    "Supply health. A fall here precedes a fall in everything else."),

                ("rooms_gone_live", "Rooms Gone Live", MetricRegistry.ActiveHosts,
                    w => w.RoomsGoneLive, true,
                    "Whether there is anything to attend, independent of how many hosts exist."),

                ("new_registrations", "New Registrations", MetricRegistry.UserRegistrations,
                    w => w.NewRegistrations, true,
                    "Top-of-funnel intake; separates an acquisition problem from an activation one."),

                ("activation_rate", "Verification Approval Rate", MetricRegistry.VoiceVerificationFunnel,
                    w => w.VoiceSubmitted > 0 ? (double)w.VoiceApproved / w.VoiceSubmitted * 100 : null, true,
                    "Whether the manual review gate is throttling intake or rejecting more people than usual.")
            };

            var signals = definitions
                .Select(d => BuildSignal(d.Key, d.Name, d.MetricKey, completeWeeks, d.Select, d.HigherIsBetter, d.Decision, hasBaseline))
                .ToList();

            return new DecisionCenterDto
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Signals = signals,
                WeeksOfHistory = weeksOfHistory,
                HasBaseline = hasBaseline,
                RequiredBaselineWeeks = RequiredBaselineWeeks,
                BaselineCaveat = hasBaseline
                    ? null
                    : $"Only {weeksOfHistory} complete week(s) of aggregated history: {RequiredBaselineWeeks} are required " +
                      "before a change can be distinguished from ordinary variation. Values are shown, but no direction " +
                      "is asserted and nothing is flagged as significant."
            };
        }

        private static DecisionSignalDto BuildSignal(
            string key,
            string name,
            string metricKey,
            List<WeeklyRollup> weeks,
            Func<WeeklyRollup, double?> select,
            bool higherIsBetter,
            string decision,
            bool hasBaseline)
        {
            var series = weeks.Select(select).Where(v => v.HasValue).Select(v => v!.Value).ToList();

            var signal = new DecisionSignalDto
            {
                SignalKey = key,
                Name = name,
                MetricKey = metricKey,
                BaselineWeeks = series.Count,
                DecisionSupported = decision
            };

            if (series.Count == 0)
            {
                signal.Interpretation = "No aggregated history for this signal yet.";
                return signal;
            }

            signal.CurrentValue = Math.Round(series[^1], 2);

            if (series.Count >= 2)
            {
                signal.PreviousValue = Math.Round(series[^2], 2);
                signal.ChangePercent = series[^2] != 0
                    ? Math.Round((series[^1] - series[^2]) / Math.Abs(series[^2]) * 100, 2)
                    : null;
            }

            if (!hasBaseline)
            {
                // Deliberately Unknown, not Stable. "Stable" is a finding; this is an absence of
                // one, and conflating them is how a dashboard starts asserting things it cannot
                // support.
                signal.Direction = SignalDirection.Unknown;
                signal.Interpretation =
                    "Not enough history to judge. The value is shown for reference only.";
                return signal;
            }

            // Baseline excludes the current week so a signal is never compared against itself.
            var baseline = series.Take(series.Count - 1).ToList();
            var mean = baseline.Average();
            var stdDev = baseline.Count > 1
                ? Math.Sqrt(baseline.Sum(v => Math.Pow(v - mean, 2)) / (baseline.Count - 1))
                : 0;

            var current = series[^1];
            var delta = current - mean;

            // A zero standard deviation means a perfectly flat history, where any movement at
            // all is genuinely new — but with a tiny sample that is more likely to be a
            // coincidence than a finding, so it is not treated as significant on its own.
            signal.IsSignificant = stdDev > 0 && Math.Abs(delta) > SignificanceSigma * stdDev;

            if (!signal.IsSignificant)
            {
                signal.Direction = SignalDirection.Stable;
                signal.Interpretation =
                    $"Within normal variation for this signal (baseline mean {Math.Round(mean, 2)}, " +
                    $"±{Math.Round(SignificanceSigma * stdDev, 2)}).";
                return signal;
            }

            var movedUp = delta > 0;
            signal.Direction = movedUp == higherIsBetter ? SignalDirection.Improving : SignalDirection.Worsening;

            signal.Interpretation =
                $"{(movedUp ? "Up" : "Down")} to {Math.Round(current, 2)} against a baseline mean of " +
                $"{Math.Round(mean, 2)} over {baseline.Count} week(s) — beyond {SignificanceSigma} standard deviations, " +
                "so this is unlikely to be ordinary week-to-week variation.";

            return signal;
        }

        private static DateTime StartOfWeekUtc(DateTime value)
        {
            var date = value.Date;
            var offset = ((int)date.DayOfWeek + 6) % 7; // Monday = 0
            return date.AddDays(-offset);
        }

        private sealed class WeeklyRollup
        {
            public DateTime WeekStart { get; init; }
            public int DistinctJoiningUsers { get; init; }
            public int DistinctSpeakingUsers { get; init; }
            public int DistinctActiveHosts { get; init; }
            public int RoomsGoneLive { get; init; }
            public int NewRegistrations { get; init; }
            public int VoiceSubmitted { get; init; }
            public int VoiceApproved { get; init; }
        }
    }
}
