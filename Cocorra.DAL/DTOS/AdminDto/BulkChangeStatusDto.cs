using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.Enums;

namespace Cocorra.DAL.DTOS.AdminDto
{
    /// <summary>
    /// Request body for PUT /Api/V1/Admin/Users/BulkChangeStatus.
    /// Applies the same <see cref="NewStatus"/> to every user in <see cref="UserIds"/>.
    /// </summary>
    public class BulkChangeStatusDto
    {
        [Required]
        [MinLength(1, ErrorMessage = "At least one user id is required.")]
        public List<Guid> UserIds { get; set; } = new();

        [Required]
        public UserStatus NewStatus { get; set; }
    }
}
