using System;
using System.Security.Cryptography;
using System.Text;

namespace Cocorra.BLL.Services.AuthServices
{
    /// <summary>
    /// JWT claim tying an access token to the user's security stamp at issue time. Identity
    /// changes the stamp on password reset and change, so <c>OnTokenValidated</c> rejects tokens
    /// issued before that. Only a hash of the stamp goes in the token, since anyone holding a JWT
    /// can read its claims.
    /// </summary>
    public static class SecurityStampClaim
    {
        public const string Type = "sstamp";

        public static string Compute(string? securityStamp)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(securityStamp ?? string.Empty));
            return Convert.ToBase64String(hash, 0, 16);
        }

        public static bool Matches(string? securityStamp, string claimValue)
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Compute(securityStamp)),
                Encoding.UTF8.GetBytes(claimValue));
        }
    }
}
