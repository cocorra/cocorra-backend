using System;
using System.Collections.Generic;
using System.Linq;

namespace Cocorra.DAL.Models
{
    /// <summary>
    /// The Identity role names. Matching is by Name, never by the dashboard display label, so
    /// renaming one of these is a data migration, not a refactor.
    /// </summary>
    public static class AppRoles
    {
        public const string Admin = "Admin";
        public const string Moderator = "Moderator";
        public const string RecordReviewer = "RecordReviewer";
        public const string Coach = "Coach";
        public const string User = "User";

        public static readonly IReadOnlyList<string> All = new[] { User, Admin, Coach, Moderator, RecordReviewer };

        /// <summary>Roles whose holders only an Admin may sanction or change.</summary>
        public static readonly IReadOnlySet<string> ProtectedFromModerators =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Admin, Moderator, RecordReviewer, Coach };

        /// <summary>Roles whose activity an Admin can review on the staff profile page.</summary>
        public static readonly IReadOnlySet<string> Staff =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Moderator, RecordReviewer, Coach };

        public static bool AnyProtectedFromModerators(IEnumerable<string> roles) =>
            roles.Any(ProtectedFromModerators.Contains);
    }
}
