using System.Collections.Generic;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.TextRoomDto;

namespace Cocorra.BLL.Services.TextRooms
{
    internal static class TextRoomSettingsValidator
    {
        public const int SanctionReasonMin = 3;
        public const int SanctionReasonMax = 1000;
        public const string SanctionReasonError = "A reason between 3 and 1000 characters is required.";

        public static string? NormalizeSanctionReason(string? reason)
        {
            var trimmed = (reason ?? string.Empty).Trim();
            return trimmed.Length >= SanctionReasonMin && trimmed.Length <= SanctionReasonMax
                ? trimmed
                : null;
        }

        public static string? ValidateRoomUpsert(TextRoomUpsertDto? dto)
        {
            if (dto is null)
                return "Request payload is required.";

            return ValidateTitle(dto.Title, "Title")
                ?? ValidateOptionalLength(dto.Subject, TextRoomLimits.SubjectMax, "Subject")
                ?? ValidateOptionalLength(dto.Description, TextRoomLimits.DescriptionMax, "Description")
                ?? ValidatePrompts(dto.DiscussionPrompts, "DiscussionPrompts")
                ?? ValidateOptionalTopicPool(dto.TopicPool)
                ?? ValidateSettings(dto.Settings);
        }

        public static string? ValidateScheduleUpsert(TextRoomScheduleUpsertDto? dto)
        {
            if (dto is null)
                return "Request payload is required.";

            return ValidateTitle(dto.Name, "Name")
                ?? ValidateSpawnInterval(dto.SpawnEveryMinutes)
                ?? ValidateWindow(dto.WindowStartMinuteOfDay, dto.WindowEndMinuteOfDay)
                ?? ValidateRequiredTopicPool(dto.TopicPool)
                ?? ValidateSettings(dto.Settings);
        }

        public static string? ValidateGlobalSettings(TextRoomGlobalSettingsDto? dto)
        {
            if (dto is null)
                return "Request payload is required.";

            if (dto.ReportContextMessagesBefore < 0 || dto.ReportContextMessagesBefore > TextRoomLimits.ReportContextMax)
                return $"ReportContextMessagesBefore must be between 0 and {TextRoomLimits.ReportContextMax}.";

            if (dto.ReportContextMessagesAfter < 0 || dto.ReportContextMessagesAfter > TextRoomLimits.ReportContextMax)
                return $"ReportContextMessagesAfter must be between 0 and {TextRoomLimits.ReportContextMax}.";

            if (dto.DefaultMessageRetentionHours < TextRoomLimits.RetentionHoursMin
                || dto.DefaultMessageRetentionHours > TextRoomLimits.RetentionHoursMax)
            {
                return $"DefaultMessageRetentionHours must be between {TextRoomLimits.RetentionHoursMin} and {TextRoomLimits.RetentionHoursMax}.";
            }

            return ValidateSettings(dto.Defaults);
        }

        public static string? ValidateSettings(TextRoomSettingsDto? settings)
        {
            if (settings is null)
                return "Settings is required.";

            return ValidateLifecycleSettings(settings)
                ?? ValidateCapacitySettings(settings)
                ?? ValidateMessagingSettings(settings);
        }

        private static string? ValidateLifecycleSettings(TextRoomSettingsDto settings)
        {
            if (!settings.IsAlwaysOn)
            {
                if (!settings.DurationMinutes.HasValue
                    || settings.DurationMinutes.Value < TextRoomLimits.DurationMinutesMin
                    || settings.DurationMinutes.Value > TextRoomLimits.DurationMinutesMax)
                {
                    return $"DurationMinutes must be between {TextRoomLimits.DurationMinutesMin} and {TextRoomLimits.DurationMinutesMax}.";
                }
            }

            if (settings.RotateTopicEveryMinutes.HasValue)
            {
                if (!settings.IsAlwaysOn)
                    return "RotateTopicEveryMinutes is only valid when IsAlwaysOn is true.";

                if (settings.RotateTopicEveryMinutes.Value < TextRoomLimits.RotateMinutesMin
                    || settings.RotateTopicEveryMinutes.Value > TextRoomLimits.RotateMinutesMax)
                {
                    return $"RotateTopicEveryMinutes must be between {TextRoomLimits.RotateMinutesMin} and {TextRoomLimits.RotateMinutesMax}.";
                }
            }

            return null;
        }

        private static string? ValidateCapacitySettings(TextRoomSettingsDto settings)
        {
            if (settings.MaxParticipants.HasValue
                && (settings.MaxParticipants.Value < TextRoomLimits.MaxParticipantsMin
                    || settings.MaxParticipants.Value > TextRoomLimits.MaxParticipantsMax))
            {
                return $"MaxParticipants must be between {TextRoomLimits.MaxParticipantsMin} and {TextRoomLimits.MaxParticipantsMax}.";
            }

            if (settings.OverflowTriggerCount.HasValue)
            {
                if (settings.OverflowTriggerCount.Value < 1)
                    return "OverflowTriggerCount must be at least 1.";

                if (settings.MaxParticipants.HasValue
                    && settings.OverflowTriggerCount.Value > settings.MaxParticipants.Value)
                {
                    return "OverflowTriggerCount cannot exceed MaxParticipants.";
                }
            }

            if (settings.MaxOverflowInstances.HasValue
                && (settings.MaxOverflowInstances.Value < 1
                    || settings.MaxOverflowInstances.Value > TextRoomLimits.HardMaxOverflowInstances))
            {
                return $"MaxOverflowInstances must be between 1 and {TextRoomLimits.HardMaxOverflowInstances}.";
            }

            if (settings.AutoOverflowEnabled
                && !settings.MaxParticipants.HasValue
                && !settings.OverflowTriggerCount.HasValue)
            {
                return "AutoOverflowEnabled requires MaxParticipants or OverflowTriggerCount.";
            }

            return null;
        }

