using System;

namespace Cocorra.BLL.Base
{
    /// <summary>
    /// The staff member performing an action, resolved by the API layer from the caller's claims.
    /// <paramref name="Roles"/> is the comma-separated role list, recorded on audit rows written
    /// outside an HTTP request (hub methods).
    /// </summary>
    public sealed record StaffActor(Guid UserId, bool IsAdmin, string? Roles = null);
}
