using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Models.Analytics;

namespace Cocorra.BLL.Services.Analytics
{
    public interface IStateSnapshotService
    {
        /// <summary>
        /// Captures and persists the daily state snapshot for a given Cairo date (defaults to today).
        /// Idempotent on (Date, MetricKey): re-running updates in place, and a concurrent insert
        /// by another writer is retried as an update rather than surfacing a constraint violation.
        /// </summary>
        /// <param name="targetDate">Cairo date to label the snapshot with. Defaults to today in Cairo.</param>
        /// <param name="skipIfAlreadyCaptured">
        /// When true, returns the existing rows untouched if the date already has a snapshot.
        /// Used by the startup run so a container restart cannot overwrite a 00:15 reading
        /// with a mid-afternoon one — queue depth varies through the day, and a series mixing
        /// capture hours is not comparable.
        /// </param>
        Task<List<DailyStateSnapshot>> CaptureSnapshotAsync(
            DateOnly? targetDate = null,
            bool skipIfAlreadyCaptured = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves recorded snapshots for a specific date range. The result is sparse:
        /// dates with no snapshot are simply absent. Call <see cref="GetGapReportAsync"/>
        /// to find out which those are — do not treat absence as zero.
        /// </summary>
        Task<List<DailyStateSnapshot>> GetSnapshotsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports which dates in the range are missing or incomplete, so uninstrumented
        /// periods can be rendered as visible gaps rather than interpolated or shown as zero.
        /// </summary>
        Task<SnapshotGapReport> GetGapReportAsync(DateOnly requestedFrom, DateOnly requestedTo, CancellationToken cancellationToken = default);
    }
}
