using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.WordDictionaryDto
{
    public class WordDictionaryEntryDto
    {
        public Guid Id { get; set; }
        public string Term { get; set; } = string.Empty;
        public string NormalizedTerm { get; set; } = string.Empty;
        public WordMatchType MatchType { get; set; }
        public WordCategory Category { get; set; }
        public ModerationSeverity Severity { get; set; }
        public WordAction Action { get; set; }
        public bool IsActive { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    public class WordDictionaryUpsertDto
    {
        [Required, MaxLength(TextRoomLimits.DictionaryTermMax)]
        public string Term { get; set; } = string.Empty;

        public WordMatchType MatchType { get; set; } = WordMatchType.WholeWord;
        public WordCategory Category { get; set; } = WordCategory.Custom;
        public ModerationSeverity Severity { get; set; } = ModerationSeverity.Medium;
        public WordAction Action { get; set; } = WordAction.MaskWord;
        public bool IsActive { get; set; } = true;

        [MaxLength(200)]
        public string? Notes { get; set; }
    }

    public class WordDictionaryQueryDto
    {
        public string? Search { get; set; }
        public WordCategory? Category { get; set; }
        public ModerationSeverity? Severity { get; set; }
        public WordAction? Action { get; set; }
        public bool? IsActive { get; set; }
        public int PageNumber { get; set; } = 1;
        public int PageSize { get; set; } = 50;
    }

    /// <summary>Either <see cref="Entries"/>, or <see cref="Terms"/> (one per line / item) with shared defaults.</summary>
    public class WordDictionaryBulkAddDto
    {
        public List<WordDictionaryUpsertDto> Entries { get; set; } = new();
        public List<string> Terms { get; set; } = new();
        public WordMatchType MatchType { get; set; } = WordMatchType.WholeWord;
        public WordCategory Category { get; set; } = WordCategory.Custom;
        public ModerationSeverity Severity { get; set; } = ModerationSeverity.Medium;
        public WordAction Action { get; set; } = WordAction.MaskWord;
    }

    public class WordDictionaryBulkResultDto
    {
        public int Added { get; set; }
        public int SkippedDuplicates { get; set; }
        public List<string> Invalid { get; set; } = new();
    }

    public class SetActiveDto
    {
        public bool IsActive { get; set; }
    }

    public class WordFilterTestRequestDto
    {
        [Required, MaxLength(TextRoomLimits.MaxMessageLengthMax)]
        public string Text { get; set; } = string.Empty;
    }

    public class WordFilterTestResultDto
    {
        public string NormalizedText { get; set; } = string.Empty;
        public string ResultContent { get; set; } = string.Empty;
        public bool IsBlocked { get; set; }
        public bool WasMasked { get; set; }
        public bool RequiresAutoReport { get; set; }
        public ModerationSeverity? MaxSeverity { get; set; }
        public List<WordFilterHitDto> Hits { get; set; } = new();
    }

    public class WordFilterHitDto
    {
        public Guid EntryId { get; set; }
        public string Term { get; set; } = string.Empty;
        public WordCategory Category { get; set; }
        public ModerationSeverity Severity { get; set; }
        public WordAction Action { get; set; }
    }
}
