using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Cocorra.Tests;

public class EmailServiceTests
{
    private const string ApiKey = "re_test_key";
    private const string FromEmail = "noreply@cocorraapp.com";

    private static readonly ILogger<EmailService> NullLogger =
        NullLoggerFactory.Instance.CreateLogger<EmailService>();

    private static IConfiguration BuildConfig(string? apiKey = ApiKey, string? fromEmail = FromEmail, string? fromName = null)
    {
        var values = new Dictionary<string, string?>();
        if (apiKey != null) values["EmailSettings:ResendApiKey"] = apiKey;
        if (fromEmail != null) values["EmailSettings:FromEmail"] = fromEmail;
        if (fromName != null) values["EmailSettings:FromName"] = fromName;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static (EmailService Service, CapturingHandler Handler) Create(
        IConfiguration config,
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = "{\"id\":\"email_123\"}")
    {
        var handler = new CapturingHandler(status, responseBody);
        return (new EmailService(new HttpClient(handler), config, NullLogger), handler);
    }

    // ── Generic SendEmailAsync ──────────────────────────────────────────

    [Fact]
    public async Task SendEmailAsync_PostsToResendWithBearerKey()
    {
        var (service, handler) = Create(BuildConfig());

        await service.SendEmailAsync("user@example.com", "Hello", "<p>Hi</p>");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(new Uri("https://api.resend.com/emails"), handler.Request.RequestUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal(ApiKey, handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SendEmailAsync_BuildsExpectedJsonBody_WithDefaultFromName()
    {
        var (service, handler) = Create(BuildConfig());
        const string subject = "Verify your account ✅ & \"welcome\"";
        const string html = "<h2>Welcome to Cocorra ✅</h2><p>Code: <b>1234</b> & more</p>";

        await service.SendEmailAsync("user@example.com", subject, html);

        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal($"Cocorra <{FromEmail}>", root.GetProperty("from").GetString());
        var to = root.GetProperty("to");
        Assert.Equal(JsonValueKind.Array, to.ValueKind);
        Assert.Equal(new[] { "user@example.com" }, to.EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(subject, root.GetProperty("subject").GetString());
        Assert.Equal(html, root.GetProperty("html").GetString());
    }

    [Fact]
    public async Task SendEmailAsync_HonorsCustomFromName()
    {
        var (service, handler) = Create(BuildConfig(fromName: "Cocorra Team"));

        await service.SendEmailAsync("user@example.com", "Hello", "<p>Hi</p>");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal($"Cocorra Team <{FromEmail}>", doc.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task SendEmailAsync_NonSuccess_ThrowsWithStatusAndBody_WithoutApiKey()
    {
        const string errorBody = "{\"statusCode\":422,\"name\":\"validation_error\",\"message\":\"Invalid `to` field.\"}";
        var (service, _) = Create(BuildConfig(), (HttpStatusCode)422, errorBody);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.SendEmailAsync("bad", "Hello", "<p>Hi</p>"));

        Assert.Contains("422", ex.Message);
        Assert.Contains("validation_error", ex.Message);
        Assert.DoesNotContain(ApiKey, ex.Message);
        Assert.Equal((HttpStatusCode)422, ex.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendEmailAsync_MissingApiKey_ThrowsBeforeHttpCall(string? apiKey)
    {
        var (service, handler) = Create(BuildConfig(apiKey: apiKey));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendEmailAsync("user@example.com", "Hello", "<p>Hi</p>"));

        Assert.Contains("ResendApiKey", ex.Message);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SendEmailAsync_MissingFromEmail_UsesDefaultAddress()
    {
        // When FromEmail is not configured, the service now falls back to
        // noreply@cocorraapp.com instead of throwing.
        var (service, handler) = Create(BuildConfig(fromEmail: null));

        await service.SendEmailAsync("user@example.com", "Hello", "<p>Hi</p>");

        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Cocorra <noreply@cocorraapp.com>", doc.RootElement.GetProperty("from").GetString());
    }

    [Fact]
    public async Task SendEmailAsync_SuccessStatus_DoesNotThrow()
    {
        var (service, handler) = Create(BuildConfig(), HttpStatusCode.Accepted);

        var ex = await Record.ExceptionAsync(
            () => service.SendEmailAsync("user@example.com", "Hello", "<p>Hi</p>"));

        Assert.Null(ex);
        Assert.Equal(1, handler.CallCount);
    }

    // ── RESEND_API_KEY fallback ──────────────────────────────────────────

    [Fact]
    public async Task SendEmailAsync_FallsBackToResendApiKeyEnvVar()
    {
        // The RESEND_API_KEY env var reaches the service through IConfiguration (ASP.NET Core's
        // environment-variable provider), so the fallback is exercised via the config key.
        const string envKey = "re_env_fallback_key";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EmailSettings:FromEmail"] = FromEmail,
                ["RESEND_API_KEY"] = envKey
            })
            .Build();

        // No EmailSettings:ResendApiKey → should pick up RESEND_API_KEY.
        var (service, handler) = Create(config);

        await service.SendEmailAsync("user@example.com", "Test", "<p>Test</p>");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(envKey, handler.Request!.Headers.Authorization!.Parameter);
    }

    // ── SendOtpEmailAsync ───────────────────────────────────────────────

    [Fact]
    public async Task SendOtpEmailAsync_SendsTemplatedEmail()
    {
        var (service, handler) = Create(BuildConfig());

        await service.SendOtpEmailAsync(
            "user@example.com", "Ali", "user@example.com", "123456", "https://img.example.com/logo.png");

        Assert.Equal(1, handler.CallCount);
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("Verify Your Email", root.GetProperty("subject").GetString());

        var html = root.GetProperty("html").GetString()!;
        Assert.Contains("Hello Ali", html);
        Assert.Contains("user@example.com", html);
        Assert.Contains("123456", html);
        Assert.Contains("https://img.example.com/logo.png", html);
    }

    // ── SendPasswordResetEmailAsync ─────────────────────────────────────

    [Fact]
    public async Task SendPasswordResetEmailAsync_SendsTemplatedEmail()
    {
        var (service, handler) = Create(BuildConfig());

        await service.SendPasswordResetEmailAsync(
            "user@example.com", "Ali", "user@example.com", "654321", "https://img.example.com/logo.png");

        Assert.Equal(1, handler.CallCount);
        using var doc = JsonDocument.Parse(handler.Body!);
        var root = doc.RootElement;
        Assert.Equal("Password Reset Code", root.GetProperty("subject").GetString());

        var html = root.GetProperty("html").GetString()!;
        Assert.Contains("Hello Ali", html);
        Assert.Contains("reset your password", html);
        Assert.Contains("654321", html);
    }

    // ── Test helpers ────────────────────────────────────────────────────

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
}
