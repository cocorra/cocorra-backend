using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// The one live email OTP for a user and purpose. Only an HMAC of the code is stored, keyed
    /// with a secret that is not in the database, so a database leak alone cannot recover codes.
    /// Issuing a new code overwrites this row; using the code, running out of attempts or
    /// expiry deletes it. Unique on (UserId, Purpose) in AppDbContext.
    /// </summary>
    public class OtpCode : BaseEntity
    {
        public Guid UserId { get; set; }
        [ForeignKey(nameof(UserId))]
        public virtual ApplicationUser? User { get; set; }

        /// <summary>One of <c>OtpPurposes</c>; a code for one flow cannot be used in another.</summary>
        [Required]
        [MaxLength(64)]
        public string Purpose { get; set; } = string.Empty;

        /// <summary>HMAC-SHA256 of purpose, user id and code.</summary>
        [Required]
        [MaxLength(32)]
        public byte[] CodeHash { get; set; } = Array.Empty<byte>();

        public DateTime ExpiresAt { get; set; }

        /// <summary>Verification attempts made against this code, counted before each check.</summary>
        public int Attempts { get; set; }
    }
}
