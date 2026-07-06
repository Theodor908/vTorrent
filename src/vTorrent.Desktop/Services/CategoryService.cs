using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using vTorrent.Storage;

namespace vTorrent.Desktop.Services;

/// <summary>
/// Service for category CRUD operations, decoupled from ViewModel concerns.
/// Extracted from SidebarViewModel as part of god class decomposition (Phase 5, Task 5.6).
/// </summary>
public class CategoryService
{
    private readonly ITorrentManagerService _torrentManager;

    /// <summary>Raised when categories change (from any source — WebUI, Desktop, etc.).</summary>
    public event Action? CategoriesUpdated;

    public CategoryService(ITorrentManagerService torrentManager)
    {
        _torrentManager = torrentManager ?? throw new ArgumentNullException(nameof(torrentManager));
    }

    /// <summary>
    /// Subscribe to core ITorrentService events so that changes from any source
    /// (Desktop UI, WebUI, etc.) propagate to this service's consumers.
    /// </summary>
    public void SubscribeToCoreEvents()
    {
        _torrentManager.Service.CategoryChanged += OnCategoryChanged;
    }

    private void OnCategoryChanged(object? sender, int categoryId)
    {
        CategoriesUpdated?.Invoke();
    }

    /// <summary>
    /// Load all categories with their torrent counts.
    /// </summary>
    public async Task<List<CategoryInfo>> LoadAllWithCountsAsync()
    {
        var categories = await _torrentManager.Service.GetAllCategoriesAsync();
        var result = new List<CategoryInfo>();

        foreach (var category in categories)
        {
            var count = await _torrentManager.Service.GetTorrentCountByCategoryAsync(category.Id);
            result.Add(new CategoryInfo(category.Id, category.Name, category.Color, category.SavePath, count));
        }

        return result;
    }

    /// <summary>
    /// Create a new category.
    /// </summary>
    public async Task<CategoryInfo> CreateAsync(string name)
    {
        var category = await _torrentManager.Service.CreateCategoryAsync(name);
        return new CategoryInfo(category.Id, category.Name, category.Color, category.SavePath, 0);
    }

    /// <summary>
    /// Update an existing category.
    /// </summary>
    public async Task UpdateAsync(int categoryId, string name, string? savePath, string? color)
    {
        await _torrentManager.Service.UpdateCategoryAsync(categoryId, name, color, savePath);
    }

    /// <summary>
    /// Delete a category.
    /// </summary>
    public async Task DeleteAsync(int categoryId)
    {
        await _torrentManager.Service.DeleteCategoryAsync(categoryId);
    }
}

/// <summary>
/// Immutable category data transfer object.
/// </summary>
public record CategoryInfo(int Id, string Name, string? Color, string? SavePath, int TorrentCount);
