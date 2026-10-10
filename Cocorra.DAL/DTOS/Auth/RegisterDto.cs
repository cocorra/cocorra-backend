using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Cocorra.DAL.AppMetaData;

namespace Cocorra.BLL.DTOS.Auth
{
    public class RegisterDto
    {
        [Required, MaxLength(100)]
        public string? FirstName { get; set; }

        [Required, MaxLength(100)]
        public string? LastName { get; set; }

        [Required, Range(AgePolicy.MinimumAge, AgePolicy.MaximumAge, ErrorMessage = AgePolicy.RangeErrorMessage)]
        public int Age { get; set; }

        [Required, EmailAddress, MaxLength(100), MinLength(5)]
        public string? Email { get; set; }

        [Required, MinLength(6), MaxLength(100), DataType(DataType.Password)]
        public string? Password { get; set; }

        [Required]
        // تأكيد الباسورد عشان يماتش الديزاين
        [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
        public string? ConfirmPassword { get; set; }

        [Required]
        public IFormFile? VoiceVerification { get; set; }
        [Required]
        public IFormFile? ProfilePicture { get; set; }
    }
}