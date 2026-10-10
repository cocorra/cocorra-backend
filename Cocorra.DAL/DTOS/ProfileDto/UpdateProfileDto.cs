using System.ComponentModel.DataAnnotations;
using Cocorra.DAL.AppMetaData;

namespace Cocorra.DAL.DTOS.ProfileDto
{
    public class UpdateProfileDto
    {
        [Required, MaxLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required, MaxLength(50)]
        public string LastName { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? Bio { get; set; }

        [Range(AgePolicy.MinimumAge, AgePolicy.MaximumAge, ErrorMessage = AgePolicy.RangeErrorMessage)]
        public int Age { get; set; }
    }
}