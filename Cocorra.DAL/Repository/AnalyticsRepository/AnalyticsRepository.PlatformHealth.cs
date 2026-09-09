using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.AnalyticsDto;
using Microsoft.EntityFrameworkCore;

namespace Cocorra.DAL.Repository.AnalyticsRepository
{
    /// <summary>
    /// A-1 — Platform Health, the dashboard's landing view.
    ///
    /// Reads RM-1 (<c>DailyPlatformMetrics</c>) exclusively. Deliberately not a live query over
    /// raw events: the point of this view is a period-over-period comparison, and the raw store
    /// is capped at 180 days while the read models are retained indefinitely. Reading raw events
    /// here would give the landing page a shorter memory than every page it links to.
    ///
    /// The corollary is that this endpoint returns nothing until aggregation has run, and it says
    /// so via <c>readModelsPopulated</c> rather than returning zeros.
    /// </summary>
    public partial class AnalyticsRepository
    {
        public async Task<PlatformHealthDto> GetPlatformHealthAsync(
            DateTime fromUtc,
            DateTime toUtc,
            bool compareToPreviousPeriod)
        {
            var fromDate = fromUtc.Date;
            var toDate = toUtc.Date;

            // Equal-length preceding window, immediately adjacent. Comparing a 7-day period to a
            // 30-day one would produce a delta that is arithmetic rather than meaningful.
            var spanDays = Math.Max(1, (toDate - fromDate).Days + 1);
            var prevToDate = fromDate.AddDays(-1);
            var prevFromDate = prevToDate.AddDays(-(spanDays - 1));

            var result = new PlatformHealthDto
            {
                From = fromUtc,
                To = toUtc,
                HasComparison = compareToPreviousPeriod,
                PreviousFrom = compareToPreviousPeriod ? prevFromDate : null,
                PreviousTo = compareToPreviousPeriod ? prevToDate.AddDays(1).AddTicks(-1) : null,
                // "To" inside today means the window is still accumulating.
                IsPartialPeriod = toUtc >= DateTime.UtcNow.Date
            };

            var earliest = await _context.DailyPlatformMetrics
                .AsNoTracking()
                .OrderBy(m => m.Date)
                .Select(m => (DateTime?)m.Date)
                .FirstOrDefaultAsync();

            result.DataAvailableFromUtc = earliest;
            result.ReadModelsPopulated = earliest is not null;

            var current = await _context.DailyPlatformMetrics
                .AsNoTracking()
                .Where(m => m.Date >= fromDate && m.Date <= toDate)
                .ToListAsync();

            var previous = compareToPreviousPeriod
                ? await _context.DailyPlatformMetrics
                    .AsNoTracking()
                    .Where(m => m.Date >= prevFromDate && m.Date <= prevToDate)
                    .ToListAsync()
                : [];

            // A window with no rows is unmeasured, not zero. The distinction is the whole reason
            // this method exists rather than a SUM with a COALESCE.
            var haveCurrent = current.Count > 0;
            var havePrevious = previous.Count > 0;

            string? gapReason = !result.ReadModelsPopulated
                ? "Read models are empty: AnalyticsAggregationService has not produced any rows yet. " +
                  "The figure is unknown, not zero."
                : !haveCurrent
                    ? "No read-model rows cover the requested window. Run POST /Analytics/System/Backfill " +
                      "for this range, or widen the window."
                    : null;

            double? Sum(List<Models.Analytics.DailyPlatformMetrics> rows, Func<Models.Analytics.DailyPlatformMetrics, double> selector)
                => rows.Count == 0 ? null : rows.Sum(selector);

            // Speaking conversion is a RATIO, so it must be recomputed from summed numerator and
            // denominator over the window. Averaging the daily percentages would weight a quiet
            // Tuesday equally with a busy Saturday and produce a number matching no real period.
            double? Conversion(List<Models.Analytics.DailyPlatformMetrics> rows)
            {
                if (rows.Count == 0) return null;
                var joiners = rows.Sum(r => r.DistinctJoiningUsers);
                if (joiners == 0) return null;
                return Math.Round((double)rows.Sum(r => r.DistinctSpeakingUsers) / joiners * 100, 2);
            }

            var metrics = new List<PlatformHealthMetricDto>
            {
                BuildMetric(
                    "M-100", "Weekly Participating Users", "count",
                    Sum(current, r => r.DistinctJoiningUsers),
                    havePrevious ? Sum(previous, r => r.DistinctJoiningUsers) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.Participation),

                BuildMetric(
                    "M-101", "Speaking Conversion Rate", "percent",
                    Conversion(current), havePrevious ? Conversion(previous) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.StageFunnel),

                BuildMetric(
                    "M-200", "Distinct Active Hosts", "count",
                    // MAX, not SUM: the same host active on Monday and Tuesday is one host, and
                    // summing daily distincts would double-count them. MAX of the daily figures
                    // is a lower bound on the window's true distinct count — stated in the
                    // metric's limitations rather than silently approximated.
                    current.Count == 0 ? null : current.Max(r => (double)r.DistinctActiveHosts),
                    havePrevious ? previous.Max(r => (double)r.DistinctActiveHosts) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.SupplyHealth),

                BuildMetric(
                    "M-205", "Rooms Gone Live", "count",
                    Sum(current, r => r.RoomsGoneLive),
                    havePrevious ? Sum(previous, r => r.RoomsGoneLive) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.Rooms),

                BuildMetric(
                    "M-508", "New Activations", "count",
                    Sum(current, r => r.VoiceVerificationsApproved),
                    havePrevious ? Sum(previous, r => r.VoiceVerificationsApproved) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.ActivationFunnel),

                BuildMetric(
                    "M-501", "New Registrations", "count",
                    Sum(current, r => r.NewRegistrations),
                    havePrevious ? Sum(previous, r => r.NewRegistrations) : null,
                    haveCurrent, gapReason, earliest,
                    Router.AnalyticsRouting.UserGrowth)
            };

            result.Metrics = metrics;
            return result;
        }

        private static PlatformHealthMetricDto BuildMetric(
            string metricKey,
            string name,
            string unit,
            double? value,
            double? previousValue,
            bool isMeasured,
            string? notMeasuredReason,
            DateTime? dataAvailableFrom,
            string drillDownEndpoint)
        {
            var dto = new PlatformHealthMetricDto
            {
                MetricKey = metricKey,
                Name = name,
                Unit = unit,
                Value = value,
                PreviousValue = previousValue,
                IsMeasured = isMeasured && value is not null,
                DataAvailableFromUtc = dataAvailableFrom,
                DrillDownEndpoint = drillDownEndpoint
            };

            if (!dto.IsMeasured)
            {
                dto.NotMeasuredReason = notMeasuredReason
                    ?? "No value could be computed for this window.";
                return dto;
            }

            if (previousValue is not null)
            {
                dto.DeltaAbsolute = Math.Round(value!.Value - previousValue.Value, 2);

                // Growth from zero has no percentage. Reporting one would be arithmetic on an
                // empty denominator dressed up as a finding.
                dto.DeltaPercent = previousValue.Value != 0
                    ? Math.Round((value.Value - previousValue.Value) / Math.Abs(previousValue.Value) * 100, 2)
                    : null;
            }

            return dto;
        }
    }
}
