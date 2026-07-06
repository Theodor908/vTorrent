// src/vTorrent.Server/Controllers/TorrentsController.cs
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Enums;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Server.Models;
using vTorrent.Server.Services;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ServerTorrentService _serverService;
    private readonly ITorrentService _torrentService;

    // Bind the multipart `options` JSON regardless of property casing (camelCase or PascalCase).
    private static readonly JsonSerializerOptions CaseInsensitiveJson =
        new() { PropertyNameCaseInsensitive = true };

    public TorrentsController(ServerTorrentService serverService, ITorrentService torrentService)
    {
        _serverService = serverService;
        _torrentService = torrentService;
    }

    [HttpGet]
    public IActionResult List(
        [FromQuery] string? phase, [FromQuery] string? intent, [FromQuery] string? health,
        [FromQuery] int? category, [FromQuery] string? tag,
        [FromQuery] string? sort, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        var result = _serverService.GetTorrents(phase, intent, health, category, tag, sort, limit, offset);
        return Ok(result);
    }

    [HttpGet("{hash}")]
    public IActionResult Get(string hash)
    {
        var snapshot = _torrentService.GetTorrent(hash);
        return snapshot == null ? NotFound(new ErrorResponse("Torrent not found", "TORRENT_NOT_FOUND")) : Ok(snapshot);
    }

    [HttpGet("{hash}/details")]
    public IActionResult GetDetails(string hash)
    {
        var details = _torrentService.GetTorrentDetails(hash);
        return details == null ? NotFound(new ErrorResponse("Torrent not found", "TORRENT_NOT_FOUND")) : Ok(details);
    }

    [HttpGet("{hash}/pieces")]
    public IActionResult GetPieces(string hash)
    {
        var states = _torrentService.GetPieceStates(hash);
        return states == null ? NotFound(new ErrorResponse("Torrent not found or engine not running", "TORRENT_NOT_FOUND")) : Ok(states);
    }

    [HttpPost]
    [RequestSizeLimit(10_485_760)] // 10 MB max torrent file
    public async Task<IActionResult> AddTorrent([FromForm] IFormFile torrentFile, [FromForm] string? options)
    {
        if (torrentFile == null || torrentFile.Length == 0)
            return BadRequest(new ErrorResponse("No torrent file provided", "INVALID_TORRENT_FILE"));

        var tempPath = Path.GetTempFileName();
        try
        {
            await using (var stream = System.IO.File.Create(tempPath))
                await torrentFile.CopyToAsync(stream);

            var request = string.IsNullOrEmpty(options)
                ? new AddTorrentRequest()
                // Case-insensitive so camelCase `options` (e.g. {"savePath":...}) binds the same
                // as PascalCase; previously camelCase silently fell back to defaults (wrong save path).
                : JsonSerializer.Deserialize<AddTorrentRequest>(options, CaseInsensitiveJson) ?? new AddTorrentRequest();

            var infoHash = await _serverService.AddTorrentAsync(tempPath, request);
            return Ok(new { infoHash });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath)) System.IO.File.Delete(tempPath);
        }
    }

    [HttpPost("magnet")]
    public async Task<IActionResult> AddMagnet([FromBody] AddMagnetRequest request)
    {
        if (string.IsNullOrEmpty(request.MagnetUri))
            return BadRequest(new ErrorResponse("Magnet URI is required", "INVALID_MAGNET"));

        var infoHash = await _serverService.AddMagnetAsync(request);
        return Ok(new { infoHash });
    }

    [HttpPost("{hash}/pause")]
    public async Task<IActionResult> Pause(string hash) { await _torrentService.PauseTorrentAsync(hash); return NoContent(); }

    [HttpPost("{hash}/resume")]
    public async Task<IActionResult> Resume(string hash) { await _torrentService.ResumeTorrentAsync(hash); return NoContent(); }

    [HttpPost("{hash}/force-start")]
    public async Task<IActionResult> ForceStart(string hash) { await _torrentService.ForceStartAsync(hash); return NoContent(); }

    [HttpPost("{hash}/recheck")]
    public async Task<IActionResult> Recheck(string hash) { await _torrentService.ForceRecheckAsync(hash); return NoContent(); }

    [HttpPost("{hash}/super-seed")]
    public async Task<IActionResult> SuperSeed(string hash) { await _torrentService.ToggleSuperSeedingAsync(hash); return NoContent(); }

    [HttpPost("{hash}/location")]
    public async Task<IActionResult> ChangeLocation(string hash, [FromBody] ChangeLocationRequest request)
    {
        var success = await _torrentService.ChangeLocationAsync(hash, request.SavePath);
        return success ? NoContent() : Conflict(new ErrorResponse("Failed to move torrent files", "MOVE_FAILED"));
    }

    [HttpPost("pause-all")]
    public async Task<IActionResult> PauseAll() { await _torrentService.PauseAllAsync(); return NoContent(); }

    [HttpPost("resume-all")]
    public async Task<IActionResult> ResumeAll() { await _torrentService.ResumeAllAsync(); return NoContent(); }

    [HttpDelete("{hash}")]
    public async Task<IActionResult> Remove(string hash,
        [FromQuery] bool deleteFiles = false, [FromQuery] bool secureWipe = false, [FromQuery] bool wipeMetadata = false)
    {
        var result = await _torrentService.RemoveTorrentAsync(hash, deleteFiles, secureWipe, wipeMetadata);
        return result == null ? NotFound(new ErrorResponse("Torrent not found", "TORRENT_NOT_FOUND")) : Ok(result);
    }

    [HttpPut("{hash}/settings")]
    public IActionResult UpdateSettings(string hash, [FromBody] TorrentSettings settings)
    {
        _torrentService.ApplyTorrentSettings(hash, settings);
        return NoContent();
    }

    [HttpPut("{hash}/files/priorities")]
    public async Task<IActionResult> SetFilePriorities(string hash, [FromBody] SetFilePrioritiesRequest request)
    {
        var priorities = request.Priorities.Select(p => (p.FileIndex, p.Priority)).ToList();
        await _torrentService.SetFilePrioritiesAsync(hash, priorities);
        return NoContent();
    }

    [HttpPut("{hash}/category")]
    public async Task<IActionResult> SetCategory(string hash, [FromBody] SetCategoryRequest request)
    {
        await _torrentService.SetTorrentCategoryAsync(hash, request.CategoryId);
        return NoContent();
    }

    [HttpGet("{hash}/tags")]
    public async Task<IActionResult> GetTags(string hash)
    {
        var tags = await _torrentService.GetTorrentTagsAsync(hash);
        return Ok(tags);
    }

    [HttpPut("{hash}/tags")]
    public async Task<IActionResult> SetTags(string hash, [FromBody] SetTagsRequest request)
    {
        await _torrentService.SetTorrentTagsAsync(hash, request.TagIds);
        return NoContent();
    }

    [HttpPost("{hash}/queue/top")]
    public IActionResult QueueTop(string hash) { _torrentService.SetQueuePositionTop(hash); return NoContent(); }

    [HttpPost("{hash}/queue/bottom")]
    public IActionResult QueueBottom(string hash) { _torrentService.SetQueuePositionBottom(hash); return NoContent(); }

    [HttpPost("{hash}/queue/up")]
    public IActionResult QueueUp(string hash) { _torrentService.SetQueuePositionUp(hash); return NoContent(); }

    [HttpPost("{hash}/queue/down")]
    public IActionResult QueueDown(string hash) { _torrentService.SetQueuePositionDown(hash); return NoContent(); }
}

// Inline request records for small models
public record ChangeLocationRequest(string SavePath);
public record SetCategoryRequest(int? CategoryId);
public record SetTagsRequest(System.Collections.Generic.IEnumerable<int> TagIds);
