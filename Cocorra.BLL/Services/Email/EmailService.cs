using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.Email
{
    /// <summary>
    /// Sends transactional email through the Resend HTTP API (https://resend.com/docs/api-reference/emails/send-email).
    /// <para>
    /// Configuration is resolved in priority order:
    /// <list type="number">
    ///   <item>Configuration path <c>EmailSettings:ResendApiKey</c> (appsettings / user-secrets /
    ///         env var <c>EmailSettings__ResendApiKey</c>).</item>
    ///   <item>Configuration key <c>RESEND_API_KEY</c> (e.g. the <c>RESEND_API_KEY</c> environment variable,
    ///         which ASP.NET Core already loads into <see cref="IConfiguration"/>).</item>
    /// </list>
    /// Sender address is read from <c>EmailSettings:FromEmail</c> (default: <c>noreply@cocorraapp.com</c>)
    /// and <c>EmailSettings:FromName</c> (default: <c>Cocorra</c>).
    /// </para>
    /// </summary>
    public class EmailService : IEmailService
    {
        private const string ResendEmailsEndpoint = "https://api.resend.com/emails";
        private const string DefaultFromName = "Cocorra";
        private const string DefaultFromEmail = "noreply@cocorraapp.com";

        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<EmailService> _logger;

        public EmailService(HttpClient httpClient, IConfiguration config, ILogger<EmailService> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        /// <inheritdoc />
        public async Task SendEmailAsync(string to, string subject, string htmlContent, CancellationToken cancellationToken = default)
        {
            var (apiKey, fromAddress) = ResolveSettings();

            var payload = new
            {
                from = fromAddress,
                to = new[] { to },
                subject,
                html = htmlContent
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, ResendEmailsEndpoint)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            // Privacy: recipients are masked in logs; the HTML body (which may contain an OTP) is never logged.
            var maskedRecipient = MaskEmail(to);
            _logger.LogInformation("Sending email via Resend to {Recipient} with subject \"{Subject}\"", maskedRecipient, subject);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Sanitise: never log the API key in error messages.
                _logger.LogError(
                    "Resend API error {StatusCode} sending to {Recipient}: {ResponseBody}",
                    (int)response.StatusCode, maskedRecipient, body);

                throw new HttpRequestException(
                    $"Resend API returned {(int)response.StatusCode} ({response.StatusCode}): {body}",
                    null,
                    response.StatusCode);
            }

            _logger.LogInformation("Email sent successfully to {Recipient}", maskedRecipient);
        }

        /// <inheritdoc />
        public async Task SendOtpEmailAsync(string to, string userName, string email, string otpCode, string logoUrl, CancellationToken cancellationToken = default)
        {
            var html = EmailTemplates.Otp(userName, email, otpCode, logoUrl);
            await SendEmailAsync(to, "Verify Your Email", html, cancellationToken);
        }

        /// <inheritdoc />
        public async Task SendPasswordResetEmailAsync(string to, string userName, string email, string otpCode, string logoUrl, CancellationToken cancellationToken = default)
        {
            var html = EmailTemplates.PasswordReset(userName, email, otpCode, logoUrl);
            await SendEmailAsync(to, "Password Reset Code", html, cancellationToken);
        }

        // ── Private helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Resolves the API key and formatted sender address from configuration.
        /// <c>EmailSettings:ResendApiKey</c> takes precedence over the <c>RESEND_API_KEY</c> key.
        /// Both are read through <see cref="IConfiguration"/> only (environment variables are already
        /// part of it), so tests using in-memory configuration stay hermetic.
        /// </summary>
        private (string ApiKey, string FromAddress) ResolveSettings()
        {
            var emailSettings = _config.GetSection("EmailSettings");

            // EmailSettings:ResendApiKey → RESEND_API_KEY (both via IConfiguration).
            var apiKey = emailSettings["ResendApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                apiKey = _config["RESEND_API_KEY"];

            var fromEmail = emailSettings["FromEmail"];
            var fromName = emailSettings["FromName"];

            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(
                    "Email configuration is missing 'EmailSettings:ResendApiKey' (or the RESEND_API_KEY environment variable).");

            if (string.IsNullOrWhiteSpace(fromEmail))
                fromEmail = DefaultFromEmail;

            if (string.IsNullOrWhiteSpace(fromName))
                fromName = DefaultFromName;

            return (apiKey, $"{fromName} <{fromEmail}>");
        }

        /// <summary>
        /// Masks an email address for logging, e.g. <c>john@example.com</c> → <c>j***@example.com</c>.
        /// </summary>
        internal static string MaskEmail(string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
                return "***";

            var at = email.IndexOf('@');
            if (at <= 0)
                return "***";

            return $"{email[0]}***{email.Substring(at)}";
        }
    }
}
