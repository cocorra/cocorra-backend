using System;

namespace Cocorra.DAL.DTOS.StaffActivityDto
{
    public class StaffActivityQueryDto
    {
        public string? Category { get; set; }
        public string? ActionType { get; set; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public Guid? TargetUserId { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 25;
    }
}
