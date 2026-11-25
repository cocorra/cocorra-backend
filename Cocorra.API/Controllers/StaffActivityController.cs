using System;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Services.StaffActivityService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.StaffActivityDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public class StaffActivityController : ControllerBase
    {
        private const int MaxPageSize = 100;

        private readonly IStaffActivityService _staffActivityService;

        public StaffActivityController(IStaffActivityService staffActivityService)
        {
            _staffActivityService = staffActivityService;
        }

        [HttpGet(Router.AdminRouting.StaffActivity)]
        public async Task<IActionResult> GetStaffActivity(
            [FromRoute] Guid userId,
            [FromQuery] StaffActivityQueryDto query)
        {
            query.PageNumber = Math.Max(1, query.PageNumber);
            query.PageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);

            var result = await _staffActivityService.GetStaffActivityAsync(userId, query);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
