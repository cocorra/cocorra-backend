using System;
using System.Collections.Generic;

namespace Cocorra.BLL.Services.Analytics
{
    /// <summary>
    /// RM-5 gap report. State snapshots are point-in-time counts and can NEVER be
    /// reconstructed after the fact, so a date with no snapshot is a permanent hole.
    /// This report makes those holes explicit so a consumer renders a visible gap
    /// instead of connecting a line across it. Nothing here interpolates.
    /// </summary>
    public sealed class SnapshotGapReport
    {
        /// <summary>
        /// Earliest date any snapshot exists for. Null when the table is empty.
        /// Dates before this were never measured — they are not counted as gaps,
        /// because the service did not exist yet.
        /// </summary>
        public DateTime? DataAvailableFromUtc { get; init; }

        /// <summary>Start of the examined range, clamped to <see cref="DataAvailableFromUtc"/>.</summary>
        public DateTime FromDate { get; init; }

        /// <summary>End of the examined range, clamped to today (UTC).</summary>
        public DateTime ToDate { get; init; }

        /// <summary>Dates inside the range with no snapshot rows at all.</summary>
        public List<DateTime> MissingDates { get; init; } = new();

        /// <summary>Dates with some rows but fewer than the full expected metric set.</summary>
        public List<DateTime> IncompleteDates { get; init; } = new();

        /// <summary>How many metric keys a complete date is expected to carry.</summary>
        public int ExpectedMetricsPerDate { get; init; }

        public bool HasGaps => MissingDates.Count > 0 || IncompleteDates.Count > 0;
    }
}
