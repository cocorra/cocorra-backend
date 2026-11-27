using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Authorization;

namespace Cocorra.API.Authorization
{
    /// <summary>
    /// Role-gated policies. Use these instead of <c>[Authorize(Roles = ...)]</c>: ASP.NET drops
    /// the default policy (and with it the VerificationStatus=Active requirement) for any
    /// attribute that names roles, so every policy here re-states that requirement.
    /// </summary>
    public static class AuthPolicies
    {
        public const string VerificationStatusClaim = "VerificationStatus";
        public const string ActiveStatus = "Active";

        public const string VerificationOnly = "VerificationOnly";
        public const string AdminOnly = "AdminOnly";
        public const string AdminOrModerator = "AdminOrModerator";
        public const string AdminOrRecordReviewer = "AdminOrRecordReviewer";
        public const string AdminOrCoach = "AdminOrCoach";

        public static void Register(AuthorizationOptions options)
        {
            options.DefaultPolicy = ActiveUser().Build();

            options.AddPolicy(VerificationOnly, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(VerificationStatusClaim, "Pending", "ReRecord", ActiveStatus));

            options.AddPolicy(AdminOnly, ActiveUser().RequireRole(AppRoles.Admin).Build());
            options.AddPolicy(AdminOrModerator, ActiveUser().RequireRole(AppRoles.Admin, AppRoles.Moderator).Build());
            options.AddPolicy(AdminOrRecordReviewer, ActiveUser().RequireRole(AppRoles.Admin, AppRoles.RecordReviewer).Build());
            options.AddPolicy(AdminOrCoach, ActiveUser().RequireRole(AppRoles.Admin, AppRoles.Coach).Build());
        }

        private static AuthorizationPolicyBuilder ActiveUser() => new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireClaim(VerificationStatusClaim, ActiveStatus);
    }
}
