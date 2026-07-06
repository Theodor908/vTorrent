using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using vTorrent.Abstractions.Interfaces.Services;
using vTorrent.Abstractions.Settings;
using vTorrent.Core.Settings;

namespace vTorrent.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/profiles")]
public class ProfilesController : ControllerBase
{
    private readonly ProfileManager _profileManager;
    private readonly SettingsManager _settingsManager;
    private readonly ITorrentService _torrentService;
    private readonly ScheduleExporter? _scheduleExporter;

    public ProfilesController(
        ProfileManager profileManager,
        SettingsManager settingsManager,
        ITorrentService torrentService,
        ScheduleExporter? scheduleExporter = null)
    {
        _profileManager = profileManager;
        _settingsManager = settingsManager;
        _torrentService = torrentService;
        _scheduleExporter = scheduleExporter;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var profiles = await _profileManager.LoadAllAsync();
        var result = profiles.Select(p => new { name = p.Name, color = p.Color, scope = p.Scope });
        return Ok(result);
    }

    [HttpGet("active")]
    public IActionResult GetActive()
    {
        var settings = _settingsManager.Current;
        return Ok(new
        {
            name = settings.ActiveProfileName,
            color = settings.ActiveProfileColor,
            scheduleEnabled = settings.Schedule.Enabled
        });
    }

    [HttpPut("active")]
    public async Task<IActionResult> Activate([FromBody] ActivateProfileRequest request)
    {
        if (_settingsManager.Current.Schedule.Enabled)
            return Conflict(new { error = "Cannot switch profiles while schedule is active. Disable the schedule first." });

        var profiles = await _profileManager.LoadAllAsync();
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Name, request.Name, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
            return NotFound(new { error = $"Profile '{request.Name}' not found." });

        await _settingsManager.UpdateAndSaveAsync(gs =>
        {
            profile.Settings.ApplyTo(gs);
            gs.ActiveProfileName = profile.Name;
            gs.ActiveProfileColor = profile.Color;
        });

        _torrentService.NotifyProfileChanged(profile.Name);
        return NoContent();
    }

    [HttpGet("schedule")]
    public async Task<IActionResult> GetSchedule()
    {
        var settings = _settingsManager.Current;
        var profiles = await _profileManager.LoadAllAsync();
        var colorMap = profiles.ToDictionary(
            p => p.Name, p => p.Color, StringComparer.OrdinalIgnoreCase);

        var grid = new object[7][];
        for (int uiDay = 0; uiDay < 7; uiDay++)
        {
            int internalDay = (uiDay + 1) % 7;
            var dayRow = new object[24];
            for (int hour = 0; hour < 24; hour++)
            {
                var cell = settings.Schedule.Grid[internalDay][hour];
                string color = cell.Mode switch
                {
                    ScheduleCellMode.SeedOnly => "#FFC107",
                    ScheduleCellMode.Paused => "#3C3C3C",
                    _ => colorMap.GetValueOrDefault(cell.ProfileName ?? "Balanced", "#2196F3")
                };
                dayRow[hour] = new
                {
                    mode = cell.Mode.ToString(),
                    profileName = cell.ProfileName,
                    color
                };
            }
            grid[uiDay] = dayRow;
        }

        return Ok(new { enabled = settings.Schedule.Enabled, grid });
    }

    [HttpPut("schedule/toggle")]
    public async Task<IActionResult> ToggleSchedule([FromBody] ToggleScheduleRequest request)
    {
        await _settingsManager.UpdateAndSaveAsync(gs =>
        {
            gs.Schedule.Enabled = request.Enabled;
        });

        _torrentService.NotifyScheduleToggled(request.Enabled);
        return NoContent();
    }

    /// <summary>GET /api/v1/profiles/schedule/export — download .vtschedule.json.</summary>
    [HttpGet("schedule/export")]
    public async Task<IActionResult> ExportSchedule()
    {
        if (_scheduleExporter == null)
            return StatusCode(503, new { error = "Schedule exporter not available." });

        var stream = new MemoryStream();
        await _scheduleExporter.ExportToStreamAsync(stream);
        stream.Position = 0;

        return File(stream, "application/json", "schedule.vtschedule.json");
    }

    /// <summary>POST /api/v1/profiles/schedule/import — upload .vtschedule.json.</summary>
    [HttpPost("schedule/import")]
    public async Task<IActionResult> ImportSchedule(IFormFile file)
    {
        if (_scheduleExporter == null)
            return StatusCode(503, new { error = "Schedule exporter not available." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        using var stream = file.OpenReadStream();
        var result = await _scheduleExporter.ImportFromStreamAsync(stream);

        if (!result.Success)
            return BadRequest(result);

        _torrentService.NotifyScheduleToggled(_settingsManager.Current.Schedule.Enabled);
        return Ok(result);
    }

    /// <summary>GET /api/v1/profiles/{name}/export — download a profile as .vtprofile.json.</summary>
    [HttpGet("{name}/export")]
    public async Task<IActionResult> ExportProfile(string name)
    {
        var profiles = await _profileManager.LoadAllAsync();
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (profile == null)
            return NotFound(new { error = $"Profile '{name}' not found." });

        var tempPath = Path.GetTempFileName();
        try
        {
            await _profileManager.ExportAsync(profile, tempPath);
            var bytes = await System.IO.File.ReadAllBytesAsync(tempPath);
            return File(bytes, "application/json", $"{name}.vtprofile.json");
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }

    /// <summary>POST /api/v1/profiles/import — upload a .vtprofile.json.</summary>
    [HttpPost("import")]
    public async Task<IActionResult> ImportProfile(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided." });

        var tempPath = Path.GetTempFileName();
        try
        {
            using (var stream = file.OpenReadStream())
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await stream.CopyToAsync(fs);
            }

            var result = await _profileManager.ImportAsync(tempPath);

            if (result.Profile == null)
                return BadRequest(new { warnings = result.Warnings });

            if (result.HasNameConflict)
                result.Profile.Name += " (imported)";

            await _profileManager.SaveAsync(result.Profile);

            return Ok(new
            {
                name = result.Profile.Name,
                color = result.Profile.Color,
                scope = result.Profile.Scope,
                warnings = result.Warnings,
                hadNameConflict = result.HasNameConflict
            });
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
                System.IO.File.Delete(tempPath);
        }
    }
}

public record ActivateProfileRequest(string Name);
public record ToggleScheduleRequest(bool Enabled);
