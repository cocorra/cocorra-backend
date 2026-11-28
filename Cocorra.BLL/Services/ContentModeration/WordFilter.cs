using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Cocorra.BLL.Services.ContentModeration
{
    public sealed class WordFilter : IWordFilter
    {
        public const string CacheKey = "word-dictionary:rules";

        private static readonly TimeSpan CacheSlidingExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

        // The filter is scoped (it needs a DbContext) but the rules cache is shared, so the rebuild
        // gate lives with the cache instance rather than with any one filter.
        private static readonly ConditionalWeakTable<IMemoryCache, CacheSyncState> CacheStates = new();

        private readonly AppDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<WordFilter> _logger;

        public WordFilter(AppDbContext db, IMemoryCache cache, ILogger<WordFilter> logger)
        {
            _db = db;
            _cache = cache;
            _logger = logger;
        }

        public async Task<WordFilterResult> EvaluateAsync(string content, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(content))
                return WordFilterResult.Clean(content ?? string.Empty);

            var normalized = TextNormalizer.Normalize(content);
            if (normalized.Value.Length == 0)
                return WordFilterResult.Clean(content);

            var rules = await GetCompiledRulesAsync(ct);
            if (rules.Count == 0)
                return WordFilterResult.Clean(content);

            var hits = CollectHits(rules, normalized);
            if (hits.Count == 0)
                return WordFilterResult.Clean(content);

            if (hits.Any(h => h.Action == WordAction.BlockMessage))
                return new WordFilterResult(content, IsBlocked: true, WasMasked: false, RequiresAutoReport: false, hits);

            var (resultContent, wasMasked) = ApplyMasks(content, hits);
            var requiresAutoReport = hits.Any(h => h.Action == WordAction.AllowAndAutoReport);

            return new WordFilterResult(resultContent, IsBlocked: false, wasMasked, requiresAutoReport, hits);
        }

        public void InvalidateCache()
        {
            var state = GetSyncState();
            Interlocked.Increment(ref state.Version);
            _cache.Remove(CacheKey);
        }

        private CacheSyncState GetSyncState() => CacheStates.GetOrCreateValue(_cache);

        private async Task<IReadOnlyList<CompiledRule>> GetCompiledRulesAsync(CancellationToken ct)
        {
            if (_cache.TryGetValue(CacheKey, out IReadOnlyList<CompiledRule>? cached) && cached is not null)
                return cached;

            var state = GetSyncState();
            await state.Lock.WaitAsync(ct);
            try
            {
                if (_cache.TryGetValue(CacheKey, out cached) && cached is not null)
                    return cached;

                var versionBeforeLoad = Volatile.Read(ref state.Version);
                var entries = await _db.WordDictionaryEntries
                    .AsNoTracking()
                    .Where(e => e.IsActive)
                    .ToListAsync(ct);

                var compiled = CompileEntries(entries);

                // Avoid caching stale rules if a write invalidated the cache mid-query.
                if (Volatile.Read(ref state.Version) == versionBeforeLoad)
                {
                    _cache.Set(CacheKey, compiled, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = CacheSlidingExpiration
                    });
                }

                return compiled;
            }
            finally
            {
                state.Lock.Release();
            }
        }

        private List<WordFilterHit> CollectHits(IReadOnlyList<CompiledRule> rules, NormalizedText normalized)
        {
            var hits = new List<WordFilterHit>();

            foreach (var rule in rules)
            {
                var ruleHits = MatchRule(rule, normalized);
                if (ruleHits is not null)
                    hits.AddRange(ruleHits);
            }

            return hits;
        }

        private List<WordFilterHit>? MatchRule(CompiledRule rule, NormalizedText normalized)
        {
            try
            {
                List<WordFilterHit>? ruleHits = null;
                var match = rule.Regex.Match(normalized.Value);

                while (match.Success)
                {
                    if (match.Length > 0)
                    {
                        var origStart = normalized.OriginalIndex[match.Index];
                        var origEnd = normalized.OriginalIndex[match.Index + match.Length - 1];
                        var origLength = origEnd - origStart + 1;

                        ruleHits ??= new List<WordFilterHit>();
                        ruleHits.Add(new WordFilterHit(
                            rule.EntryId,
                            rule.Term,
                            rule.Category,
                            rule.Severity,
                            rule.Action,
                            origStart,
                            origLength));
                    }

                    match = match.NextMatch();
                }

                return ruleHits;
            }
            catch (RegexMatchTimeoutException ex)
            {
                _logger.LogWarning(ex, "Word filter rule {EntryId} ('{Term}') timed out.", rule.EntryId, rule.Term);
                return null;
            }
        }

        private static (string Content, bool WasMasked) ApplyMasks(string original, List<WordFilterHit> hits)
        {
            var spans = new List<(int Start, int End)>();
            foreach (var hit in hits)
            {
                if (hit.Action == WordAction.MaskWord && hit.Length > 0)
                    spans.Add((hit.Start, hit.Start + hit.Length));
            }

            if (spans.Count == 0)
                return (original, false);

            var mergedSpans = MergeOverlappingSpans(spans);
            var chars = original.ToCharArray();
            var maskedAny = false;

            foreach (var (start, end) in mergedSpans)
            {
                var clampedEnd = Math.Min(end, chars.Length);
                for (var i = Math.Max(0, start); i < clampedEnd; i++)
                {
                    if (!char.IsWhiteSpace(chars[i]))
                    {
                        chars[i] = '*';
                        maskedAny = true;
                    }
                }
            }

            return (new string(chars), maskedAny);
        }

        private static List<(int Start, int End)> MergeOverlappingSpans(List<(int Start, int End)> spans)
        {
            spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

            var merged = new List<(int Start, int End)>(spans.Count) { spans[0] };
            for (var i = 1; i < spans.Count; i++)
            {
                var last = merged[^1];
                var current = spans[i];
                if (current.Start <= last.End)
                    merged[^1] = (last.Start, Math.Max(last.End, current.End));
                else
                    merged.Add(current);
            }

            return merged;
        }

        private static IReadOnlyList<CompiledRule> CompileEntries(List<WordDictionaryEntry> entries)
        {
            var compiled = new List<CompiledRule>(entries.Count);
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.NormalizedTerm))
                    continue;

                var pattern = BuildPattern(entry.NormalizedTerm, entry.MatchType);
                // Not RegexOptions.Compiled: the rule set is rebuilt on every dictionary edit, and
                // emitting IL for hundreds of terms each time costs more than interpreting them.
                var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
                compiled.Add(new CompiledRule(
                    entry.Id,
                    entry.Term,
                    entry.Category,
                    entry.Severity,
                    entry.Action,
                    regex));
            }

            return compiled;
        }

        internal static string BuildPattern(string normalizedTerm, WordMatchType matchType)
        {
            var sb = new StringBuilder(normalizedTerm.Length * 4);
            var i = 0;

            while (i < normalizedTerm.Length)
            {
                var c = normalizedTerm[i];
                if (char.IsWhiteSpace(c))
                {
                    while (i < normalizedTerm.Length && char.IsWhiteSpace(normalizedTerm[i]))
                        i++;

                    sb.Append(@"(?>\s+)");
                    continue;
                }

                var runLength = 0;
                while (i < normalizedTerm.Length && normalizedTerm[i] == c)
                {
                    runLength++;
                    i++;
                }

                sb.Append("(?>");
                sb.Append(Regex.Escape(c.ToString()));
                sb.Append('{');
                sb.Append(runLength);
                sb.Append(",})");
            }

            var core = sb.ToString();
            return matchType == WordMatchType.WholeWord
                ? $@"(?<![\p{{L}}\p{{N}}]){core}(?![\p{{L}}\p{{N}}])"
                : core;
        }

        private sealed class CacheSyncState
        {
            public SemaphoreSlim Lock { get; } = new(1, 1);
            public long Version;
        }

        private sealed record CompiledRule(
            Guid EntryId,
            string Term,
            WordCategory Category,
            ModerationSeverity Severity,
            WordAction Action,
            Regex Regex);
    }
}
