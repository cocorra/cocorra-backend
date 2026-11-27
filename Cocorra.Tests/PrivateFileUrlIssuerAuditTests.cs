using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.Storage;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class PrivateFileUrlIssuerAuditTests : IDisposable
{
    private readonly SqliteTestHost _host = new();
    private readonly Mock<IStorageUrlSigner> _signerMock = new();

    public PrivateFileUrlIssuerAuditTests()
    {
        _signerMock.Setup(s => s.DefaultTtl).Returns(TimeSpan.FromMinutes(15));
        _signerMock.Setup(s => s.GetObjectKey(It.IsAny<string?>()))
            .Returns((string? stored) => string.IsNullOrWhiteSpace(stored) ? null : stored.Trim());
        _signerMock.Setup(s => s.GetReadUrl(It.IsAny<string?>(), It.IsAny<TimeSpan?>()))
            .Returns((string? key, TimeSpan? _) => string.IsNullOrWhiteSpace(key) ? null : $"https://storage.test/{key}?sig=1");
    }

    public void Dispose() => _host.Dispose();

    private (PrivateFileUrlIssuer issuer, AppDbContext db) CreateIssuer(Guid actorId, string role)
    {
        var scope = _host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, actorId.ToString()),
                new Claim(ClaimTypes.Email, "staff@cocorra.com"),
                new Claim(ClaimTypes.Role, role)
            }, "TestAuth"))
        };
        var httpContextAccessor = new Mock<IHttpContextAccessor>();
        httpContextAccessor.Setup(a => a.HttpContext).Returns(httpContext);

        var auditLogRepo = new AuditLogRepository(db);
        var auditLogService = new AuditLogService(auditLogRepo, httpContextAccessor.Object, NullLogger<AuditLogService>.Instance);
        var issuer = new PrivateFileUrlIssuer(_signerMock.Object, auditLogService, httpContextAccessor.Object);
        return (issuer, db);
    }

    [Theory]
    [InlineData(PrivateFileKind.Voice, AuditActions.VoiceUrlIssued, AuditCategories.VoiceReview, AuditEntityTypes.User, "VoiceReview.VoiceUrl")]
    [InlineData(PrivateFileKind.ReportScreenshot, AuditActions.ScreenshotUrlIssued, AuditCategories.Report, AuditEntityTypes.Report, "Support.ReportScreenshot")]
    [InlineData(PrivateFileKind.TicketScreenshot, AuditActions.ScreenshotUrlIssued, AuditCategories.Ticket, AuditEntityTypes.Ticket, "Support.TicketScreenshot")]
    public async Task IssueAsync_SingleFile_PersistsAuditRowWithCategoryEntityTypeAndEntityId(
        PrivateFileKind kind,
        string expectedAction,
        string expectedCategory,
        string expectedEntityType,
        string source)
    {
        var actorId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var (issuer, db) = CreateIssuer(actorId, AppRoles.RecordReviewer);

        var results = await issuer.IssueAsync(
            kind,
            new[] { new PrivateFileRef(subjectId, "private/file-1.bin", targetUserId) },
            source);

        var issued = Assert.Single(results);
        Assert.NotNull(issued);
        Assert.Equal("https://storage.test/private/file-1.bin?sig=1", issued!.Url);

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(expectedAction, row.Action);
        Assert.Equal(expectedCategory, row.Category);
        Assert.Equal(expectedEntityType, row.EntityType);
        Assert.Equal(subjectId.ToString(), row.EntityId);
        Assert.Equal(actorId, row.ActorUserId);
        Assert.Equal(targetUserId, row.TargetUserId);
        Assert.Equal(AppRoles.RecordReviewer, row.ActorRole);

        using var doc = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal(source, doc.RootElement.GetProperty("source").GetString());
        Assert.Equal(kind.ToString(), doc.RootElement.GetProperty("kind").GetString());
        var files = doc.RootElement.GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("private/file-1.bin", files[0].GetProperty("objectKey").GetString());
    }

    [Theory]
    [InlineData(PrivateFileKind.Voice, AuditActions.VoiceUrlIssued, AuditCategories.VoiceReview, AuditEntityTypes.User)]
    [InlineData(PrivateFileKind.ReportScreenshot, AuditActions.ScreenshotUrlIssued, AuditCategories.Report, AuditEntityTypes.Report)]
    [InlineData(PrivateFileKind.TicketScreenshot, AuditActions.ScreenshotUrlIssued, AuditCategories.Ticket, AuditEntityTypes.Ticket)]
    public async Task IssueAsync_MultipleFiles_PersistsSingleAuditRowWithNullEntityIdAndTargetUserId(
        PrivateFileKind kind,
        string expectedAction,
        string expectedCategory,
        string expectedEntityType)
    {
        var actorId = Guid.NewGuid();
        var (issuer, db) = CreateIssuer(actorId, AppRoles.Moderator);

        var results = await issuer.IssueAsync(
            kind,
            new[]
            {
                new PrivateFileRef(Guid.NewGuid(), "private/file-a.bin", Guid.NewGuid()),
                new PrivateFileRef(Guid.NewGuid(), "private/file-b.bin", Guid.NewGuid())
            },
            "Batch.Source");

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.NotNull(r));

        var row = await db.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal(expectedAction, row.Action);
        Assert.Equal(expectedCategory, row.Category);
        Assert.Equal(expectedEntityType, row.EntityType);
        Assert.Null(row.EntityId);
        Assert.Null(row.TargetUserId);
        Assert.Equal(actorId, row.ActorUserId);

        using var doc = JsonDocument.Parse(row.DetailsJson!);
        Assert.Equal(2, doc.RootElement.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task IssueAsync_NoValidFiles_WritesNoAuditRow()
    {
        var (issuer, db) = CreateIssuer(Guid.NewGuid(), AppRoles.Admin);

        var results = await issuer.IssueAsync(
            PrivateFileKind.Voice,
            new[] { new PrivateFileRef(Guid.NewGuid(), null, Guid.NewGuid()) },
            "Admin.VoiceUrl");

        Assert.Null(Assert.Single(results));
        Assert.Equal(0, await db.AuditLogs.CountAsync());
    }
}
