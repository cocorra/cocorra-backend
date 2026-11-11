using System;

namespace Cocorra.BLL.Base
{
    /// <summary>
    /// Every timestamp the server stores or exchanges is UTC. Values built in code without a kind,
    /// and calendar dates such as read-model keys, arrive as <see cref="DateTimeKind.Unspecified"/>;
    /// reading those as UTC keeps one value meaning the same instant whatever the host's time zone.
    /// </summary>
    public static class UtcDateTime
    {
        public static DateTime Normalize(DateTime value) => value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value
        };

        public static DateTime? Normalize(DateTime? value) =>
            value.HasValue ? Normalize(value.Value) : null;
    }
}
