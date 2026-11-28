using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Enums;

namespace Cocorra.BLL.Services.ContentModeration
{
    /// <summary>
    /// Evaluates text against the active dictionary. Compiled rules are cached in IMemoryCache
    /// and rebuilt after <see cref="InvalidateCache"/>, which every dictionary write calls.
    /// </summary>
    public interface IWordFilter
    {
        /// <summary>
        /// Any BlockMessage hit → IsBlocked. Otherwise MaskWord hits are replaced in the ORIGINAL
        /// text with '*' (one per original character of the matched span) and AllowAndAutoReport
        /// hits set RequiresAutoReport. Content is the text to persist.
        /// </summary>
        Task<WordFilterResult> EvaluateAsync(string content, CancellationToken ct = default);

        void InvalidateCache();
    }

    /// <summary><paramref name="Start"/> and <paramref name="Length"/> index the original text.</summary>
    public sealed record WordFilterHit(
        Guid EntryId,
        string Term,
        WordCategory Category,
        ModerationSeverity Severity,
        WordAction Action,
        int Start,
        int Length);

    public sealed record WordFilterResult(
        string Content,
        bool IsBlocked,
        bool WasMasked,
        bool RequiresAutoReport,
        IReadOnlyList<WordFilterHit> Hits)
    {
        public static WordFilterResult Clean(string content) =>
            new(content, false, false, false, Array.Empty<WordFilterHit>());

        public ModerationSeverity? MaxSeverity =>
            Hits.Count == 0 ? null : Hits.Max(h => h.Severity);

        /// <summary>The highest-severity AllowAndAutoReport hit, which the auto report is filed for.</summary>
        public WordFilterHit? TopAutoReportHit =>
            Hits.Where(h => h.Action == WordAction.AllowAndAutoReport)
                .OrderByDescending(h => h.Severity)
                .FirstOrDefault();
    }
}
