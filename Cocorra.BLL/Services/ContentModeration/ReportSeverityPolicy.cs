using Cocorra.DAL.Enums;

namespace Cocorra.BLL.Services.ContentModeration
{
    /// <summary>How a text-room report's severity is derived. Pure, so the queue's priority order is predictable.</summary>
    public static class ReportSeverityPolicy
    {
        public const int EscalateAtDistinctReporters = 3;
        public const int CriticalAtDistinctReporters = 5;
        public const int EscalateAtOpenReportsAgainstSender = 3;

        public static ModerationSeverity BaseFor(ReportCategory category) => category switch
        {
            ReportCategory.Harassment => ModerationSeverity.High,
            ReportCategory.InappropriateContent => ModerationSeverity.Medium,
            ReportCategory.FakeIdentity => ModerationSeverity.Medium,
            _ => ModerationSeverity.Low
        };

        /// <param name="dictionarySeverity">Severity of a dictionary hit on the same message, if it had one.</param>
        /// <param name="distinctReportersOnMessage">Including the report being filed.</param>
        /// <param name="openReportsAgainstSenderLast24h">Open reports of any kind naming the sender, including this one.</param>
        public static ModerationSeverity Compute(
            ReportCategory category,
            ModerationSeverity? dictionarySeverity,
            int distinctReportersOnMessage,
            int openReportsAgainstSenderLast24h)
        {
            var severity = BaseFor(category);
            if (dictionarySeverity > severity)
                severity = dictionarySeverity.Value;

            if (distinctReportersOnMessage >= CriticalAtDistinctReporters)
                return ModerationSeverity.Critical;

            if (distinctReportersOnMessage >= EscalateAtDistinctReporters)
                severity = Raise(severity);

            if (openReportsAgainstSenderLast24h >= EscalateAtOpenReportsAgainstSender)
                severity = Raise(severity);

            return severity;
        }

        private static ModerationSeverity Raise(ModerationSeverity severity) =>
            severity == ModerationSeverity.Critical ? severity : severity + 1;
    }
}
