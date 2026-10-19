using Cocorra.BLL.Services.RolesService;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS;
using Cocorra.DAL.DTOS.Role;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Roles = "Admin")] 
    public class RolesController : ControllerBase
    {
        private const string UnresolvedAdminMessage = "Could not resolve the acting admin identity.";

        private readonly IRolesService _rolesService;

        public RolesController(IRolesService rolesService)
        {
            _rolesService = rolesService;
        }

        [HttpGet(Router.RolesRouting.GetRoles)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetRoles()
        {
            var result = await _rolesService.GetRolesAsync();
            return Ok(result);
        }

        [HttpGet(Router.RolesRouting.GetRoleById)]
        public async Task<IActionResult> GetRoleById([FromRoute] string id)
        {
            var result = await _rolesService.GetRoleByIdAsync(id);
            if (!result.Succeeded) return BadRequest(result);

            return Ok(result);
        }

        [HttpPut(Router.RolesRouting.UpdateDisplayName)]
        public async Task<IActionResult> UpdateRoleDisplayName([FromRoute] string id, [FromBody] UpdateRoleDisplayNameDto model)
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminGuid))
            {
                return BadRequest(new { succeeded = false, message = UnresolvedAdminMessage });
            }

            var result = await _rolesService.UpdateRoleDisplayNameAsync(id, model, adminGuid);

            if (!result.Succeeded) return BadRequest(result);

            return Ok(result);
        }

        [HttpPost(Router.RolesRouting.ManageUserRoles)]
        public async Task<IActionResult> ManageUserRoles([FromBody] ManageUserRolesDto model)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminGuid))
            {
                return BadRequest(new { succeeded = false, message = UnresolvedAdminMessage });
            }

            var result = await _rolesService.ManageUserRolesAsync(model, adminGuid);

            if (!result.Succeeded) return BadRequest(result);

            return Ok(result);
        }

        [HttpGet(Router.RolesRouting.GetUsersInRole)]
        public async Task<IActionResult> GetUsersInRole([FromRoute] string roleName)
        {
            var result = await _rolesService.GetUsersInRoleAsync(roleName);

            if (!result.Succeeded) return BadRequest(result);

            return Ok(result);
        }

        [HttpPost(Router.RolesRouting.CreateUser)]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserDto model)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var adminGuid))
            {
                return BadRequest(new { succeeded = false, message = UnresolvedAdminMessage });
            }

            var result = await _rolesService.CreateUserWithRoleAsync(model, adminGuid);

            if (!result.Succeeded) return BadRequest(result);

            return Ok(result);
        }
    }
}