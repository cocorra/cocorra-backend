using Cocorra.DAL.Models;
using Cocorra.DAL.Repository.GenericRepository;
using System;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.UserBlockRepository
{
    public interface IUserBlockRepository : IGenericRepositoryAsync<UserBlock>
    {
        Task BlockUserAsync(Guid blockerId, Guid blockedId );
        Task UnblockUserAsync(Guid blockerId, Guid blockedId);
        Task<bool> IsBlockedAsync(Guid userId1, Guid userId2);
    }
}
