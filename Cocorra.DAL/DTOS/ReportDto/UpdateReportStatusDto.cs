using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.ReportDto
{
    public class UpdateReportStatusDto
    {
        [Required]
        [MaxLength(50)]
        public string Status { get; set; } = string.Empty;

        public string? Reason { get; set; }
    }
}
