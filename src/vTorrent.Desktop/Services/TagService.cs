using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Storage;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Service for tag CRUD operations, decoupled from ViewModel concerns.
/// Extracted from SidebarViewModel as part of god class decomposition (Phase 5, Task 5.6).
/// </summary>
public class TagService
{
    private readonly ITorrentManagerService _torrentManager;

    /// <summary>Raised when tags change (from any source — WebUI, Desktop, etc.).</summary>
    public event Action? TagsUpdated;

    public TagService(ITorrentManagerService torrentManager)
    {
        _torrentManager = torrentManager ?? throw new ArgumentNullException(nameof(torrentManager));
    }

    /// <summary>
    /// Subscribe to core ITorrentService events so that changes from any source
    /// (Desktop UI, WebUI, etc.) propagate to this service's consumers.
    /// </summary>
    public void SubscribeToCoreEvents()
    {
        _torrentManager.Service.TagChanged += OnTagChanged;
    }

    private void OnTagChanged(object? sender, int tagId)
    {
        TagsUpdated?.Invoke();
    }

    /// <summary>
    /// Load all tags with their torrent counts.
    /// </summary>
    public async Task<List<TagInfo>> LoadAllWithCountsAsync()
    {
        var tags = await _torrentManager.Service.GetAllTagsAsync();
        var result = new List<TagInfo>();

        foreach (var tag in tags)
        {
            var count = await _torrentManager.Service.GetTorrentCountByTagAsync(tag.Id);
            result.Add(new TagInfo(tag.Id, tag.Name, tag.Color, count));
        }

        return result;
    }

    /// <summary>
    /// Create a new tag.
    /// </summary>
    public async Task<TagInfo> CreateAsync(string name)
    {
        var tag = await _torrentManager.Service.CreateTagAsync(name);
        return new TagInfo(tag.Id, tag.Name, tag.Color, 0);
    }

    /// <summary>
    /// Update an existing tag.
    /// </summary>
    public async Task UpdateAsync(int tagId, string name, string? color)
    {
        await _torrentManager.Service.UpdateTagAsync(tagId, name, color);
    }

    /// <summary>
    /// Delete a tag.
    /// </summary>
    public async Task DeleteAsync(int tagId)
    {
        await _torrentManager.Service.DeleteTagAsync(tagId);
    }
}

/// <summary>
/// Immutable tag data transfer object.
/// </summary>
public record TagInfo(int Id, string Name, string? Color, int TorrentCount);