        private static string? ValidateMessagingSettings(TextRoomSettingsDto settings)
        {
            if (settings.SlowModeSeconds < 0 || settings.SlowModeSeconds > TextRoomLimits.SlowModeSecondsMax)
                return $"SlowModeSeconds must be between 0 and {TextRoomLimits.SlowModeSecondsMax}.";

            if (settings.MaxMessageLength < TextRoomLimits.MaxMessageLengthMin
                || settings.MaxMessageLength > TextRoomLimits.MaxMessageLengthMax)
            {
                return $"MaxMessageLength must be between {TextRoomLimits.MaxMessageLengthMin} and {TextRoomLimits.MaxMessageLengthMax}.";
            }

            if (settings.MessageRetentionHours.HasValue
                && (settings.MessageRetentionHours.Value < TextRoomLimits.RetentionHoursMin
                    || settings.MessageRetentionHours.Value > TextRoomLimits.RetentionHoursMax))
            {
                return $"MessageRetentionHours must be between {TextRoomLimits.RetentionHoursMin} and {TextRoomLimits.RetentionHoursMax}.";
            }

            if (settings.ReportContextMessagesBefore.HasValue
                && (settings.ReportContextMessagesBefore.Value < 0
                    || settings.ReportContextMessagesBefore.Value > TextRoomLimits.ReportContextMax))
            {
                return $"ReportContextMessagesBefore must be between 0 and {TextRoomLimits.ReportContextMax}.";
            }

            if (settings.ReportContextMessagesAfter.HasValue
                && (settings.ReportContextMessagesAfter.Value < 0
                    || settings.ReportContextMessagesAfter.Value > TextRoomLimits.ReportContextMax))
            {
                return $"ReportContextMessagesAfter must be between 0 and {TextRoomLimits.ReportContextMax}.";
            }

            return null;
        }

        private static string? ValidateTitle(string? rawText, string fieldName)
        {
            var trimmed = (rawText ?? string.Empty).Trim();
            if (trimmed.Length < TextRoomLimits.TitleMin || trimmed.Length > TextRoomLimits.TitleMax)
                return $"{fieldName} must be between {TextRoomLimits.TitleMin} and {TextRoomLimits.TitleMax} characters.";

            return null;
        }

        private static string? ValidateOptionalLength(string? rawText, int maxLength, string fieldName)
        {
            if (rawText is not null && rawText.Trim().Length > maxLength)
                return $"{fieldName} must be at most {maxLength} characters.";

            return null;
        }

        private static string? ValidatePrompts(List<string>? prompts, string fieldName)
        {
            if (prompts is null)
                return null;

            if (prompts.Count > TextRoomLimits.MaxPrompts)
                return $"{fieldName} cannot have more than {TextRoomLimits.MaxPrompts} items.";

            foreach (var prompt in prompts)
            {
                if (string.IsNullOrWhiteSpace(prompt))
                    return $"{fieldName} cannot contain blank entries.";

                if (prompt.Trim().Length > TextRoomLimits.PromptMax)
                    return $"{fieldName} entries must be at most {TextRoomLimits.PromptMax} characters.";
            }

            return null;
        }

        private static string? ValidateOptionalTopicPool(List<TextRoomTopicDto>? pool) =>
            pool is null || pool.Count == 0 ? null : ValidateTopicPoolEntries(pool);

        private static string? ValidateRequiredTopicPool(List<TextRoomTopicDto>? pool) =>
            pool is null || pool.Count == 0
                ? "TopicPool must contain at least one topic."
                : ValidateTopicPoolEntries(pool);

        private static string? ValidateTopicPoolEntries(List<TextRoomTopicDto> pool)
        {
            if (pool.Count > TextRoomLimits.MaxTopicPool)
                return $"TopicPool cannot contain more than {TextRoomLimits.MaxTopicPool} topics.";

            foreach (var topic in pool)
            {
                if (topic is null)
                    return "TopicPool cannot contain null entries.";

                var topicError = ValidateTitle(topic.Title, "TopicPool.Title")
                    ?? ValidateOptionalLength(topic.Subject, TextRoomLimits.SubjectMax, "TopicPool.Subject")
                    ?? ValidateOptionalLength(topic.Description, TextRoomLimits.DescriptionMax, "TopicPool.Description")
                    ?? ValidatePrompts(topic.Prompts, "TopicPool.Prompts");

                if (topicError is not null)
                    return topicError;
            }

            return null;
        }

        private static string? ValidateSpawnInterval(int spawnEveryMinutes)
        {
            if (spawnEveryMinutes < TextRoomLimits.SpawnEveryMinutesMin
                || spawnEveryMinutes > TextRoomLimits.SpawnEveryMinutesMax)
            {
                return $"SpawnEveryMinutes must be between {TextRoomLimits.SpawnEveryMinutesMin} and {TextRoomLimits.SpawnEveryMinutesMax}.";
            }

            return null;
        }

        private static string? ValidateWindow(int? startMinute, int? endMinute)
        {
            if (startMinute.HasValue != endMinute.HasValue)
                return "WindowStartMinuteOfDay and WindowEndMinuteOfDay must both be set or both be null.";

            if (startMinute.HasValue && (startMinute.Value < 0 || startMinute.Value > TextRoomLimits.MinuteOfDayMax))
                return $"WindowStartMinuteOfDay must be between 0 and {TextRoomLimits.MinuteOfDayMax}.";

            if (endMinute.HasValue && (endMinute.Value < 0 || endMinute.Value > TextRoomLimits.MinuteOfDayMax))
                return $"WindowEndMinuteOfDay must be between 0 and {TextRoomLimits.MinuteOfDayMax}.";

            return null;
        }
    }
}
