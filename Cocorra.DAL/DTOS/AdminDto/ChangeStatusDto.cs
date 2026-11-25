using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.AdminDto
{
    public class ChangeStatusDto
    {
        [Required]
        public UserStatus? NewStatus { get; set; }
        public string? Reason { get; set; }
    }
}
