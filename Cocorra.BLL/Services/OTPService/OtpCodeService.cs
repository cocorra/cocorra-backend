using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cocorra.DAL.Data;
using Cocorra.DAL.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cocorra.BLL.Services.OTPService
{
    public class OtpCodeService : IOtpCodeService
    {
        /// <summary>Matches the "valid for 6 minutes" text in the email templates.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(6);
        public const int MaxAttemptsPerCode = 5;

        private readonly AppDbContext _context;
        private readonly byte[] _hmacKey;

        public OtpCodeService(AppDbContext context, IOptions<OtpOptions> options)
        {
            _context = context;
            var key = options.Value.HmacKey;
            if (string.IsNullOrEmpty(key) || key.Length < OtpOptions.MinimumHmacKeyLength)
                throw new InvalidOperationException(
                    $"Otp:HmacKey must be at least {OtpOptions.MinimumHmacKeyLength} characters.");
            _hmacKey = Encoding.UTF8.GetBytes(key);
        }

        public async Task<string> IssueAsync(Guid userId, string purpose, CancellationToken cancellationToken = default)
        {
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6");
            var hash = ComputeHash(userId, purpose, code);
            var now = DateTime.UtcNow;
            var expiresAt = now + Lifetime;

            // Overwrite in place so the unique (UserId, Purpose) row only ever holds one live code.
            if (await ReplaceExistingAsync(userId, purpose, hash, now, expiresAt, cancellationToken) > 0)
                return code;

            _context.OtpCodes.Add(new OtpCode
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Purpose = purpose,
                CodeHash = hash,
                ExpiresAt = expiresAt,
                Attempts = 0
            });
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // A concurrent request inserted the row first; overwrite it instead.
                _context.ChangeTracker.Clear();
                await ReplaceExistingAsync(userId, purpose, hash, now, expiresAt, cancellationToken);
            }
            return code;
        }

        public async Task<bool> TryConsumeAsync(Guid userId, string purpose, string code, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;

            // Reserve the attempt atomically BEFORE comparing, so a burst of parallel guesses
            // cannot get more than MaxAttemptsPerCode comparisons against one code.
            var reserved = await _context.OtpCodes
                .Where(o => o.UserId == userId && o.Purpose == purpose && o.ExpiresAt > now && o.Attempts < MaxAttemptsPerCode)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Attempts, o => o.Attempts + 1), cancellationToken);
            if (reserved == 0)
                return false;

            var row = await _context.OtpCodes.AsNoTracking()
                .FirstOrDefaultAsync(o => o.UserId == userId && o.Purpose == purpose, cancellationToken);
            if (row == null)
                return false;

            var candidate = ComputeHash(userId, purpose, code ?? string.Empty);
            if (CryptographicOperations.FixedTimeEquals(candidate, row.CodeHash))
            {
                // Matching on the hash as well means a code replaced in the meantime is left alone,
                // and only one of two concurrent correct requests can succeed.
                var consumed = await DeleteIfUnchangedAsync(row, cancellationToken);
                return consumed == 1;
            }

            if (row.Attempts >= MaxAttemptsPerCode)
                await DeleteIfUnchangedAsync(row, cancellationToken);
            return false;
        }

        private Task<int> ReplaceExistingAsync(Guid userId, string purpose, byte[] hash, DateTime now, DateTime expiresAt, CancellationToken cancellationToken)
        {
            return _context.OtpCodes
                .Where(o => o.UserId == userId && o.Purpose == purpose)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.CodeHash, hash)
                    .SetProperty(o => o.ExpiresAt, expiresAt)
                    .SetProperty(o => o.Attempts, 0)
                    .SetProperty(o => o.UpdatedAt, now), cancellationToken);
        }

        private Task<int> DeleteIfUnchangedAsync(OtpCode row, CancellationToken cancellationToken)
        {
            return _context.OtpCodes
                .Where(o => o.Id == row.Id && o.CodeHash == row.CodeHash)
                .ExecuteDeleteAsync(cancellationToken);
        }

        private byte[] ComputeHash(Guid userId, string purpose, string code)
        {
            // Purpose and user id are part of the hash, so a stored value is useless for any
            // other user or flow.
            var message = Encoding.UTF8.GetBytes($"{purpose}:{userId:N}:{code}");
            return HMACSHA256.HashData(_hmacKey, message);
        }
    }
}
