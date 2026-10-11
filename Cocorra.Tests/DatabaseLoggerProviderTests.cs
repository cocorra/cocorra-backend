using System;
using System.Security.Claims;
using System.Threading.Channels;
using Cocorra.BLL.Services.SystemLogs;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class DatabaseLoggerProviderTests
{
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();

    private sealed class LongStringException : Exception
    {
        private readonly int _length;
        public LongStringException(int length) => _length = length;
        public override string ToString() => new string('E', _length);
    }

    [Fact]
    public void Log_InformationLevelAtDefaultMinimumWarning_IsNotEnqueued()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        logger.Log(LogLevel.Information, 0, "Info message", null, (s, e) => s);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void Log_WarningAndErrorLevelsAtDefaultMinimumWarning_AreEnqueued()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        logger.Log(LogLevel.Warning, 0, "Warn message", null, (s, e) => s);
        logger.Log(LogLevel.Error, 0, "Error message", null, (s, e) => s);

        Assert.True(channel.Reader.TryRead(out var warnEntry));
        Assert.Equal("Warning", warnEntry.Level);
        Assert.Equal("Warn message", warnEntry.Message);

        Assert.True(channel.Reader.TryRead(out var errorEntry));
        Assert.Equal("Error", errorEntry.Level);
        Assert.Equal("Error message", errorEntry.Message);
    }

    [Fact]
    public void Log_MinimumLevelConfiguredAsError_DropsWarningAndEnqueuesError()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions { MinimumLevel = "Error" });
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        logger.Log(LogLevel.Warning, 0, "Warn message", null, (s, e) => s);
        logger.Log(LogLevel.Error, 0, "Error message", null, (s, e) => s);

        Assert.True(channel.Reader.TryRead(out var errorEntry));
        Assert.Equal("Error", errorEntry.Level);
        Assert.Equal("Error message", errorEntry.Message);
        Assert.False(channel.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.EntityFrameworkCore.Infrastructure")]
    [InlineData("Cocorra.BLL.Services.SystemLogs.SystemLogFlushService")]
    [InlineData("Cocorra.BLL.Services.SystemLogs.SystemLogCleanupService")]
    public void CreateLogger_ExcludedCategories_NeverEnqueueLogEntries(string excludedCategory)
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger(excludedCategory);

        logger.Log(LogLevel.Error, 0, "Should be excluded", null, (s, e) => s);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public void Log_MessageTruncatedTo4000AndExceptionTruncatedTo8000()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        var longException = new LongStringException(10_000);
        logger.Log(LogLevel.Error, 0, "state", longException, (s, e) => new string('M', 5_000));

        Assert.True(channel.Reader.TryRead(out var entry));
        Assert.NotNull(entry.Message);
        Assert.Equal(4000, entry.Message.Length);
        Assert.Equal(new string('M', 4000), entry.Message);

        Assert.NotNull(entry.Exception);
        Assert.Equal(8000, entry.Exception!.Length);
        Assert.Equal(new string('E', 8000), entry.Exception);
    }

    [Fact]
    public void Log_EventIdZeroStoredAsNull_AndNonZeroStoredAsInteger()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        logger.Log(LogLevel.Warning, new EventId(0), "No event id", null, (s, e) => s);
        logger.Log(LogLevel.Warning, new EventId(42), "With event id", null, (s, e) => s);

        Assert.True(channel.Reader.TryRead(out var zeroEntry));
        Assert.Null(zeroEntry.EventId);

        Assert.True(channel.Reader.TryRead(out var event42Entry));
        Assert.Equal(42, event42Entry.EventId);
    }

    [Fact]
    public void Log_WithDefaultHttpContext_CapturesMethodPathAndUserId()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());

        var userId = Guid.NewGuid();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "DELETE";
        httpContext.Request.Path = "/api/v1/users/123";
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        }, "TestAuth"));

        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        logger.Log(LogLevel.Warning, 0, "Context captured", null, (s, e) => s);

        Assert.True(channel.Reader.TryRead(out var entry));
        Assert.Equal("DELETE", entry.RequestMethod);
        Assert.Equal("/api/v1/users/123", entry.RequestPath);
        Assert.Equal(userId, entry.UserId);
    }

    [Fact]
    public void Log_ThrowingFormatter_DoesNotPropagateExceptionOutOfLog()
    {
        var channel = Channel.CreateBounded<SystemLog>(100);
        var options = Options.Create(new SystemLogOptions());
        using var provider = new DatabaseLoggerProvider(channel, _httpContextAccessorMock.Object, options);
        var logger = provider.CreateLogger("Cocorra.BLL.TestService");

        var exception = Record.Exception(() =>
        {
            logger.Log(LogLevel.Error, 0, "state", null, (s, e) => throw new InvalidOperationException("Formatter error"));
        });

        Assert.Null(exception);
        Assert.False(channel.Reader.TryRead(out _));
    }
}
