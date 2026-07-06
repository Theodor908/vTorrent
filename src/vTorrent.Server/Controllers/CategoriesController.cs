// src/vTorrent.Server/Controllers/CategoriesController.cs
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Interfaces.Services;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/categories")]
public class CategoriesController : ControllerBase
{
    private readonly ITorrentService _torrentService;

    public CategoriesController(ITorrentService torrentService) => _torrentService = torrentService;

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _torrentService.GetAllCategoriesAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest request)
    {
        var category = await _torrentService.CreateCategoryAsync(request.Name, request.Color, request.SavePath);
        return CreatedAtAction(nameof(List), new { id = category.Id }, category);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCategoryRequest request)
    {
        await _torrentService.UpdateCategoryAsync(id, request.Name, request.Color, request.SavePath);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await _torrentService.DeleteCategoryAsync(id);
        return NoContent();
    }
}

public record CreateCategoryRequest(string Name, string? Color = null, string? SavePath = null);
public record UpdateCategoryRequest(string Name, string? Color = null, string? SavePath = null);
