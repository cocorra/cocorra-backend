using System;
using System.Collections.Generic;

namespace Cocorra.DAL.Models
{
    public class TextRoomSchedule : BaseEntity
    {
        public string Name { get; set; } = string.Empty;

        public bool IsEnabled { get; set; } = true;

        public int SpawnEveryMinutes { get; set; } = 60;

        /// <summary>
        /// Optional daily window in Cairo local time, as minutes after midnight. A window whose end
        /// is before its start wraps past midnight. Both null: spawn around the clock.
        /// </summary>
        public int? WindowStartMinuteOfDay { get; set; }
        public int? WindowEndMinuteOfDay { get; set; }

        /// <summary>Spawned rooms take topics from here in order, wrapping around.</summary>
        public List<TextRoomTopic> TopicPool { get; set; } = new();

        public int NextTopicIndex { get; set; }

        public TextRoomSettings Settings { get; set; } = new();

        /// <summary>Stops always-on spawns from piling up: the previous room of this schedule is closed first.</summary>
        public bool ClosePreviousOnSpawn { get; set; } = true;

        public DateTime NextSpawnAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastSpawnAt { get; set; }

        public Guid? CreatedById { get; set; }
    }
}
