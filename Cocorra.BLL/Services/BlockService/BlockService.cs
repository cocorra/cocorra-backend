using Cocorra.BLL.Base;
using Cocorra.DAL.Repository.UserBlockRepository;
using Microsoft.AspNetCore.Identity;
using System;
using System.Threading.Tasks;
using Cocorra.DAL.Models;
using Cocorra.BLL.Services.BlockedDevicesService;
using Cocorra.DAL.DTOS.BlockedDevicesDto;

namespace Cocorra.BLL.Services.BlockService
{
    public class BlockService : ResponseHandler, IBlockService
    {
        private readonly IUserBlockRepository _blockRepo;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IBlockedDevicesService _blockedDevicesService; 
        public BlockService(IUserBlockRepository blockRepo, UserManager<ApplicationUser> userManager,IBlockedDevicesService blockedDevicesService)
        {
            _blockRepo = blockRepo;
            _userManager = userManager;
            _blockedDevicesService = blockedDevicesService;
        }

        public async Task<Response<string>> BlockUserAsync(Guid currentUserId, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return BadRequest<string>("Target identifier is required.");

            ApplicationUser? targetUser = null;

            if (Guid.TryParse(target, out Guid targetUserId))
            {
                if (currentUserId == targetUserId)
                    return BadRequest<string>("You cannot block yourself.");

                targetUser = await _userManager.FindByIdAsync(target);
            }
            else
            {
                targetUser = await _userManager.FindByEmailAsync(target);
                if (targetUser != null && targetUser.Id == currentUserId)
                    return BadRequest<string>("You cannot block yourself.");
            }

            if (targetUser == null) 
                return NotFound<string>("Target user not found.");

            await _blockRepo.BlockUserAsync(currentUserId, targetUser.Id);  

            // 3. الطرد الفوري (تدمير الـ RefreshToken لطرده من التطبيق فوراً)
            targetUser.RefreshToken = null;
            targetUser.RefreshTokenExpiryTime = DateTime.UtcNow;
            await _userManager.UpdateAsync(targetUser);

            return Success("User blocked successfully.");
        }

        public async Task<Response<string>> UnblockUserAsync(Guid currentUserId, string target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return BadRequest<string>("Target identifier is required.");

            ApplicationUser? targetUser = null;

            if (Guid.TryParse(target, out Guid targetUserId))
            {
                targetUser = await _userManager.FindByIdAsync(target);
            }
            else
            {
                targetUser = await _userManager.FindByEmailAsync(target);
            }

            if (targetUser == null) 
                return NotFound<string>("Target user not found.");

            // 1. فك الحظر بين الحسابين
            await _blockRepo.UnblockUserAsync(currentUserId, targetUser.Id);

            return Success("User unblocked successfully.");
        }
    }
}