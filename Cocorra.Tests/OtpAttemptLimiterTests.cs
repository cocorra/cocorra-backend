using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Services.OTPService;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Cocorra.Tests;

public class OtpAttemptLimiterTests
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly OtpAttemptLimiter _limiter;

    public OtpAttemptLimiterTests()
    {
        _limiter = new OtpAttemptLimiter(_cache);
    }

    [Fact]
    public void TryRegisterAttempt_ReturnsTrueExactlyMaxFailedAttemptsTimes_ThenFalse()
    {
        const string email = "test@example.com";
        const string purpose = OtpPurposes.EmailConfirmation;

        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(email, purpose), $"Attempt {i + 1} should succeed");
        }

        // Exhausted counter must return false on subsequent attempts
        Assert.False(_limiter.TryRegisterAttempt(email, purpose));
        Assert.False(_limiter.TryRegisterAttempt(email, purpose));
    }

    [Fact]
    public void TryRegisterAttempt_NormalizesEmail_SharesCounter()
    {
        const string emailPadded = "  User@Example.com ";
        const string emailLower = "user@example.com";
        const string purpose = OtpPurposes.EmailConfirmation;

        // Use 3 attempts on padded mixed-case address
        for (int i = 0; i < 3; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(emailPadded, purpose));
        }

        // Use remaining 2 attempts on trimmed lower-case address
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts - 3; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(emailLower, purpose));
        }

        // Counter is now exhausted (3 + 2 = 5 = MaxFailedAttempts) for both representations
        Assert.False(_limiter.TryRegisterAttempt(emailLower, purpose));
        Assert.False(_limiter.TryRegisterAttempt(emailPadded, purpose));
    }

    [Fact]
    public void PurposesAreIsolated_ExhaustingEmailConfirmationDoesNotBlockPasswordReset()
    {
        const string email = "user@example.com";

        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(email, OtpPurposes.EmailConfirmation));
        }

        // EmailConfirmation is now exhausted
        Assert.False(_limiter.TryRegisterAttempt(email, OtpPurposes.EmailConfirmation));

        // PasswordReset purpose for the same email address is unaffected
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(email, OtpPurposes.PasswordReset), $"PasswordReset attempt {i + 1} should succeed");
        }

        // PasswordReset is now also exhausted
        Assert.False(_limiter.TryRegisterAttempt(email, OtpPurposes.PasswordReset));
    }

    [Fact]
    public void Reset_ClearsCounter_AllowsAttemptsAgain()
    {
        const string email = "user@example.com";
        const string purpose = OtpPurposes.EmailConfirmation;

        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(email, purpose));
        }
        Assert.False(_limiter.TryRegisterAttempt(email, purpose));

        _limiter.Reset(email, purpose);

        // Attempts should now be permitted again up to MaxFailedAttempts
        for (int i = 0; i < OtpAttemptLimiter.MaxFailedAttempts; i++)
        {
            Assert.True(_limiter.TryRegisterAttempt(email, purpose), $"Post-reset attempt {i + 1} should succeed");
        }
        Assert.False(_limiter.TryRegisterAttempt(email, purpose));
    }

    [Fact]
    public void TryBeginSend_FirstCallTrue_ImmediateSecondCallFalse_DifferentPurposeIndependent()
    {
        const string email = "user@example.com";

        // First call for EmailConfirmation succeeds
        Assert.True(_limiter.TryBeginSend(email, OtpPurposes.EmailConfirmation));
        // Immediate second call for EmailConfirmation within cooldown fails
        Assert.False(_limiter.TryBeginSend(email, OtpPurposes.EmailConfirmation));

        // Different purpose (PasswordReset) is independent and succeeds
        Assert.True(_limiter.TryBeginSend(email, OtpPurposes.PasswordReset));
        // Immediate second call for PasswordReset within cooldown fails
        Assert.False(_limiter.TryBeginSend(email, OtpPurposes.PasswordReset));
    }

    [Fact]
    public async Task Concurrency_ParallelAttemptReservations_ExactlyMaxFailedAttemptsSucceed()
    {
        const string email = "concurrent@example.com";
        const string purpose = OtpPurposes.EmailConfirmation;
        const int concurrentTasks = 50;

        var results = new ConcurrentBag<bool>();
        var tasks = Enumerable.Range(0, concurrentTasks).Select(_ => Task.Run(() =>
        {
            var allowed = _limiter.TryRegisterAttempt(email, purpose);
            results.Add(allowed);
        }));

        await Task.WhenAll(tasks);

        var successCount = results.Count(x => x);
        var failureCount = results.Count(x => !x);

        Assert.Equal(OtpAttemptLimiter.MaxFailedAttempts, successCount);
        Assert.Equal(concurrentTasks - OtpAttemptLimiter.MaxFailedAttempts, failureCount);
    }
}
