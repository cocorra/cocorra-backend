namespace Cocorra.DAL.DTOS.AnalyticsDto
{
    public class ReportCategoryStatDto
    {
        public string Category { get; set; } = string.Empty;
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class MostReportedUserDto
    {
        public Guid UserId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int ReportCount { get; set; }
    }

    public class ReportInsightsDto
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }

        public int TotalReports { get; set; }
        public int OpenReports { get; set; }
        public int ResolvedReports { get; set; }
        public int InProgressReports { get; set; }

        public IEnumerable<ReportCategoryStatDto> ReportsByCategory { get; set; } = [];

        /// <summary>Top 10 most reported users.</summary>
        public IEnumerable<MostReportedUserDto> MostReportedUsers { get; set; } = [];
    }
}
