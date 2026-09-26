using System.Threading.Tasks;

namespace Cocorra.BLL.Services.Email
{
    public interface IEmailService
    {
        /// <summary>
        /// Sends a generic email with the given HTML body.
        /// </summary>
        Task SendEmailAsync(string to, string subject, string htmlContent);

        /// <summary>
        /// Sends an OTP verification email using the branded Cocorra template.
        /// </summary>
        Task SendOtpEmailAsync(string to, string userName, string email, string otpCode, string logoUrl);

        /// <summary>
        /// Sends a password-reset OTP email using the branded Cocorra template.
        /// </summary>
        Task SendPasswordResetEmailAsync(string to, string userName, string email, string otpCode, string logoUrl);
    }
}