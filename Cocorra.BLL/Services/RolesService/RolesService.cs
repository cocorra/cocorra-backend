using Cocorra.DAL.DTOS;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.Role;
using Cocorra.DAL.Models;
using Cocorra.DAL.Enums;
using Cocorra.BLL.Base;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.RolesService
{
    public class RolesService : ResponseHandler, IRolesService
    {
        private readonly RoleManager<IdentityRole<Guid>> _roleManager;
        private readonly UserManager<ApplicationUser> _userManager;

        public RolesService(RoleManager<IdentityRole<Guid>> roleManager, UserManager<ApplicationUser> userManager)
        {
            _roleManager = roleManager;
            _userManager = userManager;
        }

        public async Task<Response<List<RoleDto>>> GetRolesAsync()
        {
            var roles = await _roleManager.Roles
                .Select(r => new RoleDto { Id = r.Id.ToString(), Name = r.Name! })
                .ToListAsync();
            return Success(roles);
        }

        public async Task<Response<RoleDto>> GetRoleByIdAsync(string roleId)
        {
            var role = await _roleManager.FindByIdAsync(roleId);
            if (role == null) return BadRequest<RoleDto>("Role not found");
            return Success(new RoleDto { Id = role.Id.ToString(), Name = role.Name! });
        }

        public async Task<Response<string>> ManageUserRolesAsync(ManageUserRolesDto model)
        {
            var user = await _userManager.FindByIdAsync(model.UserId.ToString());
            if (user == null) return BadRequest<string>("User not found");

            if (await _userManager.IsInRoleAsync(user, "Admin"))
                return BadRequest<string>("Cannot modify roles of an Admin account.");

            foreach (var role in model.Roles)
            {
                if (role.Equals("Admin", StringComparison.OrdinalIgnoreCase))
                    return BadRequest<string>("Cannot assign the Admin role through this endpoint.");

                if (!await _roleManager.RoleExistsAsync(role))
                    return BadRequest<string>($"Role '{role}' does not exist in the system.");
            }

            var currentRoles = await _userManager.GetRolesAsync(user);

            var rolesToAdd = model.Roles.Except(currentRoles).ToList();
            var rolesToRemove = currentRoles.Except(model.Roles).ToList();

            if (!rolesToAdd.Any() && !rolesToRemove.Any())
                return BadRequest<string>("No changes detected.");

            if (rolesToAdd.Any())
            {
                var addResult = await _userManager.AddToRolesAsync(user, rolesToAdd);
                if (!addResult.Succeeded) return BadRequest<string>("Failed to add roles: " + string.Join(", ", addResult.Errors.Select(e => e.Description)));
            }

            if (rolesToRemove.Any())
            {
                var removeResult = await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                if (!removeResult.Succeeded) return BadRequest<string>("Failed to remove roles: " + string.Join(", ", removeResult.Errors.Select(e => e.Description)));
            }

            return Success("User roles updated successfully");
        }

        public async Task<Response<List<UserDto>>> GetUsersInRoleAsync(string roleName)
        {
            if (!await _roleManager.RoleExistsAsync(roleName))
                return BadRequest<List<UserDto>>("Role not found");

            var users = await _userManager.GetUsersInRoleAsync(roleName);

            var usersDto = users.Select(u => new UserDto
            {
                Id = u.Id.ToString(),
                FullName = $"{u.FirstName} {u.LastName}",
                Email = u.Email ?? "",
                Age = u.Age,
                MBTI = u.MBTI ?? "N/A",
                Status = u.Status.ToString(),
                VoicePath = u.VoiceVerificationPath ?? ""
            }).ToList();

            return Success(usersDto);
        }

        public async Task<Response<UserDto>> CreateUserWithRoleAsync(CreateUserDto model)
        {
            if (string.IsNullOrWhiteSpace(model.Email))
                return BadRequest<UserDto>("Email is required.");

            var normalizedEmail = model.Email.Trim();
            var existingUser = await _userManager.FindByEmailAsync(normalizedEmail);
            if (existingUser != null)
                return BadRequest<UserDto>("A user with this email already exists.");

            var rolesToAssign = new List<string>();
            if (model.Roles != null && model.Roles.Any())
            {
                rolesToAssign.AddRange(model.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()));
            }
            else if (!string.IsNullOrWhiteSpace(model.Role))
            {
                rolesToAssign.Add(model.Role.Trim());
            }

            if (!rolesToAssign.Any())
            {
                rolesToAssign.Add("User");
            }

            foreach (var role in rolesToAssign)
            {
                if (!await _roleManager.RoleExistsAsync(role))
                {
                    return BadRequest<UserDto>($"Role '{role}' does not exist in the system.");
                }
            }

            var user = new ApplicationUser
            {
                UserName = normalizedEmail,
                Email = normalizedEmail,
                FirstName = model.FirstName?.Trim() ?? string.Empty,
                LastName = model.LastName?.Trim() ?? string.Empty,
                Age = model.Age > 0 ? model.Age : 25,
                EmailConfirmed = true,
                Status = UserStatus.Active,
                SecurityStamp = Guid.NewGuid().ToString(),
                CreatedAt = DateTime.UtcNow
            };

            var createResult = await _userManager.CreateAsync(user, model.Password);
            if (!createResult.Succeeded)
            {
                var errors = string.Join("; ", createResult.Errors.Select(e => e.Description));
                return BadRequest<UserDto>($"Failed to create user: {errors}");
            }

            var roleResult = await _userManager.AddToRolesAsync(user, rolesToAssign);
            if (!roleResult.Succeeded)
            {
                var errors = string.Join("; ", roleResult.Errors.Select(e => e.Description));
                return BadRequest<UserDto>($"User created, but role assignment failed: {errors}");
            }

            var userDto = new UserDto
            {
                Id = user.Id.ToString(),
                FullName = $"{user.FirstName} {user.LastName}".Trim(),
                Email = user.Email,
                Age = user.Age,
                MBTI = user.MBTI ?? "N/A",
                Status = user.Status.ToString(),
                CreatedAt = user.CreatedAt,
                Roles = rolesToAssign
            };

            return Success(userDto);
        }
    }
}