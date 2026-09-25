using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace Cocorra.BLL.Services.RoomInviteService
{
    /// <summary>
    /// Invite codes are bearer secrets, so they come from the CSPRNG: 16 bytes (128 bits),
    /// Base64Url-encoded without padding, which is always exactly 22 URL-safe characters.
    /// </summary>
    public static class InviteCodeGenerator
    {
        public const int CodeLength = 22;

        private static readonly Regex Shape = new("^[A-Za-z0-9_-]{22}$", RegexOptions.Compiled);

        public static string Generate() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        /// <summary>
        /// Cheap pre-check so malformed codes are turned away without a database round-trip.
        /// </summary>
        public static bool IsWellFormed(string? code) => code != null && Shape.IsMatch(code);
    }
}
