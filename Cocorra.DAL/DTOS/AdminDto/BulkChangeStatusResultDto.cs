using System;
using System.Collections.Generic;

namespace Cocorra.DAL.DTOS.AdminDto
{
    /// <summary>
    /// Per-user outcome of a bulk status change. A bulk operation can partially
    /// succeed, so the caller inspects <see cref="Results"/> to know exactly which
    /// ids changed and which failed (and why).
    /// </summary>
    public class BulkChangeStatusResultDto
    {
        public int TotalRequested { get; set; }
        public int SucceededCount { get; set; }
        public int FailedCount { get; set; }
        public List<BulkItemResultDto> Results { get; set; } = new();
    }

    public class BulkItemResultDto
    {
        public Guid UserId { get; set; }
        public bool Succeeded { get; set; }
        public string? Message { get; set; }
    }
}
