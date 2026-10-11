namespace Cocorra.BLL.Services.OTPService
{
    /// <summary>Settings for stored email OTP codes, bound from the <c>Otp</c> section.</summary>
    public class OtpOptions
    {
        public const string SectionName = "Otp";
        public const int MinimumHmacKeyLength = 32;

        /// <summary>
        /// Secret key for hashing codes. Must not be stored in the database: it is what stops a
        /// database leak from revealing live codes. Program.cs refuses to start without it.
        /// </summary>
        public string HmacKey { get; set; } = string.Empty;
    }
}
