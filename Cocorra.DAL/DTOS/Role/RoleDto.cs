using System;
using System.Collections.Generic;
using System.Text;

namespace Cocorra.DAL.DTOS.Role
{
    public class RoleDto
    {
        public string Id { get; set; } = default!;
        public string Name { get; set; } = default!;
        /// <summary>What the dashboard shows; equals Name unless an admin set a label.</summary>
        public string DisplayName { get; set; } = default!;
    }
}
