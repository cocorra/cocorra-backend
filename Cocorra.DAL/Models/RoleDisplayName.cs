using System;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// The label the admin dashboard shows for an Identity role. The role's own Name is what
    /// [Authorize(Roles = ...)], the seeders and the dashboard guards match on, so it never
    /// changes; renaming a role in the dashboard only writes here. No row means the dashboard
    /// shows the role's Name. One row per role; deleted with the role.
    /// </summary>
    public class RoleDisplayName
    {
        [Key]
        public Guid RoleId { get; set; }

        [Required, MaxLength(MaxLength)]
        public string DisplayName { get; set; } = string.Empty;

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public Guid? UpdatedByUserId { get; set; }

        public const int MaxLength = 50;
    }
}
