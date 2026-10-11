using System;

namespace Cocorra.BLL.Services.AuditLogs
{
    /// <summary>
    /// One privileged action to record. <paramref name="Action"/> is an
    /// <see cref="Cocorra.DAL.Models.AuditActions"/> value; <paramref name="Details"/> is
    /// serialized camelCase into the row's DetailsJson.
    /// </summary>
    public sealed record AuditEntry(
        string Action,
        Guid? ActorUserId,
        Guid? TargetUserId,
        string? TargetDisplay,
        object? Details);
}
