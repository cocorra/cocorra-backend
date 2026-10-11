using System;
using System.Text.Json;

namespace Cocorra.DAL.DTOS.LogsDto
{
    public class AuditLogDto
    {
        public long Id { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public string Action { get; set; } = string.Empty;
        public Guid? ActorUserId { get; set; }
        public string? ActorEmail { get; set; }
        public string? ActorRole { get; set; }
        public Guid? TargetUserId { get; set; }
        public string? TargetDisplay { get; set; }

        // A JsonElement rather than the stored string, so clients receive a nested object.
        public JsonElement? Details { get; set; }

        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
    }
}
