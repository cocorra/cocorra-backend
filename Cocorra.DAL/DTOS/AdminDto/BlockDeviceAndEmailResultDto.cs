using System;

namespace Cocorra.DAL.DTOS.AdminDto
{
    public class BlockDeviceAndEmailResultDto
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = null!;

        /// <summary>Always true when the call succeeds — the account ban never depends on device data.</summary>
        public bool AccountBanned { get; set; }

        /// <summary>
        /// How many of the user's registered devices are now blocked. Zero is a legitimate
        /// outcome (the user never logged in from a client that sends X-Device-Id) and the
        /// dashboard must surface it rather than implying devices were blocked.
        /// </summary>
        public int DevicesBlocked { get; set; }
    }
}
