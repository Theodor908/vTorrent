// src/vTorrent.Server/Controllers/SessionController.cs
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Server.Services;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/session")]
public class SessionController : ControllerBase
{
    private readonly ITorrentService _torrentService;
    private readonly ServerTorrentService _serverService;

    public SessionController(ITorrentService torrentService, ServerTorrentService serverService)
    {
        _torrentService = torrentService;
        _serverService = serverService;
    }

    [HttpGet("stats")]
    public IActionResult GetStats() => Ok(_torrentService.SessionStats);

    [HttpGet("counts")]
    public IActionResult GetCounts() => Ok(new
    {
        downloading = _torrentService.GetDownloadingCount(),
        seeding = _torrentService.GetSeedingCount(),
        paused = _torrentService.GetPausedCount(),
        completed = _torrentService.GetCompletedCount()
    });

    [HttpGet("settings")]
    public IActionResult GetSettings() => Ok(_serverService.GetRedactedSettings());

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] Models.UpdateSettingsRequest request)
    {
        await _serverService.UpdateSettingsAsync(request.Settings);
        return NoContent();
    }
}
