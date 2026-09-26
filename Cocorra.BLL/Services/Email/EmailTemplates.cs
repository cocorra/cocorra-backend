using System.Net;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Branded HTML templates for transactional Cocorra emails.
    /// All templates use raw-string-literal interpolation (<c>$$"""</c>) so that
    /// CSS braces don't collide with C# interpolation.
    /// Every interpolated value is HTML-encoded (user-controlled names/emails must never inject markup).
    /// </summary>
    public static class EmailTemplates
    {
        /// <summary>
        /// Logo shown in the header of every email. Override with <c>EmailSettings:LogoUrl</c>.
        /// </summary>
        public const string DefaultLogoUrl = "https://admin.cocorraapp.com/cocorra-logo.jpg";

        /// <summary>Returns the configured logo URL, or <see cref="DefaultLogoUrl"/> when none is set.</summary>
        public static string ResolveLogoUrl(string? configuredUrl) =>
            string.IsNullOrWhiteSpace(configuredUrl) ? DefaultLogoUrl : configuredUrl.Trim();

        /// <summary>
        /// OTP verification template — used during registration and resend-OTP flows.
        /// </summary>
        public static string Otp(string userName, string email, string otpCode, string logoUrl)
        {
            userName = WebUtility.HtmlEncode(userName ?? string.Empty);
            email = WebUtility.HtmlEncode(email ?? string.Empty);
            otpCode = WebUtility.HtmlEncode(otpCode ?? string.Empty);
            logoUrl = WebUtility.HtmlEncode(logoUrl ?? string.Empty);

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
                            <img src="{{logoUrl}}" alt="Cocorra">
                        </div>
                    </div>
                    <div class="content">
                        <h1 class="greeting">Hello {{userName}}</h1>
                        <div class="email-box">{{email}}</div>
                        <p class="instruction">
                            The current code is for<br>
                            the verification process to complete<br>
                            your account registration.
                        </p>
                        <div class="code-box">{{otpCode}}</div>
                        <p class="footer">This code is valid for 6 minutes.</p>
                    </div>
                </div>
            </body>
            </html>
            """;
        }

        /// <summary>
        /// Password-reset template — sends a one-time code to reset the user's password.
        /// </summary>
        public static string PasswordReset(string userName, string email, string otpCode, string logoUrl)
        {
            userName = WebUtility.HtmlEncode(userName ?? string.Empty);
            email = WebUtility.HtmlEncode(email ?? string.Empty);
            otpCode = WebUtility.HtmlEncode(otpCode ?? string.Empty);
            logoUrl = WebUtility.HtmlEncode(logoUrl ?? string.Empty);

            return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>Password Reset</title>
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
                            <img src="{{logoUrl}}" alt="Cocorra">
                        </div>
                    </div>
                    <div class="content">
                        <h1 class="greeting">Hello {{userName}}</h1>
                        <div class="email-box">{{email}}</div>
                        <p class="instruction">
                            Use the code below to<br>
                            reset your password.<br>
                            If you didn't request this, please ignore this email.
                        </p>
                        <div class="code-box">{{otpCode}}</div>
                        <p class="footer">This code is valid for 6 minutes.</p>
                    </div>
                </div>
            </body>
            </html>
            """;
        }
    }
}
