using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.SupportDto
{
    public class UpdateTicketStatusDto
    {
        [Required]
        [EnumDataType(typeof(SupportTicketStatus))]
        public SupportTicketStatus? Status { get; set; }

        public string? Reason { get; set; }
    }
}
