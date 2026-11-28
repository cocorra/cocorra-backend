using System;
using System.Linq;
using System.Security.Claims;
using Cocorra.BLL.Base;
using Cocorra.DAL.Models;

namespace Cocorra.API.Authorization
{
    public static class StaffActorClaims
    {
        public static bool TryGetUserId(this ClaimsPrincipal? user, out Guid userId) =>
            Guid.TryParse(user?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);

        /// <summary>Admin or Moderator: sees counts, bypasses capacity and slow mode.</summary>
        public static bool IsTextRoomStaff(this ClaimsPrincipal? user) =>
            user is not null && (user.IsInRole(AppRoles.Admin) || user.IsInRole(AppRoles.Moderator));

        /// <summary>Null when the principal carries no usable user id.</summary>
        public static StaffActor? ToStaffActor(this ClaimsPrincipal? user)
        {
            if (!user.TryGetUserId(out var userId))
                return null;

            var roles = user!.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
            return new StaffActor(userId, user.IsInRole(AppRoles.Admin), roles.Length == 0 ? null : string.Join(",", roles));
        }
    }
}
