using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.ReportDto;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Cocorra.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Cocorra.Tests.ContentModeration;

public class TextRoomReportServiceTests : IDisposable
{
    private readonly TextRoomTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<TextRoom> SeedRoomAsync(TextRoomSettings? settings = null, TextRoomStatus status = TextRoomStatus.Active)
    {
        var room = new TextRoom
        {
            Id = Guid.NewGuid(),
            Title = "Test Lounge",
            Status = status,
            Settings = settings ?? new TextRoomSettings(),
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

    private async Task<TextRoomMessage> SeedMessageAsync(
        Guid roomId,
        Guid senderId,
        string content,
        bool isDeleted = false,
        DateTime? createdAt = null)
    {
        var message = new TextRoomMessage
        {
            TextRoomId = roomId,
            SenderId = senderId,
            Content = content,
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? _host.Clock.UtcNow : null,
            CreatedAt = createdAt ?? _host.Clock.UtcNow
        };
        await _host.WithDb(async db =>
        {
            db.TextRoomMessages.Add(message);
            await db.SaveChangesAsync();
        });
        return message;
    }

    private async Task<List<TextRoomMessage>> SeedMessagesInOrderAsync(Guid roomId, Guid senderId, int count, string prefix)
    {
        var result = new List<TextRoomMessage>(count);
        await _host.WithDb(async db =>
        {
            for (var i = 1; i <= count; i++)
            {
                var msg = new TextRoomMessage
                {
                    TextRoomId = roomId,
                    SenderId = senderId,
                    Content = $"{prefix}-{i}",
                    CreatedAt = _host.Clock.UtcNow.AddSeconds(i)
                };
                db.TextRoomMessages.Add(msg);
                await db.SaveChangesAsync();
                result.Add(msg);
            }
        });
        return result;
    }

    [Fact]
    public async Task ReportMessageAsync_OwnMessage_ReturnsBadRequest()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Self", "Sender");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        var message = await SeedMessageAsync(room.Id, sender.Id, "My own message");

        var result = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(sender.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(0, await _host.WithDb(db => db.Reports.CountAsync()));
    }

    [Fact]
    public async Task ReportMessageAsync_NonParticipant_ReturnsForbidden()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Room", "Member");
        var outsider = await _host.CreateUserAsync(AppRoles.User, "Room", "Outsider");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        var message = await SeedMessageAsync(room.Id, sender.Id, "Inside room");

        var result = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(outsider.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Harassment }));

        Assert.False(result.Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(0, await _host.WithDb(db => db.Reports.CountAsync()));
    }

