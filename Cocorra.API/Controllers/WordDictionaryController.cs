using System;
using System.Threading.Tasks;
using Cocorra.API.Authorization;
using Cocorra.BLL.Services.ContentModeration;
using Cocorra.DAL.AppMetaData;
using Cocorra.DAL.DTOS.WordDictionaryDto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Cocorra.API.Controllers
{
    [ApiController]
    [Authorize(Policy = AuthPolicies.AdminOnly)]
    public class WordDictionaryController : ControllerBase
    {
        private readonly IWordDictionaryService _service;

        public WordDictionaryController(IWordDictionaryService service)
        {
            _service = service;
        }

        [HttpGet(Router.WordDictionaryRouting.List)]
        public async Task<IActionResult> GetList([FromQuery] WordDictionaryQueryDto query)
        {
            var result = await _service.GetPagedAsync(query);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPost(Router.WordDictionaryRouting.Create)]
        public async Task<IActionResult> Create([FromBody] WordDictionaryUpsertDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _service.CreateAsync(dto, actor);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPut(Router.WordDictionaryRouting.ById)]
        public async Task<IActionResult> Update([FromRoute] Guid id, [FromBody] WordDictionaryUpsertDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _service.UpdateAsync(id, dto, actor);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpDelete(Router.WordDictionaryRouting.ById)]
        public async Task<IActionResult> Delete([FromRoute] Guid id)
        {
            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _service.DeleteAsync(id, actor);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPut(Router.WordDictionaryRouting.Active)]
        public async Task<IActionResult> SetActive([FromRoute] Guid id, [FromBody] SetActiveDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _service.SetActiveAsync(id, dto.IsActive, actor);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPost(Router.WordDictionaryRouting.Bulk)]
        public async Task<IActionResult> BulkAdd([FromBody] WordDictionaryBulkAddDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var actor = User.ToStaffActor();
            if (actor is null)
                return Unauthorized();

            var result = await _service.BulkAddAsync(dto, actor);
            return StatusCode((int)result.StatusCode, result);
        }

        [HttpPost(Router.WordDictionaryRouting.Test)]
        public async Task<IActionResult> Test([FromBody] WordFilterTestRequestDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = await _service.TestAsync(dto.Text);
            return StatusCode((int)result.StatusCode, result);
        }
    }
}
