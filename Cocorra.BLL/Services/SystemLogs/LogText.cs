using System;

namespace Cocorra.BLL.Services.SystemLogs
{
    /// <summary>
    /// String and date normalization shared by the system and audit log writers and readers.
    /// Values are cut to the column sizes up front because an over-length value would fail the
    /// whole insert, not just that field.
    /// </summary>
    internal static class LogText
    {
        public const int MaxSearchLength = 200;

        public static string? Truncate(string? value, int maxLength) =>
            value is not null && value.Length > maxLength ? value.Substring(0, maxLength) : value;

        public static string? NormalizeSearch(string? search) =>
            string.IsNullOrWhiteSpace(search) ? null : Truncate(search.Trim(), MaxSearchLength);

        /// <summary>
        /// Stored timestamps are UTC. A filter value without an offset is read as UTC too, so the
        /// same query string means the same instant regardless of the server's time zone.
        /// </summary>
        public static DateTime? ToUtc(DateTime? value) => value?.Kind switch
        {
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
            _ => value
        };

        /// <summary>EF materializes datetime2 as Unspecified; marking it UTC makes it serialize with "Z".</summary>
        public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
