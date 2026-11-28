using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.ReportDto
{
    /// <summary>
    /// Filters and paging for the staff reports queue. PrioritySort orders by Severity descending
    /// (legacy reports without one last), then newest first; otherwise newest first.
    /// </summary>
    public sealed record ReportListQuery(
        ReportCategory? Category,
        string? Status,
        ModerationSeverity? Severity,
        bool PrioritySort,
        int PageNumber,
        int PageSize);
}
