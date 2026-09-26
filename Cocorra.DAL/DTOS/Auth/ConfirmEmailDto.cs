using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.Auth
{
    public class ConfirmEmailDto
    {
        [Required, EmailAddress]
        public string Email { get; set; } = string.Empty;

        [Required]
        public string OtpCode { get; set; } = string.Empty;
    }
}
