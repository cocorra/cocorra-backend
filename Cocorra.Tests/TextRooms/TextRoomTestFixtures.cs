using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.BLL.Services.TextRooms;
using Cocorra.DAL.DTOS.TextRoomDto;
using Cocorra.DAL.Time;
using Cocorra.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cocorra.Tests.TextRooms
{
    internal static class TextRoomTestFixtures
    {
        public static TextRoomTopicDto Topic(string title, params string[] prompts) => new()
        {
            Title = title,
            Subject = "General",
            Description = $"Discussion about {title}",
            Prompts = prompts.Length > 0 ? prompts.ToList() : new List<string> { "Share your thoughts." }
        };

        public static TextRoomUpsertDto ValidRoomDto(
            string title = "Evening Discussion",
            Action<TextRoomSettingsDto>? configureSettings = null,
            Action<TextRoomUpsertDto>? configure = null)
        {
            var settings = new TextRoomSettingsDto
            {
                IsAlwaysOn = false,
                DurationMinutes = 60,
                ShowOnlineCountToUsers = false,
                AllowPrivateChatFromRoom = true,
                SlowModeSeconds = 0,
                MaxMessageLength = 500
            };
            configureSettings?.Invoke(settings);

            var dto = new TextRoomUpsertDto
            {
                Title = title,
                Subject = "Community",
                Description = "Open conversation room.",
                DiscussionPrompts = new List<string> { "What inspired you today?" },
                TopicPool = new List<TextRoomTopicDto>(),
                Settings = settings
            };
            configure?.Invoke(dto);
            return dto;
        }

        public static TextRoomScheduleUpsertDto ValidScheduleDto(
            string name = "Hourly Lounge",
            Action<TextRoomSettingsDto>? configureSettings = null,
            Action<TextRoomScheduleUpsertDto>? configure = null)
        {
            var settings = new TextRoomSettingsDto
            {
                IsAlwaysOn = false,
                DurationMinutes = 60,
                ShowOnlineCountToUsers = false,
                AllowPrivateChatFromRoom = true,
                SlowModeSeconds = 0,
                MaxMessageLength = 500
            };
            configureSettings?.Invoke(settings);

            var dto = new TextRoomScheduleUpsertDto
            {
                Name = name,
                IsEnabled = true,
                SpawnEveryMinutes = 60,
                ClosePreviousOnSpawn = true,
                TopicPool = new List<TextRoomTopicDto>
                {
                    Topic("First Topic", "Prompt one"),
                    Topic("Second Topic", "Prompt two")
                },
                Settings = settings
            };
            configure?.Invoke(dto);
            return dto;
        }

        public static TextRoomGlobalSettingsDto ValidGlobalSettingsDto(
            Action<TextRoomGlobalSettingsDto>? configure = null)
        {
            var dto = new TextRoomGlobalSettingsDto
            {
                Defaults = new TextRoomSettingsDto
                {
                    IsAlwaysOn = false,
                    DurationMinutes = 60,
                    MaxMessageLength = 500,
                    AllowPrivateChatFromRoom = true
                },
                ReportContextMessagesBefore = 25,
                ReportContextMessagesAfter = 25,
                DefaultMessageRetentionHours = 72,
                AllowModeratorsToManageRooms = false
            };
            configure?.Invoke(dto);
            return dto;
        }

        public static async Task<TextRoomStaffViewDto> CreateRoomAsync(
            TextRoomTestHost host,
            StaffActor actor,
            TextRoomUpsertDto? dto = null)
        {
            var result = await host.With<ITextRoomAdminService, Response<TextRoomStaffViewDto>>(svc =>
                svc.CreateRoomAsync(dto ?? ValidRoomDto(), actor));

            Assert.True(result.Succeeded, $"Expected room creation to succeed, got {result.StatusCode}: {result.Message}");
            Assert.NotNull(result.Data);
            return result.Data!;
        }

        public static async Task<TextRoomScheduleDto> CreateScheduleAsync(
            TextRoomTestHost host,
            StaffActor actor,
            TextRoomScheduleUpsertDto? dto = null)
        {
            var result = await host.With<ITextRoomAdminService, Response<TextRoomScheduleDto>>(svc =>
                svc.CreateScheduleAsync(dto ?? ValidScheduleDto(), actor));

            Assert.True(result.Succeeded, $"Expected schedule creation to succeed, got {result.StatusCode}: {result.Message}");
            Assert.NotNull(result.Data);
            return result.Data!;
        }

        public static async Task<Guid> JoinAndGoOnlineAsync(
            TextRoomTestHost host,
            Guid roomId,
            Guid userId,
            string connectionId,
            bool isStaff = false)
        {
            var join = await host.With<ITextRoomService, TextRoomJoinResultDto>(svc =>
                svc.JoinAsync(roomId, userId, isStaff, connectionId));

            Assert.True(
                join.Status is TextRoomJoinStatus.Joined or TextRoomJoinStatus.Redirected,
                $"Expected user to join room {roomId}, got {join.Status}.");
            Assert.NotNull(join.RoomId);
            return join.RoomId!.Value;
        }

        public static string NewConnectionId() => $"conn-{Guid.NewGuid():N}";

        public static DateTime CairoLocalToUtc(int year, int month, int day, int hour, int minute)
        {
            var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(local, CairoTime.Zone);
        }
    }
}
