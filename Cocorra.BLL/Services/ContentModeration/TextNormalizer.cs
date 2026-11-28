using System;
using System.Text;

namespace Cocorra.BLL.Services.ContentModeration
{
    public sealed record NormalizedText(string Value, int[] OriginalIndex);

    /// <summary>
    /// Normalizes Arabic and Latin text for word-filter matching while preserving each
    /// character's index in the original input so masking spans map back accurately.
    /// </summary>
    public static class TextNormalizer
    {
        public static NormalizedText Normalize(string input)
        {
            if (string.IsNullOrEmpty(input))
                return new NormalizedText(string.Empty, Array.Empty<int>());

            var chars = new char[input.Length];
            var indices = new int[input.Length];
            var count = 0;

            for (var i = 0; i < input.Length; i++)
            {
                var c = input[i];
                if (ShouldDrop(c))
                    continue;

                chars[count] = char.ToLowerInvariant(MapChar(c));
                indices[count] = i;
                count++;
            }

            var normalizedValue = new string(chars, 0, count);
            var originalIndex = count == indices.Length ? indices : indices[..count];
            return new NormalizedText(normalizedValue, originalIndex);
        }

        public static string NormalizeTerm(string term)
        {
            if (string.IsNullOrEmpty(term))
                return string.Empty;

            var normalized = Normalize(term).Value;
            var sb = new StringBuilder(normalized.Length);
            var pendingSpace = false;

            foreach (var c in normalized)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0)
                        pendingSpace = true;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        private static bool ShouldDrop(char c) =>
            c is (>= '\u064B' and <= '\u065F')
                or '\u0670'
                or (>= '\u06D6' and <= '\u06ED')
                or '\u0640'
                or (>= '\u200B' and <= '\u200F')
                or (>= '\u202A' and <= '\u202E')
                or (>= '\u2066' and <= '\u2069')
                or '\uFEFF';

        private static char MapChar(char c) => c switch
        {
            '\u0623' or '\u0625' or '\u0622' or '\u0671' => '\u0627',
            '\u0629' => '\u0647',
            '\u0649' => '\u064A',
            '\u06A9' => '\u0643',
            '\u06CC' => '\u064A',
            _ => c
        };
    }
}
