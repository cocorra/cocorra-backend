using Cocorra.BLL.Services.BlockService;
using Cocorra.DAL.AppMetaData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize]
    public class BlockController : ControllerBase
    {
        private readonly IBlockService _blockService;

        public BlockController(IBlockService blockService)
        {
            _blockService = blockService;
        }

        [HttpPost(Router.BlockRouting.Block)]
        public async Task<IActionResult> BlockUser(string target)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out Guid currentUserId))
            {
                return Unauthorized();
            }

            var result = await _blockService.BlockUserAsync(currentUserId, target);

            if (result.Succeeded)
                return Ok(result);
            return BadRequest(result);
        }

        [HttpDelete(Router.BlockRouting.Unblock)]
        public async Task<IActionResult> UnblockUser(string target)
        {
            var userIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userIdStr) || !Guid.TryParse(userIdStr, out Guid currentUserId))
            {
                return Unauthorized();
            }

            var result = await _blockService.UnblockUserAsync(currentUserId, target);

            if (result.Succeeded)
                return Ok(result);
            return BadRequest(result);
        }
    }
}
