using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.LogsDto;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class AuditLogServiceTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<IHttpContextAccessor> _httpContextAccessorMock = new();
    private readonly Mock<ILogger<AuditLogService>> _loggerMock = new();

    public void Dispose() => _host.Dispose();

    private (AuditLogService service, AppDbContext db) CreateService()
    {
        var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repo = new AuditLogRepository(db);
        var service = new AuditLogService(repo, _httpContextAccessorMock.Object, _loggerMock.Object);
        return (service, db);
    }

    [Fact]
    public async Task RecordAsync_WithHttpContext_PersistsActionActorTargetDisplayAndEnrichesFromContext()
    {
        var (service, db) = CreateService();

        var adminId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");
        httpContext.Request.Headers.UserAgent = "Mozilla/5.0 TestBrowser";

        var claims = new List<Claim>
        {
            new(ClaimTypes.Email, "superadmin@cocorra.com"),
            new(ClaimTypes.Role, "Admin"),
            new(ClaimTypes.Role, "Manager")
        };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

        var entry = new AuditEntry(
            AuditActions.UserStatusChanged,
            adminId,
            targetUserId,
            "target@cocorra.com",
            new { fromStatus = "Pending", toStatus = "Active" }
        );

        await service.RecordAsync(entry);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(AuditActions.UserStatusChanged, row.Action);
        Assert.Equal(adminId, row.ActorUserId);
        Assert.Equal(targetUserId, row.TargetUserId);
        Assert.Equal("target@cocorra.com", row.TargetDisplay);
        Assert.Equal("superadmin@cocorra.com", row.ActorEmail);
        Assert.Equal("Admin,Manager", row.ActorRole);
        Assert.Equal("192.168.1.50", row.IpAddress);
        Assert.Equal("Mozilla/5.0 TestBrowser", row.UserAgent);

        Assert.NotNull(row.DetailsJson);
        using var doc = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal("Pending", doc.RootElement.GetProperty("fromStatus").GetString());
        Assert.Equal("Active", doc.RootElement.GetProperty("toStatus").GetString());
    }

    [Fact]
    public async Task RecordAsync_UserAgentExceeds512Characters_TruncatesTo512()
    {
        var (service, db) = CreateService();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.UserAgent = new string('A', 600);
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns(httpContext);

        var entry = new AuditEntry(
            AuditActions.UserStatusChanged,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "target@cocorra.com",
            null
        );

        await service.RecordAsync(entry);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.NotNull(row.UserAgent);
        Assert.Equal(512, row.UserAgent!.Length);
        Assert.Equal(new string('A', 512), row.UserAgent);
    }

    [Fact]
    public async Task RecordAsync_GuidEmptyActor_StoredAsNull()
    {
        var (service, db) = CreateService();

        var entry = new AuditEntry(
            AuditActions.UserStatusChanged,
            Guid.Empty,
            Guid.NewGuid(),
            "user@cocorra.com",
            null
        );

        await service.RecordAsync(entry);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Null(row.ActorUserId);
    }

    [Fact]
    public async Task RecordAsync_NoHttpContext_PersistsRowWithNullRequestFields()
    {
        var (service, db) = CreateService();
        _httpContextAccessorMock.Setup(h => h.HttpContext).Returns((HttpContext?)null);

        var actorId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var entry = new AuditEntry(
            AuditActions.UserRolesChanged,
            actorId,
            targetId,
            "target@cocorra.com",
            new { roles = new[] { "Coach" } }
        );

        await service.RecordAsync(entry);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(AuditActions.UserRolesChanged, row.Action);
        Assert.Equal(actorId, row.ActorUserId);
        Assert.Equal(targetId, row.TargetUserId);
        Assert.Equal("target@cocorra.com", row.TargetDisplay);
        Assert.Null(row.ActorEmail);
        Assert.Null(row.ActorRole);
        Assert.Null(row.IpAddress);
        Assert.Null(row.UserAgent);
    }

    [Fact]
    public async Task RecordAsync_RepositoryThrows_DoesNotThrowAndLogsError()
    {
        var mockRepo = new Mock<IAuditLogRepository>();
        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditLog>()))
            .ThrowsAsync(new InvalidOperationException("Simulated database failure"));

        var service = new AuditLogService(mockRepo.Object, _httpContextAccessorMock.Object, _loggerMock.Object);

        var entry = new AuditEntry(
            AuditActions.UserStatusChanged,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "target@cocorra.com",
            null
        );

        var exception = await Record.ExceptionAsync(() => service.RecordAsync(entry));

        Assert.Null(exception);
        _loggerMock.Verify(
            l => l.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task GetAuditLogsAsync_ActionFilter_ReturnsMatchingRowsOnly()
    {
        var (service, db) = CreateService();

        db.AuditLogs.AddRange(
            new AuditLog { Action = AuditActions.UserStatusChanged, OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserRolesChanged, OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserCreatedWithRole, OccurredAtUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var query = new AuditLogQueryDto { Action = AuditActions.UserRolesChanged };
        var response = await service.GetAuditLogsAsync(query);

        Assert.True(response.Succeeded);
        var items = response.Data!.ToList();
        Assert.Single(items);
        Assert.Equal(AuditActions.UserRolesChanged, items[0].Action);
    }

    [Fact]
    public async Task GetAuditLogsAsync_InvalidAction_ReturnsBadRequest()
    {
        var (service, _) = CreateService();

        var query = new AuditLogQueryDto { Action = "user.statuschanged" };
        var response = await service.GetAuditLogsAsync(query);

        Assert.False(response.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid action", response.Message);
    }

    [Fact]
    public async Task GetAuditLogsAsync_ActorUserIdAndTargetUserIdFilters_ReturnsMatchingRows()
    {
        var (service, db) = CreateService();

        var actor1 = Guid.NewGuid();
        var actor2 = Guid.NewGuid();
        var target1 = Guid.NewGuid();
        var target2 = Guid.NewGuid();

        db.AuditLogs.AddRange(
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorUserId = actor1, TargetUserId = target1, OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorUserId = actor1, TargetUserId = target2, OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorUserId = actor2, TargetUserId = target1, OccurredAtUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var actorQuery = await service.GetAuditLogsAsync(new AuditLogQueryDto { ActorUserId = actor1 });
        Assert.True(actorQuery.Succeeded);
        Assert.Equal(2, actorQuery.Data!.Count());

        var targetQuery = await service.GetAuditLogsAsync(new AuditLogQueryDto { TargetUserId = target2 });
        Assert.True(targetQuery.Succeeded);
        Assert.Single(targetQuery.Data!);
        Assert.Equal(target2, targetQuery.Data!.First().TargetUserId);
    }

    [Fact]
    public async Task GetAuditLogsAsync_SearchFilter_MatchesActorEmailOrTargetDisplay()
    {
        var (service, db) = CreateService();

        db.AuditLogs.AddRange(
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorEmail = "samira@admin.com", TargetDisplay = "User X", OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorEmail = "omar@admin.com", TargetDisplay = "samira.target@example.com", OccurredAtUtc = DateTime.UtcNow },
            new AuditLog { Action = AuditActions.UserStatusChanged, ActorEmail = "zayd@admin.com", TargetDisplay = "Other Person", OccurredAtUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var response = await service.GetAuditLogsAsync(new AuditLogQueryDto { Search = "samira" });
        Assert.True(response.Succeeded);

        var items = response.Data!.ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.ActorEmail == "samira@admin.com");
        Assert.Contains(items, i => i.TargetDisplay == "samira.target@example.com");
    }

    [Fact]
    public async Task GetAuditLogsAsync_FromLaterThanTo_ReturnsBadRequest()
    {
        var (service, _) = CreateService();

        var query = new AuditLogQueryDto
        {
            From = DateTime.UtcNow.AddHours(5),
            To = DateTime.UtcNow
        };
        var response = await service.GetAuditLogsAsync(query);

        Assert.False(response.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'from' must not be later than 'to'", response.Message);
    }

    [Fact]
    public async Task GetAuditLogsAsync_MalformedDetailsJson_ReturnsNullDetailsAndPageSucceeds()
    {
        var (service, db) = CreateService();

        db.AuditLogs.Add(new AuditLog
        {
            Action = AuditActions.UserStatusChanged,
            OccurredAtUtc = DateTime.UtcNow,
            DetailsJson = "{invalid-json-content"
        });
        await db.SaveChangesAsync();

        var response = await service.GetAuditLogsAsync(new AuditLogQueryDto());

        Assert.True(response.Succeeded);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single(response.Data!);
        Assert.Null(item.Details);
    }

    [Fact]
    public async Task GetAuditLogsAsync_ValidDetailsJson_ReturnsJsonObjectDetails()
    {
        var (service, db) = CreateService();

        db.AuditLogs.Add(new AuditLog
        {
            Action = AuditActions.UserStatusChanged,
            OccurredAtUtc = DateTime.UtcNow,
            DetailsJson = "{\"status\":\"Active\",\"count\":5}"
        });
        await db.SaveChangesAsync();

        var response = await service.GetAuditLogsAsync(new AuditLogQueryDto());

        Assert.True(response.Succeeded);
        var item = Assert.Single(response.Data!);
        Assert.NotNull(item.Details);
        Assert.True(item.Details.HasValue);
        Assert.Equal(JsonValueKind.Object, item.Details.Value.ValueKind);
        Assert.Equal("Active", item.Details.Value.GetProperty("status").GetString());
    }
}
