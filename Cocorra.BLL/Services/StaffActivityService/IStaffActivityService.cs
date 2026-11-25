using System;
using System.Threading.Tasks;
using Cocorra.BLL.Base;
using Cocorra.DAL.DTOS.StaffActivityDto;

namespace Cocorra.BLL.Services.StaffActivityService
{
    public interface IStaffActivityService
    {
        Task<Response<StaffActivityPageDto>> GetStaffActivityAsync(Guid staffUserId, StaffActivityQueryDto? query);
    }
}
