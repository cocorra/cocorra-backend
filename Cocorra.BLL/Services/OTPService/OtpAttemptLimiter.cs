using System;
using Microsoft.Extensions.Caching.Memory;

namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>
    /// <see cref="IMemoryCache"/>-backed OTP attempt limiter. Registered as a singleton.
    /// Identity lockout is disabled for new users, so this is what bounds OTP brute-forcing.
    /// </summary>
    public class OtpAttemptLimiter : IOtpAttemptLimiter
    {
        public const int MaxFailedAttempts = 5;
        public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan SendCooldown = TimeSpan.FromSeconds(60);

        private readonly IMemoryCache _cache;
        private readonly object _sync = new();

        public OtpAttemptLimiter(IMemoryCache cache)
        {
            _cache = cache;
        }

        public bool TryRegisterAttempt(string email, string purpose)
        {
            var key = FailureKey(email, purpose);
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out FailureCounter? counter) && counter != null)
                {
                    if (counter.Count >= MaxFailedAttempts)
                        return false;

                    // Absolute expiry was fixed at the first attempt; mutate in place so the
                    // window is not extended by later attempts.
                    counter.Count++;
                    return true;
                }

                _cache.Set(key, new FailureCounter { Count = 1 }, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = FailureWindow
                });
                return true;
            }
        }

        public void Reset(string email, string purpose)
        {
            _cache.Remove(FailureKey(email, purpose));
        }

        public bool TryBeginSend(string email, string purpose)
        {
            var key = SendKey(email, purpose);
            lock (_sync)
            {
                if (_cache.TryGetValue(key, out _))
                    return false;

                _cache.Set(key, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = SendCooldown
                });
                return true;
            }
        }

        private static string Normalize(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();

        private static string FailureKey(string email, string purpose) => $"otp:fail:{purpose}:{Normalize(email)}";

        private static string SendKey(string email, string purpose) => $"otp:send:{purpose}:{Normalize(email)}";

        private sealed class FailureCounter
        {
            public int Count;
        }
    }
}
