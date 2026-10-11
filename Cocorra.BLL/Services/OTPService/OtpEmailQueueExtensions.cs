using System;
using Cocorra.BLL.Services.Email;
using Microsoft.Extensions.DependencyInjection;

namespace Cocorra.BLL.Services.OTPService
{
    public static class OtpEmailQueueExtensions
    {
        /// <summary>
        /// Queues issuing a new OTP for <paramref name="purpose"/> and emailing it. Both happen in
        /// the background, so the request does no database or mail work that depends on whether
        /// the account exists. Returns false if the queue is full.
        /// </summary>
        public static bool TryEnqueueOtpEmail(
            this IEmailQueue queue, Guid userId, string purpose, string to, string firstName, string logoUrl)
        {
            var isReset = purpose == OtpPurposes.PasswordReset;
            return queue.TryEnqueue(async (services, ct) =>
            {
                var otpCode = await services.GetRequiredService<IOtpCodeService>().IssueAsync(userId, purpose, ct);
                var emailService = services.GetRequiredService<IEmailService>();
                if (isReset)
                    await emailService.SendPasswordResetEmailAsync(to, firstName, to, otpCode, logoUrl, ct);
                else
                    await emailService.SendOtpEmailAsync(to, firstName, to, otpCode, logoUrl, ct);
            }, $"{(isReset ? "password reset" : "verification")} email for user {userId}");
        }
    }
}
