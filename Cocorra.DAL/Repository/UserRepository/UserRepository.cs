using Cocorra.DAL.Data;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.Enums;
using Cocorra.DAL.Models;
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

        public async Task<(int TotalCount, IEnumerable<UserDto> Users)> GetPaginatedUsersWithRolesAsync(
            string? search,
            int pageNumber,
            int pageSize,
            bool excludeAdmins = false)
        {
            var query = _dbContext.Users.AsNoTracking().AsQueryable();

            if (excludeAdmins)
            {
                var adminRoleIds = await GetRoleIdsAsync(new[] { AppRoles.Admin });
                if (adminRoleIds.Count > 0)
                {
                    query = query.Where(u => !_dbContext.UserRoles
                        .Any(ur => ur.UserId == u.Id && adminRoleIds.Contains(ur.RoleId)));
                }
            }

            if (!string.IsNullOrEmpty(search))
            {
                // Moderators receive null emails, so restrict their search to names to prevent an email-lookup oracle.
                query = excludeAdmins
                    ? query.Where(u => u.FirstName!.Contains(search) || u.LastName!.Contains(search))
                    : query.Where(u => u.Email!.Contains(search) ||
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

            var users = rawUsers.Select(u => new UserDto
            {
                Id = u.Id.ToString(),
                FullName = $"{u.FirstName} {u.LastName}",
                Email = u.Email ?? "",
                Age = u.Age,
                MBTI = u.MBTI ?? "N/A",
                Status = u.Status.ToString(),
                CreatedAt = u.CreatedAt,
                VoicePath = u.VoiceVerificationPath,
                Roles = u.Roles!
            }).ToList();

            return (totalCount, users);
        }

        public async Task<(int TotalCount, List<PendingVoiceReviewRecord> Items)> GetPendingVoiceReviewsAsync(
            int pageNumber,
            int pageSize)
        {
            var protectedRoleIds = await GetRoleIdsAsync(AppRoles.ProtectedFromModerators);

            var query = _dbContext.Users
                .AsNoTracking()
                .Where(u => u.Status == UserStatus.Pending
                    && u.VoiceVerificationPath != null
                    && u.VoiceVerificationPath.Trim() != ""
                    && !u.VoiceVerificationPath.StartsWith("Error"));

            if (protectedRoleIds.Count > 0)
            {
                query = query.Where(u => !_dbContext.UserRoles
                    .Any(ur => ur.UserId == u.Id && protectedRoleIds.Contains(ur.RoleId)));
            }

            var totalCount = await query.CountAsync();

            var rows = await query
                .OrderBy(u => u.CreatedAt)
                .ThenBy(u => u.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new
                {
                    u.Id,
                    u.CreatedAt,
                    u.VoiceVerificationPath
                })
                .ToListAsync();

            var items = rows
                .Select(r => new PendingVoiceReviewRecord(
                    r.Id,
                    DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc),
                    r.VoiceVerificationPath!))
                .ToList();

            return (totalCount, items);
        }

        public async Task<Dictionary<Guid, UserIdentitySummary>> GetUserIdentitiesByIdsAsync(IEnumerable<Guid> userIds)
        {
            var distinctIds = userIds.Distinct().ToList();
            if (distinctIds.Count == 0)
                return new Dictionary<Guid, UserIdentitySummary>();

            var rows = await _dbContext.Users
                .AsNoTracking()
                .Where(u => distinctIds.Contains(u.Id))
                .Select(u => new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email
                })
                .ToListAsync();

            return rows.ToDictionary(
                r => r.Id,
                r => new UserIdentitySummary(r.Id, $"{r.FirstName} {r.LastName}".Trim(), r.Email));
        }

        private Task<List<Guid>> GetRoleIdsAsync(IEnumerable<string> roleNames)
        {
            var names = roleNames.ToList();
            var normalizedNames = names.Select(n => n.ToUpperInvariant()).ToList();

            return _dbContext.Roles
                .AsNoTracking()
                .Where(r => (r.Name != null && names.Contains(r.Name))
                    || (r.NormalizedName != null && normalizedNames.Contains(r.NormalizedName)))
                .Select(r => r.Id)
                .ToListAsync();
        }
    }
}
