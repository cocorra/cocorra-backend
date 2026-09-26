namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>
    /// Token purposes for email OTP codes. Each flow uses its own purpose so a code
    /// issued for one flow (e.g. email confirmation) can never be redeemed in another
    /// (e.g. password reset).
    /// </summary>
    public static class OtpPurposes
    {
        public const string EmailConfirmation = "EmailConfirmationOtp";
        public const string PasswordReset = "PasswordResetOtp";
    }
}
