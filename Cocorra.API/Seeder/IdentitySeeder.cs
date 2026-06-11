using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
using Microsoft.AspNetCore.Identity;

namespace Cocorra.API.Seeder
{
    public class IdentitySeeder
    {
        public static async Task SeedAsync(
      UserManager<ApplicationUser> userManager,
      RoleManager<IdentityRole<Guid>> roleManager,
      IConfiguration configuration)
        {
            string adminEmail = configuration["SeedAdmin:Email"]!;
            string adminPassword = configuration["SeedAdmin:Password"]!;

            var user = await userManager.FindByEmailAsync(adminEmail);

            if (user == null)
            {
                user = new ApplicationUser
                {
                    UserName = adminEmail,
                    Email = adminEmail,
                    EmailConfirmed = true,
                    Status = UserStatus.Active,
                    SecurityStamp = Guid.NewGuid().ToString() // Ensures SecurityStamp is not null
                };

                var result = await userManager.CreateAsync(user, adminPassword);
                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    throw new Exception($"Failed to create admin user: {errors}");
                }

                await userManager.AddToRolesAsync(user, new[] { "Admin", "Coach" });
            }
            else
            {
                // Fix for existing users that might have a null SecurityStamp in the DB
                if (string.IsNullOrEmpty(user.SecurityStamp))
                {
                    user.SecurityStamp = Guid.NewGuid().ToString();
                    await userManager.UpdateAsync(user);
                }

                // Ensure existing seeder account has roles just in case
                if (!await userManager.IsInRoleAsync(user, "Admin"))
                    await userManager.AddToRoleAsync(user, "Admin");
                    
                if (!await userManager.IsInRoleAsync(user, "Coach"))
                    await userManager.AddToRoleAsync(user, "Coach");
            }
        }
    }
}