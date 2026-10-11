using System;

namespace Cocorra.DAL.DTOS.LogsDto
{
    public class SystemLogQueryDto
    {
        public string? Level { get; set; }
        public string? Search { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
