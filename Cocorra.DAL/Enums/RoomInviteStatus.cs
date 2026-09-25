namespace Cocorra.DAL.Enums;

/// <summary>
/// Only Active, Used and Revoked are ever stored. Expired is computed at read time (the
/// invite's ExpiresAt has passed, or its room is no longer joinable), so no background job
/// is needed to flip rows; the value exists so API responses and stats can report it.
/// </summary>
public enum RoomInviteStatus
{
    Active = 0,
    Used = 1,
    Expired = 2,
    Revoked = 3
}
