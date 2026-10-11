using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.BLL.Services.OTPService;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Cocorra.Tests;

public class OtpCodeServiceTests : IDisposable
{
    private const string TestKey = "test-only-otp-hmac-key-at-least-32-chars";

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;
    private readonly AppDbContext _context;
    private readonly OtpCodeService _service;
    private readonly Guid _userId;

    public OtpCodeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();

        _services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();
        _context = _services.GetRequiredService<AppDbContext>();
        _context.Database.EnsureCreated();

        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "otp@test.local", Email = "otp@test.local", FirstName = "Otp", LastName = "User" };
        _context.Users.Add(user);
        _context.SaveChanges();
        _userId = user.Id;

        _service = CreateService(TestKey);
    }

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    private OtpCodeService CreateService(string key) =>
        new(_context, Options.Create(new OtpOptions { HmacKey = key }));

    private OtpCode SingleRow() => _context.OtpCodes.AsNoTracking().Single();

    [Fact]
    public async Task IssueAsync_ReturnsSixDigitCode_AndStoresOnlyAnHmac()
    {
        var code = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);

        Assert.Matches("^[0-9]{6}$", code);
        var row = SingleRow();
        Assert.Equal(32, row.CodeHash.Length);
        Assert.DoesNotContain(code, Convert.ToHexString(row.CodeHash));
        Assert.NotEqual(Encoding.UTF8.GetBytes(code), row.CodeHash);
        Assert.InRange(row.ExpiresAt, DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow.Add(OtpCodeService.Lifetime));
    }

    [Fact]
    public async Task TryConsumeAsync_CorrectCode_SucceedsOnce()
    {
        var code = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);

        Assert.True(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
        Assert.Empty(_context.OtpCodes);
    }

    [Fact]
    public async Task TryConsumeAsync_OtherPurposeOrOtherUser_IsRejected()
    {
        var code = await _service.IssueAsync(_userId, OtpPurposes.PasswordReset);

        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
        Assert.False(await _service.TryConsumeAsync(Guid.NewGuid(), OtpPurposes.PasswordReset, code));
        Assert.True(await _service.TryConsumeAsync(_userId, OtpPurposes.PasswordReset, code));
    }

    [Fact]
    public async Task IssueAsync_NewCode_InvalidatesThePreviousOne()
    {
        var first = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);
        string second;
        do second = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);
        while (second == first); // 1-in-a-million collision guard

        Assert.Single(_context.OtpCodes);
        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, first));
        Assert.True(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, second));
    }

    [Fact]
    public async Task IssueAsync_NewCode_ResetsAttempts()
    {
        await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);
        for (int i = 0; i < OtpCodeService.MaxAttemptsPerCode - 1; i++)
            await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, "not-it");

        var code = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);

        Assert.Equal(0, SingleRow().Attempts);
        Assert.True(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
    }

    [Fact]
    public async Task TryConsumeAsync_AfterMaxWrongAttempts_CodeIsDeletedEvenForTheCorrectCode()
    {
        var code = await _service.IssueAsync(_userId, OtpPurposes.PasswordReset);

        for (int i = 0; i < OtpCodeService.MaxAttemptsPerCode; i++)
            Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.PasswordReset, "not-it"));

        Assert.Empty(_context.OtpCodes);
        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.PasswordReset, code));
    }

    [Fact]
    public async Task TryConsumeAsync_ExpiredCode_IsRejected()
    {
        var code = await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);
        await _context.OtpCodes.ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));

        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
    }

    [Fact]
    public async Task TryConsumeAsync_HashFromADifferentKey_IsRejected()
    {
        var code = await CreateService("a-completely-different-hmac-key-0123456789").IssueAsync(_userId, OtpPurposes.EmailConfirmation);

        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, code));
    }

    [Fact]
    public async Task TryConsumeAsync_NoCodeIssued_ReturnsFalse()
    {
        Assert.False(await _service.TryConsumeAsync(_userId, OtpPurposes.EmailConfirmation, "123456"));
        Assert.False(await _service.TryConsumeAsync(Guid.Empty, OtpPurposes.EmailConfirmation, "123456"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Constructor_MissingOrShortKey_Throws(string key)
    {
        Assert.Throws<InvalidOperationException>(() => CreateService(key));
    }

    [Fact]
    public async Task CleanupService_DeletesExpiredAndExhaustedCodes_KeepsLiveOnes()
    {
        var other = new ApplicationUser { Id = Guid.NewGuid(), UserName = "otp2@test.local", Email = "otp2@test.local", FirstName = "Otp", LastName = "Two" };
        _context.Users.Add(other);
        await _context.SaveChangesAsync();

        await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);   // will expire
        await _service.IssueAsync(_userId, OtpPurposes.PasswordReset);       // will be exhausted
        await _service.IssueAsync(other.Id, OtpPurposes.EmailConfirmation);  // stays live
        await _context.OtpCodes.Where(o => o.UserId == _userId && o.Purpose == OtpPurposes.EmailConfirmation)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        await _context.OtpCodes.Where(o => o.UserId == _userId && o.Purpose == OtpPurposes.PasswordReset)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Attempts, OtpCodeService.MaxAttemptsPerCode));

        var cleanup = new OtpCodeCleanupService(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OtpCodeCleanupService>.Instance);
        var deleted = await cleanup.DeleteDeadCodesAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        Assert.Equal(other.Id, SingleRow().UserId);
    }

    [Fact]
    public async Task DeletingTheUser_DeletesTheirCodes()
    {
        await _service.IssueAsync(_userId, OtpPurposes.EmailConfirmation);

        await _context.Users.Where(u => u.Id == _userId).ExecuteDeleteAsync();

        Assert.Empty(_context.OtpCodes);
    }
}
