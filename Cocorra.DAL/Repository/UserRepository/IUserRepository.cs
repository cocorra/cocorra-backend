using Cocorra.DAL.DTOS.AdminDto;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cocorra.DAL.Repository.UserRepository
{
    public interface IUserRepository
    {
        Task<(int TotalCount, IEnumerable<UserDto> Users)> GetPaginatedUsersWithRolesAsync(string? search, int pageNumber, int pageSize);
    }
}
