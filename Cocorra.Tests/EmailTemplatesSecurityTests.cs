using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cocorra.Tests;

public class EmailTemplatesSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveLogoUrl_NotConfigured_ReturnsCocorraLogo(string? configured)
    {
        Assert.Equal("https://admin.cocorraapp.com/cocorra-logo.jpg", EmailTemplates.ResolveLogoUrl(configured));
    }

    [Fact]
    public void ResolveLogoUrl_Configured_ReturnsConfiguredUrl()
    {
        Assert.Equal("https://cdn.example.com/logo.png", EmailTemplates.ResolveLogoUrl(" https://cdn.example.com/logo.png "));
    }

    [Fact]
    public void Templates_RenderLogoUrlInImgSrc()
    {
        var logo = EmailTemplates.DefaultLogoUrl;
        Assert.Contains($"<img src=\"{logo}\"", EmailTemplates.Otp("Ali", "a@b.com", "123456", logo));
        Assert.Contains($"<img src=\"{logo}\"", EmailTemplates.PasswordReset("Ali", "a@b.com", "123456", logo));
    }

    [Theory]
    [InlineData("Otp")]
    [InlineData("PasswordReset")]
    public void EmailTemplates_HtmlEncodes_UserNameAndEmail(string templateName)
    {
        const string maliciousInput = "<a href=\"https://evil\">x</a>";
        const string expectedEncoded = "&lt;a href=&quot;https://evil&quot;&gt;x&lt;/a&gt;";

        // Malicious userName must not appear raw; HTML-encoded version must appear
        string htmlWithName = templateName == "Otp"
            ? EmailTemplates.Otp(maliciousInput, "safe@example.com", "123456", "https://logo.png")
            : EmailTemplates.PasswordReset(maliciousInput, "safe@example.com", "123456", "https://logo.png");

        Assert.DoesNotContain(maliciousInput, htmlWithName);
        Assert.Contains(expectedEncoded, htmlWithName);

        // Malicious email must not appear raw; HTML-encoded version must appear
        string htmlWithEmail = templateName == "Otp"
            ? EmailTemplates.Otp("SafeUser", maliciousInput, "123456", "https://logo.png")
            : EmailTemplates.PasswordReset("SafeUser", maliciousInput, "123456", "https://logo.png");

        Assert.DoesNotContain(maliciousInput, htmlWithEmail);
        Assert.Contains(expectedEncoded, htmlWithEmail);
    }

    [Theory]
    [InlineData("Otp")]
    [InlineData("PasswordReset")]
    public void EmailTemplates_FooterSpecifiesValidFor6Minutes_Not10Minutes(string templateName)
    {
        string html = templateName == "Otp"
            ? EmailTemplates.Otp("User", "user@example.com", "123456", "https://logo.png")
            : EmailTemplates.PasswordReset("User", "user@example.com", "123456", "https://logo.png");

        Assert.Contains("valid for 6 minutes", html);
        Assert.DoesNotContain("10 minutes", html);
    }

    [Fact]
    public async Task EmailService_ResendApiKey_WinsOverResendApiKeyEnvVar()
    {
        const string configApiKey = "re_config_wins";
        const string envApiKey = "re_env_loses";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:ResendApiKey"] = configApiKey,
                ["RESEND_API_KEY"] = envApiKey
            })
            .Build();

        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"id\":\"123\"}");
        var service = new EmailService(new HttpClient(handler), config, new CapturingLogger<EmailService>());

        await service.SendEmailAsync("user@example.com", "Test", "<p>Test</p>");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(configApiKey, handler.Request!.Headers.Authorization!.Parameter);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmailService_UsesResendApiKeyEnvVar_WhenEmailSettingsKeyIsEmpty(string? emptySetting)
    {
        const string envApiKey = "re_env_used";

        var values = new Dictionary<string, string?>
        {
            ["RESEND_API_KEY"] = envApiKey
        };
        if (emptySetting != null)
        {
            values["EmailSettings:ResendApiKey"] = emptySetting;
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"id\":\"123\"}");
        var service = new EmailService(new HttpClient(handler), config, new CapturingLogger<EmailService>());

        await service.SendEmailAsync("user@example.com", "Test", "<p>Test</p>");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(envApiKey, handler.Request!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task EmailService_LoggedMessages_MaskRecipientOnSuccess()
    {
        const string rawEmail = "user@example.com";
        const string maskedEmail = "u***@example.com";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:ResendApiKey"] = "re_test_key"
            })
            .Build();

        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"id\":\"ok\"}");
        var logger = new CapturingLogger<EmailService>();
        var service = new EmailService(new HttpClient(handler), config, logger);

        await service.SendEmailAsync(rawEmail, "Subject", "<p>Body</p>");

        Assert.NotEmpty(logger.FormattedLogs);
        foreach (var message in logger.FormattedLogs)
        {
            Assert.DoesNotContain(rawEmail, message);
        }
        Assert.Contains(logger.FormattedLogs, m => m.Contains(maskedEmail));
    }

    [Fact]
    public async Task EmailService_LoggedMessages_MaskRecipientOnFailure()
    {
        const string rawEmail = "user@example.com";
        const string maskedEmail = "u***@example.com";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:ResendApiKey"] = "re_test_key"
            })
            .Build();

        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "{\"error\":\"fail\"}");
        var logger = new CapturingLogger<EmailService>();
        var service = new EmailService(new HttpClient(handler), config, logger);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.SendEmailAsync(rawEmail, "Subject", "<p>Body</p>"));

        Assert.NotEmpty(logger.FormattedLogs);
        foreach (var message in logger.FormattedLogs)
        {
            Assert.DoesNotContain(rawEmail, message);
        }
        Assert.Contains(logger.FormattedLogs, m => m.Contains(maskedEmail));
    }

    [Theory]
    [InlineData("user@example.com", "u***@example.com")]
    [InlineData("alice.bob@company.org", "a***@company.org")]
    [InlineData(null, "***")]
    [InlineData("", "***")]
    [InlineData("   ", "***")]
    [InlineData("invalid", "***")]
    public void EmailService_MaskEmail_InternalHelper_FormatsCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, EmailService.MaskEmail(input));
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _responseBody;

        public CapturingHandler(HttpStatusCode status, string responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public int CallCount { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            Body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> FormattedLogs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            FormattedLogs.Add(formatter(state, exception));
        }
    }
}
