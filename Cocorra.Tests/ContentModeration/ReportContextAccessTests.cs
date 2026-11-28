using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class ReportContextAccessTests : IDisposable
{
    private readonly TextRoomTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<TextRoom> SeedRoomAsync(Guid? createdById = null)
    {
        var room = new TextRoom
        {
            Id = Guid.NewGuid(),
            Title = "Moderation Room",
            Status = TextRoomStatus.Active,
            CreatedById = createdById,
            Settings = new TextRoomSettings(),
            OpensAt = _host.Clock.UtcNow.AddMinutes(-10),
            CreatedAt = _host.Clock.UtcNow.AddMinutes(-10)
        };
        await _host.WithDb(async db =>
        {
            db.TextRooms.Add(room);
            await db.SaveChangesAsync();
        });
        return room;
    }

    private async Task SeedParticipantAsync(Guid roomId, Guid userId)
    {
        await _host.WithDb(async db =>
        {
            db.TextRoomParticipants.Add(new TextRoomParticipant
            {
                TextRoomId = roomId,
                UserId = userId,
                FirstJoinedAt = _host.Clock.UtcNow,
                LastSeenAt = _host.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        });
    }

    private async Task<TextRoomMessage> SeedMessageAsync(Guid roomId, Guid senderId, string content)
    {
        var message = new TextRoomMessage
        {
            TextRoomId = roomId,
            SenderId = senderId,
            Content = content,
            CreatedAt = _host.Clock.UtcNow
        };
        await _host.WithDb(async db =>
        {
            db.TextRoomMessages.Add(message);
            await db.SaveChangesAsync();
        });
        return message;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetReportContextAsync_ModeratorOpeningAdminInvolvedReport_ReturnsNotFoundNoAudit_AdminSucceeds(
        bool adminIsReportedUser)
    {
        var adminParticipant = await _host.CreateUserAsync(AppRoles.Admin, "Involved", "Admin");
        var adminViewer = await _host.CreateUserAsync(AppRoles.Admin, "Viewer", "Admin");
        var moderator = await _host.CreateUserAsync(AppRoles.Moderator, "Mod", "One");
        var regularUser = await _host.CreateUserAsync(AppRoles.User, "Plain", "User");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, adminParticipant.Id);
        await SeedParticipantAsync(room.Id, regularUser.Id);

        var senderId = adminIsReportedUser ? adminParticipant.Id : regularUser.Id;
        var reporterId = adminIsReportedUser ? regularUser.Id : adminParticipant.Id;

        var msg = await SeedMessageAsync(room.Id, senderId, "Admin-involved message");
        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporterId, msg.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Harassment }));
        Assert.True(report.Succeeded);

        var modResult = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(report.Data!.ReportId, TextRoomTestHost.ModeratorActor(moderator)));
        Assert.False(modResult.Succeeded);
        Assert.Equal(HttpStatusCode.NotFound, modResult.StatusCode);
        Assert.Empty(_host.Audit.Entries);

        var adminResult = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(report.Data!.ReportId, TextRoomTestHost.AdminActor(adminViewer)));
        Assert.True(adminResult.Succeeded);
        Assert.Equal(HttpStatusCode.OK, adminResult.StatusCode);
        Assert.Single(_host.Audit.ByAction(AuditActions.ReportContextViewed));
    }

    [Fact]
    public async Task GetReportContextAsync_ContextContainingAdminLine_MasksAdminAsStaffForModeratorAndShowsRealSenderForAdmin()
    {
        var adminCreator = await _host.CreateUserAsync(AppRoles.Admin, "Super", "Admin");
        var moderator = await _host.CreateUserAsync(AppRoles.Moderator, "Mod", "Viewer");
        var sender = await _host.CreateUserAsync(AppRoles.User, "Alice", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Bob", "Reporter");

        // Room created by Admin: moderator must still be able to inspect reports between regular users.
        var room = await SeedRoomAsync(createdById: adminCreator.Id);
        foreach (var u in new[] { adminCreator, sender, reporter })
            await SeedParticipantAsync(room.Id, u.Id);

        var adminMsg = await SeedMessageAsync(room.Id, adminCreator.Id, "Please keep it civil.");
        var reportedMsg = await SeedMessageAsync(room.Id, sender.Id, "Rule-breaking reply");
        var regularAfter = await SeedMessageAsync(room.Id, reporter.Id, "Reporting that");

        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, reportedMsg.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Harassment }));
        Assert.True(report.Succeeded);

        var modView = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(report.Data!.ReportId, TextRoomTestHost.ModeratorActor(moderator)));
        Assert.True(modView.Succeeded);
        Assert.Equal(reportedMsg.Id, modView.Data!.ReportedMessageId);
        Assert.Equal(new[] { -1, 0, 1 }, modView.Data.Messages.Select(m => m.Offset).ToArray());

        var modAdminLine = Assert.Single(modView.Data.Messages, m => m.MessageId == adminMsg.Id);
        Assert.Null(modAdminLine.SenderId);
        Assert.Equal("Staff", modAdminLine.SenderName);

        var modReportedLine = Assert.Single(modView.Data.Messages, m => m.MessageId == reportedMsg.Id);
        Assert.Equal(sender.Id, modReportedLine.SenderId);
        Assert.Equal("Alice Sender", modReportedLine.SenderName);
        Assert.True(modReportedLine.IsReportedMessage);

        var modAfterLine = Assert.Single(modView.Data.Messages, m => m.MessageId == regularAfter.Id);
        Assert.Equal(reporter.Id, modAfterLine.SenderId);
        Assert.Equal("Bob Reporter", modAfterLine.SenderName);
        Assert.False(modAfterLine.IsReportedMessage);

        var adminView = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(report.Data!.ReportId, TextRoomTestHost.AdminActor(adminCreator)));
        Assert.True(adminView.Succeeded);

        var adminLineForAdmin = Assert.Single(adminView.Data!.Messages, m => m.MessageId == adminMsg.Id);
        Assert.Equal(adminCreator.Id, adminLineForAdmin.SenderId);
        Assert.Equal("Super Admin", adminLineForAdmin.SenderName);
    }

    [Fact]
    public async Task GetReportContextAsync_TwoSuccessfulOpens_WriteTwoAuditRowsAndNeverLeakEmails()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Main", "Admin");
        var moderator = await _host.CreateUserAsync(AppRoles.Moderator, "Mod", "User");
        var sender = await _host.CreateUserAsync(AppRoles.User, "Sender", "User");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Reporter", "User");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);

        var msg = await SeedMessageAsync(room.Id, sender.Id, "Check audit and email safety");
        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, msg.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(report.Succeeded);
        var reportId = report.Data!.ReportId;

        var firstOpen = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(reportId, TextRoomTestHost.ModeratorActor(moderator)));
        var secondOpen = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(reportId, TextRoomTestHost.AdminActor(admin)));

        Assert.True(firstOpen.Succeeded);
        Assert.True(secondOpen.Succeeded);

        var audits = _host.Audit.ByAction(AuditActions.ReportContextViewed);
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a =>
        {
            Assert.Equal(AuditCategories.Report, a.Category);
            Assert.Equal(AuditEntityTypes.Report, a.EntityType);
            Assert.Equal(reportId.ToString(), a.EntityId);
            Assert.Equal(sender.Id, a.TargetUserId);
        });
        Assert.Equal(moderator.Id, audits[0].ActorUserId);
        Assert.Equal(admin.Id, audits[1].ActorUserId);

        var seededEmails = new[] { admin.Email!, moderator.Email!, sender.Email!, reporter.Email! };
        foreach (var dto in new[] { firstOpen.Data!, secondOpen.Data! })
        {
            var json = JsonSerializer.Serialize(dto);
            foreach (var email in seededEmails)
                Assert.DoesNotContain(email, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("@example.test", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetReportContextAsync_ReportWithoutTextRoomMessageOrUnknownId_ReturnsNotFoundWithNoAudit()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Site", "Admin");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Plain", "Reporter");
        var reported = await _host.CreateUserAsync(AppRoles.User, "Plain", "Reported");

        var legacyReport = new Report
        {
            Id = Guid.NewGuid(),
            ReporterId = reporter.Id,
            ReportedUserId = reported.Id,
            Category = ReportCategory.Harassment,
            Description = "Voice report without text message",
            Status = "Open",
            StatusCode = ReportStatus.Open
        };
        await _host.WithDb(async db =>
        {
            db.Reports.Add(legacyReport);
            await db.SaveChangesAsync();
        });

        var legacyResult = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(legacyReport.Id, TextRoomTestHost.AdminActor(admin)));
        Assert.Equal(HttpStatusCode.NotFound, legacyResult.StatusCode);

        var missingResult = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(Guid.NewGuid(), TextRoomTestHost.AdminActor(admin)));
        Assert.Equal(HttpStatusCode.NotFound, missingResult.StatusCode);

        Assert.Empty(_host.Audit.Entries);
    }
}
