using Cocorra.DAL.DTOS.AdminDto;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.UserRepository
{
    public sealed record PendingVoiceReviewRecord(Guid UserId, DateTime SubmittedAtUtc, string VoiceVerificationPath);

    public sealed record UserIdentitySummary(Guid UserId, string FullName, string? Email);

    public interface IUserRepository
    {
        Task<(int TotalCount, IEnumerable<UserDto> Users)> GetPaginatedUsersWithRolesAsync(
            string? search,
            int pageNumber,
            int pageSize,
            bool excludeAdmins = false);

        Task<(int TotalCount, List<PendingVoiceReviewRecord> Items)> GetPendingVoiceReviewsAsync(
            int pageNumber,
            int pageSize);

        Task<Dictionary<Guid, UserIdentitySummary>> GetUserIdentitiesByIdsAsync(IEnumerable<Guid> userIds);
    }
}
