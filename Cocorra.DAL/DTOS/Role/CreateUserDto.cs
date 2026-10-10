using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Cocorra.DAL.DTOS.Role
{
    public class CreateUserDto
    {
        [Required(ErrorMessage = "First name is required"), MaxLength(50)]
        public string FirstName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Last name is required"), MaxLength(50)]
        public string LastName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Invalid email format"), MaxLength(100)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required"), MinLength(8, ErrorMessage = "Password must be at least 8 characters")]
        public string Password { get; set; } = string.Empty;

        [Range(10, 120, ErrorMessage = "Age must be between 10 and 120")]
        public int Age { get; set; } = 25;

        public string? Role { get; set; }
        public List<string>? Roles { get; set; }
    }
}
