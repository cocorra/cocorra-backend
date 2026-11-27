using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.ReportDto
{
    public class TakeReportActionDto
    {
        [Required]
        public AdminReportAction Action { get; set; }

        public string? AdminNote { get; set; }

        public string? Reason { get; set; }
    }
}