    [Fact]
    public async Task ReportMessageAsync_MissingOrSoftDeletedMessage_ReturnsNotFound()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);
        var deleted = await SeedMessageAsync(room.Id, sender.Id, "Gone", isDeleted: true);

        var missingResult = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, 999_999L, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.Equal(HttpStatusCode.NotFound, missingResult.StatusCode);

        var deletedResult = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, deleted.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.Equal(HttpStatusCode.NotFound, deletedResult.StatusCode);
    }

    [Fact]
    public async Task ReportMessageAsync_DuplicateBySameReporter_ReturnsConflictSequentiallyAndUnderConcurrency()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter1 = await _host.CreateUserAsync(AppRoles.User, "First", "Reporter");
        var reporter2 = await _host.CreateUserAsync(AppRoles.User, "Second", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter1.Id);
        await SeedParticipantAsync(room.Id, reporter2.Id);
        var message = await SeedMessageAsync(room.Id, sender.Id, "Double report target");

        var first = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter1.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(first.Succeeded);

        var second = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter1.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.False(second.Succeeded);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var concurrentResults = await Task.WhenAll(
            _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
                s.ReportMessageAsync(reporter2.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam })),
            _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
                s.ReportMessageAsync(reporter2.Id, message.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam })));

        Assert.Equal(1, concurrentResults.Count(r => r.Succeeded));
        Assert.Equal(1, concurrentResults.Count(r => !r.Succeeded && r.StatusCode == HttpStatusCode.Conflict));

        var reporter2Rows = await _host.WithDb(db =>
            db.Reports.CountAsync(r => r.ReporterId == reporter2.Id && r.ReportedTextRoomMessageId == message.Id));
        Assert.Equal(1, reporter2Rows);
    }

    [Fact]
    public async Task ReportMessageAsync_HarassmentIsHighAndThirdReporterEscalatesOpenReportsWithoutRewritingResolved()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Bad", "Actor");
        var reporterResolved = await _host.CreateUserAsync(AppRoles.User, "Rep", "Zero");
        var reporter1 = await _host.CreateUserAsync(AppRoles.User, "Rep", "One");
        var reporter2 = await _host.CreateUserAsync(AppRoles.User, "Rep", "Two");
        var reporter3 = await _host.CreateUserAsync(AppRoles.User, "Rep", "Three");
        var room = await SeedRoomAsync();
        foreach (var u in new[] { sender, reporterResolved, reporter1, reporter2, reporter3 })
            await SeedParticipantAsync(room.Id, u.Id);

        var harassmentMessage = await SeedMessageAsync(room.Id, sender.Id, "Harassing message");
        var harassmentReport = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter1.Id, harassmentMessage.Id, new ReportTextRoomMessageDto
            {
                Category = ReportCategory.Harassment,
                Description = "Direct harassment"
            }));
        Assert.True(harassmentReport.Succeeded);
        Assert.Equal(ModerationSeverity.High, harassmentReport.Data!.Severity);
        Assert.Equal(nameof(ModerationSeverity.High), harassmentReport.Data.SeverityName);

        var flagged = await _host.WithDb(db => db.TextRoomMessages.SingleAsync(m => m.Id == harassmentMessage.Id));
        Assert.True(flagged.IsReported);

        // Close the harassment report so it does not skew the sender's open-report count for the pile-on test.
        await _host.WithDb(db => db.Reports
            .Where(r => r.Id == harassmentReport.Data.ReportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Resolved")
                .SetProperty(r => r.StatusCode, ReportStatus.Resolved)));

        var pileOnMessage = await SeedMessageAsync(room.Id, sender.Id, "Pile-on message");

        var resolvedEarly = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporterResolved.Id, pileOnMessage.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(resolvedEarly.Succeeded);
        Assert.Equal(ModerationSeverity.Low, resolvedEarly.Data!.Severity);

        await _host.WithDb(db => db.Reports
            .Where(r => r.Id == resolvedEarly.Data.ReportId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "Resolved")
                .SetProperty(r => r.StatusCode, ReportStatus.Resolved)));

        var open1 = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter1.Id, pileOnMessage.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        var open2 = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter2.Id, pileOnMessage.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        var open3 = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter3.Id, pileOnMessage.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));

        Assert.True(open1.Succeeded);
        Assert.True(open2.Succeeded);
        Assert.True(open3.Succeeded);
        // 3 open reports against sender in 24h (+1) plus 3+ distinct reporters on message (+1) raises Spam (Low) to High.
        Assert.Equal(ModerationSeverity.High, open3.Data!.Severity);

        var persisted = await _host.WithDb(db => db.Reports
            .AsNoTracking()
            .Where(r => r.ReportedTextRoomMessageId == pileOnMessage.Id)
            .ToDictionaryAsync(r => r.Id));

        Assert.Equal(ModerationSeverity.Low, persisted[resolvedEarly.Data.ReportId].Severity);
        Assert.Equal(ModerationSeverity.High, persisted[open1.Data!.ReportId].Severity);
        Assert.Equal(ModerationSeverity.High, persisted[open2.Data!.ReportId].Severity);
        Assert.Equal(ModerationSeverity.High, persisted[open3.Data.ReportId].Severity);
    }

    [Fact]
    public async Task ContextCapture_Default25Window_CapturesNearest25BeforeAndCompletesTailIdempotently()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Chat", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Chat", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);

        var beforeMessages = await SeedMessagesInOrderAsync(room.Id, sender.Id, 40, "before");
        var reported = await SeedMessageAsync(room.Id, sender.Id, "reported-41", createdAt: _host.Clock.UtcNow.AddSeconds(41));
        var initialAfter = await SeedMessagesInOrderAsync(room.Id, sender.Id, 10, "after");

        var reportResult = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, reported.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(reportResult.Succeeded);
        var reportId = reportResult.Data!.ReportId;

        var (reportBeforeCompletion, initialEntries) = await _host.WithDb(async db =>
        {
            var r = await db.Reports.AsNoTracking().SingleAsync(x => x.Id == reportId);
            var ctx = await db.ReportMessageContextEntries
                .AsNoTracking()
                .Where(c => c.ReportId == reportId)
                .OrderBy(c => c.Offset)
                .ToListAsync();
            return (r, ctx);
        });

        Assert.Equal(25, reportBeforeCompletion.ContextBeforeRequested);
        Assert.Equal(25, reportBeforeCompletion.ContextAfterRequested);
        Assert.Null(reportBeforeCompletion.ContextCompletedAt);
        Assert.Equal(36, initialEntries.Count);

        var capturedBefore = initialEntries.Where(e => e.Offset < 0).ToList();
        Assert.Equal(25, capturedBefore.Count);
        Assert.Equal(Enumerable.Range(-25, 25), capturedBefore.Select(e => e.Offset));
        Assert.Equal(beforeMessages.Skip(15).Select(m => m.Id), capturedBefore.Select(e => e.TextRoomMessageId));

        var center = Assert.Single(initialEntries, e => e.Offset == 0);
        Assert.Equal(reported.Id, center.TextRoomMessageId);

        var capturedInitialAfter = initialEntries.Where(e => e.Offset > 0).ToList();
        Assert.Equal(10, capturedInitialAfter.Count);
        Assert.Equal(Enumerable.Range(1, 10), capturedInitialAfter.Select(e => e.Offset));
        Assert.Equal(initialAfter.Select(m => m.Id), capturedInitialAfter.Select(e => e.TextRoomMessageId));

        var extraAfter = await SeedMessagesInOrderAsync(room.Id, sender.Id, 20, "more");

        var sealedCount = await _host.With<ITextRoomReportService, int>(s => s.CompletePendingContextsAsync());
        Assert.Equal(1, sealedCount);

        var (reportAfterCompletion, finalEntries) = await _host.WithDb(async db =>
        {
            var r = await db.Reports.AsNoTracking().SingleAsync(x => x.Id == reportId);
            var ctx = await db.ReportMessageContextEntries
                .AsNoTracking()
                .Where(c => c.ReportId == reportId)
                .OrderBy(c => c.Offset)
                .ToListAsync();
            return (r, ctx);
        });

        Assert.NotNull(reportAfterCompletion.ContextCompletedAt);
        Assert.Equal(51, finalEntries.Count);

        var finalAfter = finalEntries.Where(e => e.Offset > 0).ToList();
        Assert.Equal(25, finalAfter.Count);
        Assert.Equal(Enumerable.Range(1, 25), finalAfter.Select(e => e.Offset));
        var expectedAfterIds = initialAfter.Concat(extraAfter.Take(15)).Select(m => m.Id);
        Assert.Equal(expectedAfterIds, finalAfter.Select(e => e.TextRoomMessageId));

        var secondRun = await _host.With<ITextRoomReportService, int>(s => s.CompletePendingContextsAsync());
        Assert.Equal(0, secondRun);
        var countAfterSecondRun = await _host.WithDb(db => db.ReportMessageContextEntries.CountAsync(c => c.ReportId == reportId));
        Assert.Equal(51, countAfterSecondRun);
    }

    [Fact]
    public async Task ContextCapture_RoomOverride3And0_SealsImmediately_AndGlobalSettings5And5AppliesToNewReports()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Global", "Admin");
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");

        var overrideRoom = await SeedRoomAsync(new TextRoomSettings
        {
            ReportContextMessagesBefore = 3,
            ReportContextMessagesAfter = 0
        });
        await SeedParticipantAsync(overrideRoom.Id, sender.Id);
        await SeedParticipantAsync(overrideRoom.Id, reporter.Id);

        await SeedMessagesInOrderAsync(overrideRoom.Id, sender.Id, 8, "ovr-before");
        var overrideTarget = await SeedMessageAsync(overrideRoom.Id, sender.Id, "ovr-target");
        await SeedMessagesInOrderAsync(overrideRoom.Id, sender.Id, 5, "ovr-after");

        var overrideReport = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, overrideTarget.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(overrideReport.Succeeded);

        var (ovrRow, ovrEntries) = await _host.WithDb(async db =>
        {
            var r = await db.Reports.AsNoTracking().SingleAsync(x => x.Id == overrideReport.Data!.ReportId);
            var ctx = await db.ReportMessageContextEntries.AsNoTracking().Where(c => c.ReportId == r.Id).ToListAsync();
            return (r, ctx);
        });

        Assert.Equal(3, ovrRow.ContextBeforeRequested);
        Assert.Equal(0, ovrRow.ContextAfterRequested);
        Assert.NotNull(ovrRow.ContextCompletedAt);
        Assert.Equal(3, ovrEntries.Count(e => e.Offset < 0));
        Assert.Equal(0, ovrEntries.Count(e => e.Offset > 0));

        var updateGlobal = await _host.With<ITextRoomAdminService, Response<TextRoomGlobalSettingsDto>>(s =>
            s.UpdateGlobalSettingsAsync(new TextRoomGlobalSettingsDto
            {
                Defaults = new TextRoomSettingsDto(),
                ReportContextMessagesBefore = 5,
                ReportContextMessagesAfter = 5,
                DefaultMessageRetentionHours = 72
            }, TextRoomTestHost.AdminActor(admin)));
        Assert.True(updateGlobal.Succeeded);

        var defaultRoom = await SeedRoomAsync();
        await SeedParticipantAsync(defaultRoom.Id, sender.Id);
        await SeedParticipantAsync(defaultRoom.Id, reporter.Id);
        await SeedMessagesInOrderAsync(defaultRoom.Id, sender.Id, 10, "glb-before");
        var globalTarget = await SeedMessageAsync(defaultRoom.Id, sender.Id, "glb-target");
        await SeedMessagesInOrderAsync(defaultRoom.Id, sender.Id, 10, "glb-after");

        var globalReport = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, globalTarget.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(globalReport.Succeeded);

        var (glbRow, glbEntries) = await _host.WithDb(async db =>
        {
            var r = await db.Reports.AsNoTracking().SingleAsync(x => x.Id == globalReport.Data!.ReportId);
            var ctx = await db.ReportMessageContextEntries.AsNoTracking().Where(c => c.ReportId == r.Id).ToListAsync();
            return (r, ctx);
        });

        Assert.Equal(5, glbRow.ContextBeforeRequested);
        Assert.Equal(5, glbRow.ContextAfterRequested);
        Assert.NotNull(glbRow.ContextCompletedAt);
        Assert.Equal(5, glbEntries.Count(e => e.Offset < 0));
        Assert.Equal(5, glbEntries.Count(e => e.Offset > 0));
    }

    [Fact]
    public async Task ContextCapture_FirstMessageInRoom_IgnoresOtherRoomsAndCapturesDeletedNeighbours()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");
        var targetRoom = await SeedRoomAsync();
        var otherRoom = await SeedRoomAsync();
        await SeedParticipantAsync(targetRoom.Id, sender.Id);
        await SeedParticipantAsync(targetRoom.Id, reporter.Id);

        await SeedMessagesInOrderAsync(otherRoom.Id, sender.Id, 5, "other-before");
        var firstInTarget = await SeedMessageAsync(targetRoom.Id, sender.Id, "very-first");
        await SeedMessagesInOrderAsync(otherRoom.Id, sender.Id, 5, "other-after");

        var report1 = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, firstInTarget.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Other }));
        Assert.True(report1.Succeeded);

        var entries1 = await _host.WithDb(db => db.ReportMessageContextEntries
            .AsNoTracking()
            .Where(c => c.ReportId == report1.Data!.ReportId)
            .ToListAsync());

        var only = Assert.Single(entries1);
        Assert.Equal(firstInTarget.Id, only.TextRoomMessageId);
        Assert.Equal(0, only.Offset);

        var deletedBefore = await SeedMessageAsync(targetRoom.Id, sender.Id, "deleted-before-content", isDeleted: true);
        var reportedWithDeletedNeighbours = await SeedMessageAsync(targetRoom.Id, sender.Id, "center-msg");
        var deletedAfter = await SeedMessageAsync(targetRoom.Id, sender.Id, "deleted-after-content", isDeleted: true);

        var report2 = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, reportedWithDeletedNeighbours.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Other }));
        Assert.True(report2.Succeeded);

        var entries2 = await _host.WithDb(db => db.ReportMessageContextEntries
            .AsNoTracking()
            .Where(c => c.ReportId == report2.Data!.ReportId)
            .ToDictionaryAsync(c => c.TextRoomMessageId));

        Assert.True(entries2.ContainsKey(deletedBefore.Id));
        Assert.True(entries2[deletedBefore.Id].WasDeleted);
        Assert.Equal("deleted-before-content", entries2[deletedBefore.Id].Content);

        Assert.True(entries2.ContainsKey(deletedAfter.Id));
        Assert.True(entries2[deletedAfter.Id].WasDeleted);
        Assert.Equal("deleted-after-content", entries2[deletedAfter.Id].Content);
    }

    [Fact]
    public async Task CompletePendingContextsAsync_SealsWhenRoomClosed()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);
        var msg = await SeedMessageAsync(room.Id, sender.Id, "Pending tail");

        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, msg.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(report.Succeeded);
        Assert.Null(await _host.WithDb(db => db.Reports.Where(r => r.Id == report.Data!.ReportId).Select(r => r.ContextCompletedAt).SingleAsync()));

        await _host.WithDb(db => db.TextRooms
            .Where(r => r.Id == room.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, TextRoomStatus.Closed)
                .SetProperty(r => r.ClosedAt, _host.Clock.UtcNow)));

        var sealedCount = await _host.With<ITextRoomReportService, int>(s => s.CompletePendingContextsAsync());
        Assert.Equal(1, sealedCount);
        Assert.NotNull(await _host.WithDb(db => db.Reports.Where(r => r.Id == report.Data!.ReportId).Select(r => r.ContextCompletedAt).SingleAsync()));
    }

    [Fact]
    public async Task CompletePendingContextsAsync_SealsOnlyAfterCaptureWindowExpires()
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);
        var msg = await SeedMessageAsync(room.Id, sender.Id, "Window check");

        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, msg.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Spam }));
        Assert.True(report.Succeeded);

        _host.Clock.Advance(TimeSpan.FromMinutes(TextRoomLimits.ReportContextCaptureWindowMinutes - 5));
        Assert.Equal(0, await _host.With<ITextRoomReportService, int>(s => s.CompletePendingContextsAsync()));
        Assert.Null(await _host.WithDb(db => db.Reports.Where(r => r.Id == report.Data!.ReportId).Select(r => r.ContextCompletedAt).SingleAsync()));

        _host.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(1, await _host.With<ITextRoomReportService, int>(s => s.CompletePendingContextsAsync()));
        Assert.NotNull(await _host.WithDb(db => db.Reports.Where(r => r.Id == report.Data!.ReportId).Select(r => r.ContextCompletedAt).SingleAsync()));
    }

    [Fact]
    public async Task ContextSnapshot_IsImmutableAfterSourceMessagesHardDeletedAndHistoryCleared()
    {
        var admin = await _host.CreateUserAsync(AppRoles.Admin, "Audit", "Admin");
        var sender = await _host.CreateUserAsync(AppRoles.User, "Msg", "Sender");
        var reporter = await _host.CreateUserAsync(AppRoles.User, "Msg", "Reporter");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedParticipantAsync(room.Id, reporter.Id);

        var m1 = await SeedMessageAsync(room.Id, sender.Id, "Original before text");
        var m2 = await SeedMessageAsync(room.Id, sender.Id, "Original reported text");
        var m3 = await SeedMessageAsync(room.Id, sender.Id, "Original after text");

        var report = await _host.With<ITextRoomReportService, Response<TextRoomReportResultDto>>(s =>
            s.ReportMessageAsync(reporter.Id, m2.Id, new ReportTextRoomMessageDto { Category = ReportCategory.Harassment }));
        Assert.True(report.Succeeded);

        await _host.WithDb(async db =>
        {
            await db.TextRoomMessages.Where(m => m.TextRoomId == room.Id).ExecuteDeleteAsync();
            await db.TextRooms.Where(r => r.Id == room.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.HistoryClearedAt, _host.Clock.UtcNow));
        });

        var context = await _host.With<ITextRoomReportService, Response<ReportMessageContextDto>>(s =>
            s.GetReportContextAsync(report.Data!.ReportId, TextRoomTestHost.AdminActor(admin)));

        Assert.True(context.Succeeded);
        Assert.Equal(m2.Id, context.Data!.ReportedMessageId);
        Assert.Equal(
            new[] { "Original before text", "Original reported text", "Original after text" },
            context.Data.Messages.Select(m => m.Content).ToArray());
        Assert.Equal(new[] { m1.Id, m2.Id, m3.Id }, context.Data.Messages.Select(m => m.MessageId).ToArray());
    }

    [Theory]
    [InlineData(WordCategory.Harassment, ReportCategory.Harassment, ModerationSeverity.Critical)]
    [InlineData(WordCategory.Spam, ReportCategory.Spam, ModerationSeverity.Low)]
    [InlineData(WordCategory.ContactLeak, ReportCategory.Spam, ModerationSeverity.High)]
    [InlineData(WordCategory.Profanity, ReportCategory.InappropriateContent, ModerationSeverity.Medium)]
    [InlineData(WordCategory.Custom, ReportCategory.InappropriateContent, ModerationSeverity.High)]
    public async Task CreateAutoReportAsync_SetsAutoModerationMetadataCategoryMarksReportedAndCapturesContext(
        WordCategory wordCategory, ReportCategory expectedReportCategory, ModerationSeverity severity)
    {
        var sender = await _host.CreateUserAsync(AppRoles.User, "Auto", "Sender");
        var room = await SeedRoomAsync();
        await SeedParticipantAsync(room.Id, sender.Id);
        await SeedMessageAsync(room.Id, sender.Id, "Context line before");
        var message = await SeedMessageAsync(room.Id, sender.Id, "Triggering term here");

        var loadedMessage = await _host.WithDb(db =>
            db.TextRoomMessages.Include(m => m.Sender).SingleAsync(m => m.Id == message.Id));

        var hit = new WordFilterHit(
            EntryId: Guid.NewGuid(),
            Term: "triggering",
            Category: wordCategory,
            Severity: severity,
            Action: WordAction.AllowAndAutoReport,
            Start: 0,
            Length: 10);

        var reportId = await _host.With<ITextRoomReportService, Guid>(s =>
            s.CreateAutoReportAsync(loadedMessage, hit));

        var (report, updatedMessage, contextCount) = await _host.WithDb(async db =>
        {
            var r = await db.Reports.AsNoTracking().SingleAsync(x => x.Id == reportId);
            var m = await db.TextRoomMessages.AsNoTracking().SingleAsync(x => x.Id == message.Id);
            var c = await db.ReportMessageContextEntries.CountAsync(x => x.ReportId == reportId);
            return (r, m, c);
        });

        Assert.Equal(ReportSource.AutoModeration, report.Source);
        Assert.Equal(sender.Id, report.ReporterId);
        Assert.Equal(sender.Id, report.ReportedUserId);
        Assert.Equal(expectedReportCategory, report.Category);
        Assert.Equal(severity, report.Severity);
        Assert.Equal("triggering", report.MatchedTerm);
        Assert.Equal(hit.EntryId, report.MatchedDictionaryEntryId);
        Assert.Equal(room.Id, report.ReportedTextRoomId);
        Assert.Equal(message.Id, report.ReportedTextRoomMessageId);
        Assert.True(updatedMessage.IsReported);
        Assert.Equal(2, contextCount);
    }
}
