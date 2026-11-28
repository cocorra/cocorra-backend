using System;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.Models
{
    public class WordDictionaryEntry : BaseEntity
    {
        /// <summary>As the admin typed it.</summary>
        public string Term { get; set; } = string.Empty;

        /// <summary><c>TextNormalizer.NormalizeTerm(Term)</c>; unique per <see cref="MatchType"/>.</summary>
        public string NormalizedTerm { get; set; } = string.Empty;

        public WordMatchType MatchType { get; set; } = WordMatchType.WholeWord;

        public WordCategory Category { get; set; } = WordCategory.Custom;

        public ModerationSeverity Severity { get; set; } = ModerationSeverity.Medium;

        public WordAction Action { get; set; } = WordAction.MaskWord;

        public bool IsActive { get; set; } = true;

        public string? Notes { get; set; }

        public Guid? CreatedById { get; set; }

        public Guid? UpdatedById { get; set; }
    }
}
