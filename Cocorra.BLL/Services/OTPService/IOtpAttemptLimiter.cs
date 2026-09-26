namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>
    /// Tracks failed OTP verification attempts and send cooldowns per purpose + email.
    /// Keys use the normalized email so unknown addresses are limited identically to
    /// real ones (no account enumeration).
    /// </summary>
    public interface IOtpAttemptLimiter
    {
        /// <summary>
        /// Atomically reserves one verification attempt BEFORE the code is checked.
        /// Returns false (caller must reject with 429) when the attempt budget for this
        /// email + purpose is exhausted; otherwise counts the attempt and returns true.
        /// Counting up front means concurrent bursts cannot all slip past the cap.
        /// </summary>
        bool TryRegisterAttempt(string email, string purpose);

        /// <summary>Clears the attempt counter (call after a successful verification).</summary>
        void Reset(string email, string purpose);

        /// <summary>
        /// Returns false if a code for this email + purpose was sent within the cooldown window;
        /// otherwise records the send and returns true.
        /// </summary>
        bool TryBeginSend(string email, string purpose);
    }
}
