// src/vTorrent.Server/Controllers/DhtController.cs
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Interfaces.Services;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/dht")]
public class DhtController : ControllerBase
{
    private readonly ITorrentService _torrentService;

    public DhtController(ITorrentService torrentService) => _torrentService = torrentService;

    [HttpGet]
    public IActionResult GetStatus() => Ok(new
    {
        isRunning = _torrentService.IsDhtRunning,
        isEnabled = _torrentService.IsDhtEnabled,
        nodeCount = _torrentService.DhtNodeCount
    });

    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle()
    {
        await _torrentService.ToggleDhtAsync();
        return NoContent();
    }
}
