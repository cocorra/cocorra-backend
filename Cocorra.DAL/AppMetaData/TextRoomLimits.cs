namespace Cocorra.DAL.AppMetaData
{
    /// <summary>Server-side bounds for every configurable text-room value. The dashboard mirrors these.</summary>
    public static class TextRoomLimits
    {
        public const int TitleMin = 3;
        public const int TitleMax = 120;
        public const int SubjectMax = 60;
        public const int DescriptionMax = 1000;
        public const int MaxPrompts = 20;
        public const int PromptMax = 300;
        public const int MaxTopicPool = 50;

        public const int DurationMinutesMin = 5;
        public const int DurationMinutesMax = 10080;
        public const int RotateMinutesMin = 5;
        public const int RotateMinutesMax = 10080;
        public const int SpawnEveryMinutesMin = 5;
        public const int SpawnEveryMinutesMax = 10080;
        public const int MinuteOfDayMax = 1439;

        public const int MaxParticipantsMin = 2;
        public const int MaxParticipantsMax = 10000;
        public const int HardMaxOverflowInstances = 20;

        public const int SlowModeSecondsMax = 3600;
        public const int MaxMessageLengthMin = 1;
        public const int MaxMessageLengthMax = 4000;
        public const int RetentionHoursMin = 1;
        public const int RetentionHoursMax = 8760;
        public const int ReportContextMax = 100;

        public const int MuteMinutesMin = 1;
        public const int MuteMinutesMax = 43200;

        public const int HistoryPageDefault = 50;
        public const int HistoryPageMax = 100;

        /// <summary>After this long, a report stops waiting for "after" messages and its context is sealed.</summary>
        public const int ReportContextCaptureWindowMinutes = 30;

        public const int DictionaryTermMax = 100;
        public const int DictionaryBulkMax = 500;
    }
}
