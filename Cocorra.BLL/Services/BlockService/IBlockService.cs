using Cocorra.BLL.Base;
using System;
using System.Threading.Tasks;

namespace Cocorra.BLL.Services.BlockService
{
    public interface IBlockService
    {
        Task<Response<string>> BlockUserAsync(Guid currentUserId, string target);
        Task<Response<string>> UnblockUserAsync(Guid currentUserId, string target);
    }
}
