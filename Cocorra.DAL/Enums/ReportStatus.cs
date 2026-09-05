namespace Cocorra.DAL.Enums
{
    /// <summary>
    /// AN-033. Report.Status is a free-form string that analytics can only match on three
    /// literal values, so any typo or new value silently disappears from every breakdown.
    /// This enum names the values the application actually writes; the string column is kept
    /// so existing rows and callers are unaffected while the two are reconciled.
    /// </summary>
    public enum ReportStatus
    {
        Open = 0,
        Resolved = 1,
        Rejected = 2
    }

    /// <summary>AN-033: the same problem on SupportTicket.Status.</summary>
    public enum SupportTicketStatus
    {
        Open = 0,
        Resolved = 1,
        Rejected = 2
    }
}
