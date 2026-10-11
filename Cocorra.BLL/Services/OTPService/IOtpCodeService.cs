using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>
    /// Issues and checks single-use email OTP codes stored as HMACs (see <c>OtpCode</c>).
    /// </summary>
    public interface IOtpCodeService
    {
        /// <summary>
        /// Creates a new 6-digit code for this user and purpose and returns it. Any earlier code
        /// for the same user and purpose stops working.
        /// </summary>
        Task<string> IssueAsync(Guid userId, string purpose, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns true and deletes the code if <paramref name="code"/> is the live, unexpired
        /// code for this user and purpose. Each call uses one of the code's attempts; once they
        /// run out the code is deleted and a new one must be requested.
        /// </summary>
        Task<bool> TryConsumeAsync(Guid userId, string purpose, string code, CancellationToken cancellationToken = default);
    }
}
