using System;

namespace Cocorra.BLL.Services.AuditLogs
{
    /// <summary>
    /// One privileged action to record. <paramref name="Action"/> is an
    /// <see cref="Cocorra.DAL.Models.AuditActions"/> value; <paramref name="Details"/> is
    /// serialized camelCase into the row's DetailsJson. The init-only properties feed the
    /// staff activity timeline and are optional.
    /// </summary>
    public sealed record AuditEntry(
        string Action,
        Guid? ActorUserId,
        Guid? TargetUserId,
        string? TargetDisplay,
        object? Details)
    {
        /// <summary>An <see cref="Cocorra.DAL.Models.AuditCategories"/> value.</summary>
        public string? Category { get; init; }

        /// <summary>An <see cref="Cocorra.DAL.Models.AuditEntityTypes"/> value.</summary>
        public string? EntityType { get; init; }

        public string? EntityId { get; init; }

        public string? FromState { get; init; }

        public string? ToState { get; init; }

        public string? Reason { get; init; }

        /// <summary>
        /// The actor's roles, comma-separated. Only needed where there is no HTTP request to read
        /// them from (SignalR hub methods); a request's own roles take precedence.
        /// </summary>
        public string? ActorRole { get; init; }
    }
}
