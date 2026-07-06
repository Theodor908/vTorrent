// src/vTorrent.Server/Controllers/TagsController.cs
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Interfaces.Services;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/tags")]
public class TagsController : ControllerBase
{
    private readonly ITorrentService _torrentService;

    public TagsController(ITorrentService torrentService) => _torrentService = torrentService;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _torrentService.GetAllTagsAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTagRequest request)
    {
        var tag = await _torrentService.CreateTagAsync(request.Name, request.Color);
        return CreatedAtAction(nameof(List), new { id = tag.Id }, tag);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateTagRequest request)
    {
        await _torrentService.UpdateTagAsync(id, request.Name, request.Color);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _torrentService.DeleteTagAsync(id);
        return NoContent();
    }
}

public record CreateTagRequest(string Name, string? Color = null);
public record UpdateTagRequest(string Name, string? Color = null);
