using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Cocorra.DAL.Models;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Cocorra.BLL.Services.EventTracking;

namespace Cocorra.BLL.Services.OTPService
{
    public class OTPService : ResponseHandler, IOTPService
    {
        public const string ResendGenericMessage = "If the account exists and is not yet verified, a verification code has been sent.";
        public const string InvalidOtpMessage = "Invalid or expired OTP code.";
        public const string TooManyAttemptsMessage = "Too many failed attempts. Please try again later.";

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly IEventTracker _eventTracker;
        private readonly IOtpAttemptLimiter _attemptLimiter;
        private readonly ILogger<OTPService>? _logger;

        public OTPService(IConfiguration configuration, UserManager<ApplicationUser> userManager, IEmailService emailService, IHttpContextAccessor httpContextAccessor, IEventTracker eventTracker, IOtpAttemptLimiter attemptLimiter, ILogger<OTPService>? logger = null)
        {
            _configuration = configuration;
            _emailService = emailService;
            _userManager = userManager;
            _eventTracker = eventTracker;
            _attemptLimiter = attemptLimiter;
            _logger = logger;
        }

        public async Task<Response<string>> ResendOtpAsync(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest<string>("Email is required.");

            // SECURITY: every outcome below returns the same response so the endpoint
            // cannot be used to discover which emails are registered or verified.
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null || user.EmailConfirmed)
                return Success(ResendGenericMessage);

            // Anti email-bombing: at most one code per email per cooldown window.
            // Note: requesting a new code does NOT reset the failed-attempt counter.
            if (!_attemptLimiter.TryBeginSend(email, OtpPurposes.EmailConfirmation))
                return Success(ResendGenericMessage);

            var otpCode = await _userManager.GenerateUserTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider,
                OtpPurposes.EmailConfirmation
            );
            var fullImagePath = EmailTemplates.ResolveLogoUrl(_configuration["EmailSettings:LogoUrl"]);

            try
            {
                await _emailService.SendOtpEmailAsync(user.Email!, user.FirstName, user.Email!, otpCode, fullImagePath);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send verification email for user {UserId}", user.Id);
            }

            return Success(ResendGenericMessage);
        }

        public async Task<Response<string>> VerifyOtpAsync(string email, string otpCode)
        {
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(otpCode))
                return BadRequest<string>(InvalidOtpMessage);

            // Reserve the attempt atomically BEFORE verifying, so concurrent bursts cannot
            // all pass the check before any failure is recorded.
            if (!_attemptLimiter.TryRegisterAttempt(email, OtpPurposes.EmailConfirmation))
                return TooManyRequests<string>(TooManyAttemptsMessage);

            var user = await _userManager.FindByEmailAsync(email);

            // Unknown user is indistinguishable from a wrong code (the attempt is already counted).
            if (user == null)
                return BadRequest<string>(InvalidOtpMessage);

            var isValidOtp = await _userManager.VerifyUserTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider,
                OtpPurposes.EmailConfirmation,
                otpCode
            );

            if (!isValidOtp)
                return BadRequest<string>(InvalidOtpMessage);

            _attemptLimiter.Reset(email, OtpPurposes.EmailConfirmation);

            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);

            _eventTracker.Track(EventTypes.EmailConfirmed, user.Id);

            return Success("Email confirmed successfully");
        }
    }
}
