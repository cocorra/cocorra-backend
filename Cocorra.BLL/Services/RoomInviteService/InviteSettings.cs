namespace Cocorra.BLL.Services.RoomInviteService
{
    /// <summary>
    /// Bound from the "Invites" configuration section.
    /// </summary>
    public class InviteSettings
    {
        public const string SectionName = "Invites";

        /// <summary>Invite URLs are {PublicBaseUrl}/invite/{code}.</summary>
        public string PublicBaseUrl { get; set; } = "https://cocorraapp.com";

        /// <summary>Lifetime used when the create request does not ask for one.</summary>
        public int DefaultLifetimeHours { get; set; } = 24;

        /// <summary>Upper bound on a requested lifetime; requests above it are clamped.</summary>
        public int MaxLifetimeHours { get; set; } = 168;

        /// <summary>
        /// Anti-spam cap: how many still-usable invites one user may hold for one room at a time.
        /// </summary>
        public int MaxActivePerInviterPerRoom { get; set; } = 20;
    }
}
