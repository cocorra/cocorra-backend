using System;

namespace Cocorra.DAL.DTOS.LogsDto
{
    public class SystemLogDto
    {
        public long Id { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string Level { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int? EventId { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? Exception { get; set; }
        public string? TraceId { get; set; }
        public string? RequestMethod { get; set; }
        public string? RequestPath { get; set; }
        public Guid? UserId { get; set; }
    }
}
