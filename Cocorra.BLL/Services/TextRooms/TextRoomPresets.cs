using System.Collections.Generic;
using Cocorra.DAL.DTOS.TextRoomDto;

namespace Cocorra.BLL.Services.TextRooms
{
    internal static class TextRoomPresets
    {
        public static IReadOnlyList<TextRoomPresetDto> All() => new TextRoomPresetDto[]
        {
            new()
            {
                Key = "always-on-open",
                Name = "24/7 Open Topic",
                Description = "Continuous room that rotates topics every 12 hours without clearing history.",
                Settings = new TextRoomSettingsDto
                {
                    IsAlwaysOn = true,
                    RotateTopicEveryMinutes = 720,
                    ClearHistoryOnRotate = false
                }
            },
            new()
            {
                Key = "flash-1h",
                Name = "1-Hour Flash Chat",
                Description = "Fast-paced 1-hour session with a 3-second slow mode.",
                Settings = new TextRoomSettingsDto
                {
                    DurationMinutes = 60,
                    SlowModeSeconds = 3
                }
            },
            new()
            {
                Key = "lounge-2h",
                Name = "2-Hour Lounge",
                Description = "Relaxed 2-hour open discussion room.",
                Settings = new TextRoomSettingsDto
                {
                    DurationMinutes = 120
                }
            },
            new()
            {
                Key = "daily-12h",
                Name = "12-Hour Daily Topic",
                Description = "Half-day discussion room open for 12 hours.",
                Settings = new TextRoomSettingsDto
                {
                    DurationMinutes = 720
                }
            }
        };
    }
}
