using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.API.Controllers;
using Cocorra.API.Hubs;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.AuditLogs;
using Cocorra.BLL.Services.EventTracking;
using Cocorra.BLL.Services.NotificationService;
using Cocorra.BLL.Services.RealTimeNotifier;
using Cocorra.BLL.Services.SupportService;
using Cocorra.BLL.Services.Upload;
using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.SupportChatDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.AuditLogRepository;
using Cocorra.DAL.Repository.NotificationRepository;
using Cocorra.DAL.Repository.SupportRepository;
using Cocorra.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Cocorra.Tests;

public class SupportChatScopingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public SupportChatScopingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var db = new AppDbContext(_dbOptions);
        db.Database.EnsureCreated();
        InstallSqliteRowVersionTrigger(db);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    // SQLite has no native SQL Server rowversion generator, so this trigger emulates rowversion
    // updates on SupportChats to exercise EF Core's optimistic concurrency token end-to-end.
    private static void InstallSqliteRowVersionTrigger(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS TR_SupportChats_BumpRowVersion
            AFTER UPDATE ON SupportChats
            WHEN NEW.RowVersion = OLD.RowVersion
            BEGIN
                UPDATE SupportChats SET RowVersion = randomblob(8) WHERE Id = NEW.Id;
            END;");
    }

    private static async Task<Guid> SeedChatAsync(
        AppDbContext db,
        string userId,
        SupportChatStatus status = SupportChatStatus.Pending,
        string? adminId = null,
        DateTime? createdAt = null,
        DateTime? closedAt = null)
    {
        var chatId = Guid.NewGuid();
        var created = createdAt ?? DateTime.UtcNow;
        var chatIdUpper = chatId.ToString().ToUpperInvariant();
        var statusInt = (int)status;

        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO SupportChats (Id, UserId, AdminId, Status, CreatedAt, ClosedAt, RowVersion)
            VALUES ({chatIdUpper}, {userId}, {adminId}, {statusInt}, {created}, {closedAt}, X'0000000000000001')");

        return chatId;
    }

    private static SupportService CreateService(
        AppDbContext db,
        ISupportRepository? repoOverride = null,
        UserManager<ApplicationUser>? userManagerOverride = null,
        IPushNotificationService? pushServiceOverride = null)
    {
        var userManager = userManagerOverride ?? TestIdentityHelper.CreateMockUserManager().Object;
        var httpContextAccessorMock = new Mock<IHttpContextAccessor>();
        var auditLog = new AuditLogService(
            new AuditLogRepository(db),
            httpContextAccessorMock.Object,
            NullLogger<AuditLogService>.Instance);

        return new SupportService(
            repoOverride ?? new SupportRepository(db),
            new Mock<IUploadImage>().Object,
            userManager,
            new NotificationRepository(db),
            new Mock<IRealTimeNotifier>().Object,
            pushServiceOverride ?? new Mock<IPushNotificationService>().Object,
            new Mock<IEventTracker>().Object,
            NullLogger<SupportService>.Instance,
            new FakePrivateFileUrlIssuer(),
            new ConfigurationBuilder().Build(),
            auditLog);
    }

    [Fact]
    public async Task GetPendingChatsAsync_ReturnsOnlyPendingChatsWithNullAdminId()
    {
        using var seedDb = new AppDbContext(_dbOptions);
        var validPendingId = await SeedChatAsync(seedDb, Guid.NewGuid().ToString(), SupportChatStatus.Pending, adminId: null);
        await SeedChatAsync(seedDb, Guid.NewGuid().ToString(), SupportChatStatus.Pending, adminId: Guid.NewGuid().ToString());
        await SeedChatAsync(seedDb, Guid.NewGuid().ToString(), SupportChatStatus.Active, adminId: Guid.NewGuid().ToString());
        await SeedChatAsync(seedDb, Guid.NewGuid().ToString(), SupportChatStatus.Closed, adminId: Guid.NewGuid().ToString(), closedAt: DateTime.UtcNow);

        using var db = new AppDbContext(_dbOptions);
        var service = CreateService(db);

        var result = await service.GetPendingChatsAsync(1, 20);

        Assert.True(result.Succeeded);
        var item = Assert.Single(result.Data!);
        Assert.Equal(validPendingId, item.Id);
        Assert.Equal(SupportChatStatus.Pending, item.Status);
    }

    [Fact]
    public async Task ClaimChatAsync_AlreadyClaimedChat_RefusedLeavesOwnerAndAuditUntouched()
    {
        var userId = Guid.NewGuid();
        var mod1 = Guid.NewGuid();
        var mod2 = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Pending, adminId: null);
        }

        using (var db1 = new AppDbContext(_dbOptions))
        {
            var firstClaim = await CreateService(db1).ClaimChatAsync(chatId, mod1.ToString());
            Assert.True(firstClaim.Succeeded);
        }

        using (var db2 = new AppDbContext(_dbOptions))
        {
            var secondClaim = await CreateService(db2).ClaimChatAsync(chatId, mod2.ToString());
            Assert.False(secondClaim.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, secondClaim.StatusCode);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(mod1.ToString(), persistedChat.AdminId);
        Assert.Equal(SupportChatStatus.Active, persistedChat.Status);

        var audit = Assert.Single(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.SupportChatClaimed, audit.Action);
        Assert.Equal(AuditCategories.SupportChat, audit.Category);
        Assert.Equal(AuditEntityTypes.SupportChat, audit.EntityType);
        Assert.Equal(chatId.ToString(), audit.EntityId);
        Assert.Equal(mod1, audit.ActorUserId);
        Assert.Equal(userId, audit.TargetUserId);
    }

    [Fact]
    public async Task ClaimChatAsync_ConcurrentRaceAfterBothReadPending_ExactlyOneSucceedsAndOneAuditRow()
    {
        var tempFile = Path.GetTempFileName();
        var connectionString = $"Data Source={tempFile};Default Timeout=30;";

        try
        {
            using var conn1 = new SqliteConnection(connectionString);
            using var conn2 = new SqliteConnection(connectionString);
            await conn1.OpenAsync();
            await conn2.OpenAsync();

            var opts1 = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn1).Options;
            var opts2 = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn2).Options;

            var userId = Guid.NewGuid();
            var mod1 = Guid.NewGuid();
            var mod2 = Guid.NewGuid();
            Guid chatId;

            using (var setupDb = new AppDbContext(opts1))
            {
                await setupDb.Database.EnsureCreatedAsync();
                InstallSqliteRowVersionTrigger(setupDb);
                chatId = await SeedChatAsync(setupDb, userId.ToString(), SupportChatStatus.Pending, adminId: null);
            }

            var barrier = new AsyncTwoPartyBarrier();

            using var db1 = new AppDbContext(opts1);
            using var db2 = new AppDbContext(opts2);
            var service1 = CreateService(db1, new BarrierOnGetChatRepository(new SupportRepository(db1), barrier));
            var service2 = CreateService(db2, new BarrierOnGetChatRepository(new SupportRepository(db2), barrier));

            var results = await Task.WhenAll(
                service1.ClaimChatAsync(chatId, mod1.ToString()),
                service2.ClaimChatAsync(chatId, mod2.ToString()));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.Equal(1, results.Count(r => !r.Succeeded && r.StatusCode == HttpStatusCode.BadRequest));

            var winnerId = results[0].Succeeded ? mod1 : mod2;

            using var verifyDb = new AppDbContext(opts1);
            var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
            Assert.Equal(SupportChatStatus.Active, persistedChat.Status);
            Assert.Equal(winnerId.ToString(), persistedChat.AdminId);

            var claimedAudits = await verifyDb.AuditLogs.AsNoTracking()
                .Where(a => a.Action == AuditActions.SupportChatClaimed)
                .ToListAsync();
            var audit = Assert.Single(claimedAudits);
            Assert.Equal(winnerId, audit.ActorUserId);
            Assert.Equal(userId, audit.TargetUserId);
            Assert.Equal(AuditCategories.SupportChat, audit.Category);
            Assert.Equal(AuditEntityTypes.SupportChat, audit.EntityType);
            Assert.Equal(chatId.ToString(), audit.EntityId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public async Task AdminReplyAsync_ByClaimer_PersistsMessageAndAuditWithMessageIdOnlyNeverMessageText()
    {
        const string secretMessageText = "CONFIDENTIAL_USER_DISPUTE_BODY_7741";
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: claimerId.ToString());
        }

        Guid sentMessageId;
        using (var db = new AppDbContext(_dbOptions))
        {
            var result = await CreateService(db).AdminReplyAsync(
                chatId,
                claimerId.ToString(),
                new SendMessageDto { Content = secretMessageText },
                callerIsAdmin: false);

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Data);
            sentMessageId = result.Data!.Message.Id;
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var message = await verifyDb.SupportMessages.AsNoTracking().SingleAsync(m => m.Id == sentMessageId);
        Assert.Equal(secretMessageText, message.Content);
        Assert.True(message.IsFromAdmin);

        var audit = Assert.Single(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.SupportChatReplied, audit.Action);
        Assert.Equal(AuditCategories.SupportChat, audit.Category);
        Assert.Equal(AuditEntityTypes.SupportChat, audit.EntityType);
        Assert.Equal(chatId.ToString(), audit.EntityId);
        Assert.Equal(claimerId, audit.ActorUserId);
        Assert.Equal(userId, audit.TargetUserId);

        Assert.NotNull(audit.DetailsJson);
        Assert.DoesNotContain(secretMessageText, audit.DetailsJson!, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(audit.DetailsJson!);
        Assert.Equal(sentMessageId, doc.RootElement.GetProperty("messageId").GetGuid());
        Assert.Single(doc.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task AdminReplyAsync_ByNonClaimer_RefusedLeavesMessagesAndAuditUntouched()
    {
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid();
        var otherModId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: claimerId.ToString());
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var result = await CreateService(db).AdminReplyAsync(
                chatId,
                otherModId.ToString(),
                new SendMessageDto { Content = "Unauthorized reply" },
                callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        Assert.Empty(await verifyDb.SupportMessages.AsNoTracking().ToListAsync());
        Assert.Empty(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CloseChatAsync_ByClaimer_ClosesChatAndPersistsAuditRow()
    {
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: claimerId.ToString());
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var result = await CreateService(db).CloseChatAsync(chatId, claimerId.ToString(), callerIsAdmin: false);

            Assert.True(result.Succeeded);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(SupportChatStatus.Closed, persistedChat.Status);
        Assert.NotNull(persistedChat.ClosedAt);

        var audit = Assert.Single(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.SupportChatClosed, audit.Action);
        Assert.Equal(AuditCategories.SupportChat, audit.Category);
        Assert.Equal(AuditEntityTypes.SupportChat, audit.EntityType);
        Assert.Equal(chatId.ToString(), audit.EntityId);
        Assert.Equal(claimerId, audit.ActorUserId);
        Assert.Equal(userId, audit.TargetUserId);
    }

    [Fact]
    public async Task CloseChatAsync_ByNonClaimer_RefusedLeavesChatOpenAndWritesNoAudit()
    {
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid();
        var otherModId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: claimerId.ToString());
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var result = await CreateService(db).CloseChatAsync(chatId, otherModId.ToString(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(SupportChatStatus.Active, persistedChat.Status);
        Assert.Null(persistedChat.ClosedAt);
        Assert.Empty(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CloseChatAsync_AlreadyClosed_RefusedWithNoExtraAudit()
    {
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Closed, adminId: claimerId.ToString(), closedAt: DateTime.UtcNow);
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var result = await CreateService(db).CloseChatAsync(chatId, claimerId.ToString(), callerIsAdmin: false);

            Assert.False(result.Succeeded);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        Assert.Empty(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SendMessageAsync_ActiveClaimedChat_SetsInternalClaimedByAdminIdAndExcludesItFromJson()
    {
        var userId = Guid.NewGuid().ToString();
        var claimerGuid = Guid.NewGuid();
        var claimerId = claimerGuid.ToString();

        using (var seedDb = new AppDbContext(_dbOptions))
        {
            await SeedChatAsync(seedDb, userId, SupportChatStatus.Active, adminId: claimerId);
        }

        var userManagerMock = TestIdentityHelper.CreateMockUserManager();
        var activeMod = new ApplicationUser { Id = claimerGuid, Status = UserStatus.Active };
        userManagerMock.Setup(m => m.FindByIdAsync(claimerId)).ReturnsAsync(activeMod);
        userManagerMock.Setup(m => m.GetRolesAsync(activeMod)).ReturnsAsync(new List<string> { AppRoles.Moderator });

        using var db = new AppDbContext(_dbOptions);
        var result = await CreateService(db, userManagerOverride: userManagerMock.Object)
            .SendMessageAsync(userId, new SendMessageDto { Content = "Checking in" });

        Assert.True(result.Succeeded);
        Assert.Equal(claimerId, result.Data!.ClaimedByAdminId);

        var serialized = JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain(claimerId, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ClaimedByAdminId", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdminCaller_CanReplyToAndCloseActiveChatClaimedByAnotherModerator()
    {
        var userId = Guid.NewGuid();
        var modClaimerId = Guid.NewGuid();
        var adminCallerId = Guid.NewGuid();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: modClaimerId.ToString());
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var svc = CreateService(db);
            var reply = await svc.AdminReplyAsync(chatId, adminCallerId.ToString(), new SendMessageDto { Content = "Admin taking over" }, callerIsAdmin: true);
            Assert.True(reply.Succeeded);

            var close = await svc.CloseChatAsync(chatId, adminCallerId.ToString(), callerIsAdmin: true);
            Assert.True(close.Succeeded);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(SupportChatStatus.Closed, persistedChat.Status);
        Assert.NotNull(persistedChat.ClosedAt);

        var msg = Assert.Single(await verifyDb.SupportMessages.AsNoTracking().ToListAsync());
        Assert.Equal(adminCallerId.ToString(), msg.SenderId);
        Assert.True(msg.IsFromAdmin);

        var audits = await verifyDb.AuditLogs.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(AuditActions.SupportChatReplied, audits[0].Action);
        Assert.Equal(adminCallerId, audits[0].ActorUserId);
        Assert.Equal(AuditActions.SupportChatClosed, audits[1].Action);
        Assert.Equal(adminCallerId, audits[1].ActorUserId);
    }

    [Fact]
    public async Task CloseChatAsync_PendingChat_RefusedForAdminAndModerator()
    {
        var userId = Guid.NewGuid();
        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Pending, adminId: null);
        }

        using (var db = new AppDbContext(_dbOptions))
        {
            var svc = CreateService(db);
            Assert.Equal(HttpStatusCode.BadRequest, (await svc.CloseChatAsync(chatId, Guid.NewGuid().ToString(), callerIsAdmin: true)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await svc.CloseChatAsync(chatId, Guid.NewGuid().ToString(), callerIsAdmin: false)).StatusCode);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persisted = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(SupportChatStatus.Pending, persisted.Status);
        Assert.Null(persisted.ClosedAt);
        Assert.Empty(await verifyDb.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(AppRoles.User, UserStatus.Active, false)]
    [InlineData(AppRoles.Moderator, UserStatus.Banned, false)]
    [InlineData(AppRoles.Moderator, UserStatus.Active, true)]
    public async Task SendMessage_OrphanedClaimedChat_ReleasesToPendingAlertsAdminsGroupAndSendsNothingToFormerClaimer(
        string claimerRole, UserStatus claimerStatus, bool claimerLockedOut)
    {
        var userId = Guid.NewGuid();
        var formerClaimerId = Guid.NewGuid();
        var formerClaimerIdStr = formerClaimerId.ToString();

        Guid chatId;
        using (var seedDb = new AppDbContext(_dbOptions))
        {
            chatId = await SeedChatAsync(seedDb, userId.ToString(), SupportChatStatus.Active, adminId: formerClaimerIdStr);
            for (var i = 0; i < 3; i++)
            {
                seedDb.SupportMessages.Add(new SupportMessage
                {
                    Id = Guid.NewGuid(),
                    SupportChatId = chatId,
                    SenderId = userId.ToString(),
                    Content = $"Prior message {i}",
                    IsFromAdmin = false,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-5 + i)
                });
            }
            await seedDb.SaveChangesAsync();
        }

        var userManagerMock = TestIdentityHelper.CreateMockUserManager();
        var formerClaimer = new ApplicationUser
        {
            Id = formerClaimerId,
            Status = claimerStatus,
            FcmToken = "former_claimer_fcm"
        };
        userManagerMock.Setup(m => m.FindByIdAsync(formerClaimerIdStr)).ReturnsAsync(formerClaimer);
        userManagerMock.Setup(m => m.GetRolesAsync(formerClaimer)).ReturnsAsync(new List<string> { claimerRole });
        userManagerMock.Setup(m => m.IsLockedOutAsync(formerClaimer)).ReturnsAsync(claimerLockedOut);

        var pushMock = new Mock<IPushNotificationService>();

        var hubMock = new Mock<IHubContext<SupportHub>>();
        var clientsMock = new Mock<IHubClients>();
        var adminsProxyMock = new Mock<IClientProxy>();
        var supportAdminsProxyMock = new Mock<IClientProxy>();
        var formerClaimerProxyMock = new Mock<IClientProxy>();

        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.AdminsGroup)).Returns(adminsProxyMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.SupportAdminsGroup)).Returns(supportAdminsProxyMock.Object);
        clientsMock.Setup(c => c.User(formerClaimerIdStr)).Returns(formerClaimerProxyMock.Object);

        using (var db = new AppDbContext(_dbOptions))
        {
            var service = CreateService(db, userManagerOverride: userManagerMock.Object, pushServiceOverride: pushMock.Object);
            var controller = CreateAuthenticatedController(service, hubMock.Object, userId);

            var actionResult = await controller.SendMessage(new SendMessageDto { Content = "Hello, anyone still here?" });
            var obj = Assert.IsType<ObjectResult>(actionResult);
            Assert.Equal(200, obj.StatusCode);

            var response = Assert.IsType<Response<SendMessageResultDto>>(obj.Value);
            Assert.True(response.Succeeded);
            Assert.Null(response.Data!.ClaimedByAdminId);
        }

        using var verifyDb = new AppDbContext(_dbOptions);
        var persistedChat = await verifyDb.SupportChats.AsNoTracking().SingleAsync(c => c.Id == chatId);
        Assert.Equal(SupportChatStatus.Pending, persistedChat.Status);
        Assert.Null(persistedChat.AdminId);

        adminsProxyMock.Verify(
            p => p.SendCoreAsync("NewPendingChatAlert", It.Is<object?[]>(args => args.Length == 0), default),
            Times.Once);
        adminsProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveSupportMessage", It.Is<object?[]>(args => args.Length == 1), default),
            Times.Once);
        formerClaimerProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
        supportAdminsProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
        clientsMock.Verify(c => c.User(It.IsAny<string>()), Times.Never);
        pushMock.Verify(
            p => p.SendPushNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>>()),
            Times.Never);
    }

    [Fact]
    public async Task Controller_SendMessage_ClaimedChat_SendsReceiveSupportMessageOnlyToClaimerAndSupportAdminsNeverToAdmins()
    {
        var userId = Guid.NewGuid();
        var claimerId = Guid.NewGuid().ToString();
        var messageDto = new SupportMessageDto { Id = Guid.NewGuid(), Content = "Private follow-up" };
        var dto = new SendMessageDto { Content = "Private follow-up" };

        var supportServiceMock = new Mock<ISupportService>();
        supportServiceMock
            .Setup(s => s.SendMessageAsync(userId.ToString(), dto))
            .ReturnsAsync(new Response<SendMessageResultDto>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = new SendMessageResultDto
                {
                    ChatId = Guid.NewGuid(),
                    IsNewChat = false,
                    ClaimedByAdminId = claimerId,
                    Message = messageDto
                }
            });

        var hubMock = new Mock<IHubContext<SupportHub>>();
        var clientsMock = new Mock<IHubClients>();
        var claimerProxyMock = new Mock<IClientProxy>();
        var supportAdminsProxyMock = new Mock<IClientProxy>();
        var adminsProxyMock = new Mock<IClientProxy>();

        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.User(claimerId)).Returns(claimerProxyMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.SupportAdminsGroup)).Returns(supportAdminsProxyMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.AdminsGroup)).Returns(adminsProxyMock.Object);

        var controller = CreateAuthenticatedController(supportServiceMock.Object, hubMock.Object, userId);

        var actionResult = await controller.SendMessage(dto);

        var obj = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(200, obj.StatusCode);

        claimerProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveSupportMessage", It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], messageDto)), default),
            Times.Once);
        supportAdminsProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveSupportMessage", It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], messageDto)), default),
            Times.Once);
        adminsProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
        clientsMock.Verify(c => c.Group(SupportHub.AdminsGroup), Times.Never);
    }

    [Fact]
    public async Task Controller_SendMessage_UnclaimedNewChat_AlertsAdminsGroupOnly()
    {
        var userId = Guid.NewGuid();
        var dto = new SendMessageDto { Content = "Need help" };

        var supportServiceMock = new Mock<ISupportService>();
        supportServiceMock
            .Setup(s => s.SendMessageAsync(userId.ToString(), dto))
            .ReturnsAsync(new Response<SendMessageResultDto>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = new SendMessageResultDto
                {
                    ChatId = Guid.NewGuid(),
                    IsNewChat = true,
                    ClaimedByAdminId = null,
                    Message = new SupportMessageDto { Id = Guid.NewGuid(), Content = "Need help" }
                }
            });

        var hubMock = new Mock<IHubContext<SupportHub>>();
        var clientsMock = new Mock<IHubClients>();
        var adminsProxyMock = new Mock<IClientProxy>();
        var supportAdminsProxyMock = new Mock<IClientProxy>();

        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.AdminsGroup)).Returns(adminsProxyMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.SupportAdminsGroup)).Returns(supportAdminsProxyMock.Object);

        var controller = CreateAuthenticatedController(supportServiceMock.Object, hubMock.Object, userId);

        var actionResult = await controller.SendMessage(dto);

        var obj = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(200, obj.StatusCode);

        adminsProxyMock.Verify(
            p => p.SendCoreAsync("NewPendingChatAlert", It.Is<object?[]>(args => args.Length == 0), default),
            Times.Once);
        supportAdminsProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
        clientsMock.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Controller_SendMessage_UnclaimedFollowUp_AlertsAndBroadcastsMessageToAdminsGroupOnly()
    {
        var userId = Guid.NewGuid();
        var messageDto = new SupportMessageDto { Id = Guid.NewGuid(), Content = "Second pending message" };
        var dto = new SendMessageDto { Content = "Second pending message" };

        var supportServiceMock = new Mock<ISupportService>();
        supportServiceMock
            .Setup(s => s.SendMessageAsync(userId.ToString(), dto))
            .ReturnsAsync(new Response<SendMessageResultDto>
            {
                Succeeded = true,
                StatusCode = HttpStatusCode.OK,
                Data = new SendMessageResultDto
                {
                    ChatId = Guid.NewGuid(),
                    IsNewChat = false,
                    ClaimedByAdminId = null,
                    Message = messageDto
                }
            });

        var hubMock = new Mock<IHubContext<SupportHub>>();
        var clientsMock = new Mock<IHubClients>();
        var adminsProxyMock = new Mock<IClientProxy>();
        var supportAdminsProxyMock = new Mock<IClientProxy>();

        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.AdminsGroup)).Returns(adminsProxyMock.Object);
        clientsMock.Setup(c => c.Group(SupportHub.SupportAdminsGroup)).Returns(supportAdminsProxyMock.Object);

        var controller = CreateAuthenticatedController(supportServiceMock.Object, hubMock.Object, userId);

        var actionResult = await controller.SendMessage(dto);

        var obj = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(200, obj.StatusCode);

        adminsProxyMock.Verify(
            p => p.SendCoreAsync("NewPendingChatAlert", It.Is<object?[]>(args => args.Length == 0), default),
            Times.Once);
        adminsProxyMock.Verify(
            p => p.SendCoreAsync("ReceiveSupportMessage", It.Is<object?[]>(args => args.Length == 1 && ReferenceEquals(args[0], messageDto)), default),
            Times.Once);
        supportAdminsProxyMock.Verify(
            p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), default),
            Times.Never);
        clientsMock.Verify(c => c.User(It.IsAny<string>()), Times.Never);
    }

    private static SupportController CreateAuthenticatedController(
        ISupportService supportService,
        IHubContext<SupportHub> supportHub,
        Guid userId)
    {
        var controller = new SupportController(supportService, supportHub);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Role, AppRoles.User)
        }, "TestAuth");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private sealed class AsyncTwoPartyBarrier
    {
        private int _arrived;
        private readonly TaskCompletionSource _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SignalAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= 2)
            {
                _bothArrived.TrySetResult();
            }

            return _bothArrived.Task;
        }
    }

    private sealed class BarrierOnGetChatRepository : ISupportRepository
    {
        private readonly ISupportRepository _inner;
        private readonly AsyncTwoPartyBarrier _barrier;

        public BarrierOnGetChatRepository(ISupportRepository inner, AsyncTwoPartyBarrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task<SupportChat?> GetChatByIdAsync(Guid chatId)
        {
            var chat = await _inner.GetChatByIdAsync(chatId);
            await _barrier.SignalAndWaitAsync();
            return chat;
        }

        public Task AddTicketAsync(SupportTicket ticket) => _inner.AddTicketAsync(ticket);
        public Task AddReportAsync(Report report) => _inner.AddReportAsync(report);
        public Task<(int TotalCount, List<Report> Items)> GetPagedReportsAsync(ReportCategory? category, string? status, int pageNumber, int pageSize, bool excludeAdmins = false) =>
            _inner.GetPagedReportsAsync(category, status, pageNumber, pageSize, excludeAdmins);
        public Task<Report?> GetReportByIdAsync(Guid reportId) => _inner.GetReportByIdAsync(reportId);
        public Task<Report?> GetReportDetailsByIdAsync(Guid reportId) => _inner.GetReportDetailsByIdAsync(reportId);
        public Task<SupportTicket?> GetTicketByIdAsync(Guid ticketId) => _inner.GetTicketByIdAsync(ticketId);
        public Task<SupportTicket?> GetTicketDetailsByIdAsync(Guid ticketId) => _inner.GetTicketDetailsByIdAsync(ticketId);
        public Task<(int TotalCount, List<SupportTicket> Items)> GetPagedTicketsAsync(SupportTicketType? type, SupportTicketStatus? status, int pageNumber, int pageSize, bool excludeAdmins = false) =>
            _inner.GetPagedTicketsAsync(type, status, pageNumber, pageSize, excludeAdmins);
        public Task UpdateTicketAsync(SupportTicket ticket) => _inner.UpdateTicketAsync(ticket);
        public Task UpdateReportAsync(Report report) => _inner.UpdateReportAsync(report);
        public Task<SupportChat?> GetUserOpenChatAsync(string userId) => _inner.GetUserOpenChatAsync(userId);
        public Task<int> GetPendingUserMessageCountAsync(Guid chatId) => _inner.GetPendingUserMessageCountAsync(chatId);
        public Task AddChatAsync(SupportChat chat) => _inner.AddChatAsync(chat);
        public Task AddMessageAsync(SupportMessage message) => _inner.AddMessageAsync(message);
        public Task UpdateChatAsync(SupportChat chat) => _inner.UpdateChatAsync(chat);
        public Task ReleaseActiveChatsForStaffAsync(string adminId) => _inner.ReleaseActiveChatsForStaffAsync(adminId);
        public Task<List<SupportChat>> GetPendingChatsAsync(int pageNumber, int pageSize) => _inner.GetPendingChatsAsync(pageNumber, pageSize);
        public Task<int> GetPendingChatsCountAsync() => _inner.GetPendingChatsCountAsync();
        public Task<List<SupportChat>> GetAdminActiveChatsAsync(string adminId, int pageNumber, int pageSize) =>
            _inner.GetAdminActiveChatsAsync(adminId, pageNumber, pageSize);
        public Task<int> GetAdminActiveChatsCountAsync(string adminId) => _inner.GetAdminActiveChatsCountAsync(adminId);
        public Task<List<SupportChat>> GetUserChatHistoryAsync(string userId, int pageNumber, int pageSize) =>
            _inner.GetUserChatHistoryAsync(userId, pageNumber, pageSize);
        public Task<int> GetUserChatHistoryCountAsync(string userId) => _inner.GetUserChatHistoryCountAsync(userId);
    }
}
