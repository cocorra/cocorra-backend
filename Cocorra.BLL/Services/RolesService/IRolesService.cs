using Cocorra.DAL.DTOS;
using Cocorra.DAL.DTOS.AdminDto;
using Cocorra.DAL.DTOS.Role;
using Cocorra.BLL.Base;

namespace Cocorra.BLL.Services.RolesService
{
    public interface IRolesService
    {
        Task<Response<List<RoleDto>>> GetRolesAsync();
        Task<Response<RoleDto>> GetRoleByIdAsync(string roleId);
        Task<Response<string>> ManageUserRolesAsync(ManageUserRolesDto model);
        Task<Response<List<UserDto>>> GetUsersInRoleAsync(string roleName);
        Task<Response<UserDto>> CreateUserWithRoleAsync(CreateUserDto model);
    }
}
