using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.UserRepository
{
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _dbContext;

        public UserRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<(int TotalCount, IEnumerable<UserDto> Users)> GetPaginatedUsersWithRolesAsync(string? search, int pageNumber, int pageSize, string baseUrl)
        {
            var query = _dbContext.Users.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                query = query.Where(u => u.Email!.Contains(search) ||
                                         u.FirstName!.Contains(search) ||
                                         u.LastName!.Contains(search));
            }

            var totalCount = await query.CountAsync();

            var rawUsers = await query
                .OrderByDescending(u => u.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email,
                    u.Age,
                    u.MBTI,
                    u.Status,
                    u.VoiceVerificationPath,
                    u.CreatedAt,
                    Roles = _dbContext.UserRoles
                        .Where(ur => ur.UserId == u.Id)
                        .Join(_dbContext.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
                        .ToList()
                })
                .ToListAsync();

            string? BuildFullUrl(string? relativePath)
            {
                if (string.IsNullOrWhiteSpace(relativePath)) return null;
                if (relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return relativePath;
                }
                return $"{baseUrl}/{relativePath.Replace("\\", "/").TrimStart('/')}";
            }

            var users = rawUsers.Select(u => new UserDto
            {
                Id = u.Id.ToString(),
                FullName = $"{u.FirstName} {u.LastName}",
                Email = u.Email ?? "",
                Age = u.Age,
                MBTI = u.MBTI ?? "N/A",
                Status = u.Status.ToString(),
                CreatedAt = u.CreatedAt,
                VoicePath = BuildFullUrl(u.VoiceVerificationPath),
                Roles = u.Roles!
            }).ToList();

            return (totalCount, users);
        }
    }
}
