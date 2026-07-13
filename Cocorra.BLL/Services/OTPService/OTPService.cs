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
using Cocorra.BLL.Services.EventTracking;

namespace Cocorra.BLL.Services.OTPService
{
    public class OTPService : ResponseHandler, IOTPService
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IEmailService _emailService;
        private readonly IConfiguration _configuration;
        private readonly IEventTracker _eventTracker;

        public OTPService(IConfiguration configuration, UserManager<ApplicationUser> userManager, IEmailService emailService, IHttpContextAccessor httpContextAccessor, IEventTracker eventTracker)
        {
            _configuration = configuration;
            _emailService = emailService;
            _userManager = userManager;
            _eventTracker = eventTracker;
        }

        public async Task<Response<string>> ResendOtpAsync(string email)
        {
            var user = await _userManager.FindByEmailAsync(email);
            if (user == null)
                return BadRequest<string>("User not found");
            if (user.EmailConfirmed) return BadRequest<string>("Email is already confirmed");
            var otpCode = await _userManager.GenerateTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider
            );
            var baseUrl = _configuration["AppSettings:BaseUrl"];
            var fullImagePath = $"{baseUrl}/System/388f7e03b835e6ca1f7c156816047a360bf18efe.png";
            var emailBody = GetOtpHtmlTemplate(user.FirstName, user.Email!, otpCode, fullImagePath);
            await _emailService.SendEmailAsync(user.Email!, "Resend OTP", emailBody);

            return Success("OTP code resent successfully");

        }

        public async Task<Response<string>> VerifyOtpAsync(string email, string otpCode)
        {
            var user = await _userManager.FindByEmailAsync(email);

            if (user == null)
                return BadRequest<string>("User not found");

            var isValidOtp = await _userManager.VerifyTwoFactorTokenAsync(
                user,
                TokenOptions.DefaultEmailProvider,
                otpCode
            );

            if (!isValidOtp)
                return BadRequest<string>("Invalid OTP code");

            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);

            _eventTracker.Track(EventTypes.EmailConfirmed, user.Id);

            return Success("Email confirmed successfully");
        }
        private string GetOtpHtmlTemplate(string userName, string email, string otpCode, string baseUrl)
        {
            // استخدمنا $$""" لكي نتجاهل أقواس الـ CSS العادية { }
            // ونستخدم المتغيرات بين قوسين مزدوجين {{ }}
            return $$"""
    <!DOCTYPE html>
    <html lang="en">

    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
        <title>Email Verification</title>
        <style>
            body { margin: 0; padding: 0; background-color: #e0e0e0; display: flex; justify-content: center; align-items: center; min-height: 100vh; font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; }
            .container { width: 100%; max-width: 400px; text-align: center; box-shadow: 0 4px 10px rgba(0, 0, 0, 0.2); }
            .header { background-color: #4f5b49; padding: 30px 0; }
            .logo-container { width: 100px; height: 100px; margin: 0 auto; border: 2px solid white; border-radius: 8px; overflow: hidden; background-color: #c5d1ba; }
            .logo-container img { width: 100%; height: 100%; object-fit: cover; }
            .content { background-color: #a0b19d; padding: 25px 20px 40px; color: white; }
            .greeting { margin: 0 0 15px 0; font-size: 26px; font-weight: bold; }
            .email-box { background-color: white; color: #333; padding: 12px 20px; border-radius: 30px; display: inline-block; font-weight: bold; font-size: 18px; margin-bottom: 25px; width: 85%; box-sizing: border-box; }
            .instruction { margin: 0 0 25px 0; font-size: 16px; line-height: 1.4; font-weight: 600; }
            .code-box { background-color: white; color: black; font-size: 32px; font-weight: 900; letter-spacing: 8px; padding: 20px; border-radius: 15px; margin-bottom: 25px; display: inline-block; width: 85%; box-sizing: border-box; }
            .footer { margin: 0; font-size: 14px; font-weight: bold; }
        </style>
    </head>

    <body>
        <div class="container">
            <div class="header">
                <div class="logo-container">
                    <img src="{{baseUrl}}" alt="Cocorra">
                </div>
            </div>

            <div class="content">
                <h1 class="greeting">Hello {{userName}}</h1>

                <div class="email-box">
                    {{email}}
                </div>

                <p class="instruction">
                    The current code is for<br>
                    the verification process to complete<br>
                    your account registration.
                </p>

                <div class="code-box">
                    {{otpCode}}
                </div>

                <p class="footer">
                    This code is valid for 10 minutes.
                </p>
            </div>
        </div>
    </body>

    </html>
    """;
        }

    }
}