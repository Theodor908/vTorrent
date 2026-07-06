using System.Collections.Generic;
using System.Threading.Tasks;
using vTorrent.Abstractions.Records;

namespace vTorrent.Abstractions.Interfaces.Storage;

public interface ICategoryRepository
{
    Task<List<Category>> GetAllCategoriesAsync();
    Task<Category?> GetCategoryAsync(int id);
    Task<Category?> GetCategoryByNameAsync(string name);
    Task<Category> CreateCategoryAsync(string name, string? color = null, string? savePath = null);
    Task UpdateCategoryAsync(int id, string name, string? color, string? savePath);
    Task DeleteCategoryAsync(int id);
    Task<int> GetTorrentCountByCategoryAsync(int categoryId);
    Task<List<TorrentRecord>> GetTorrentsByCategoryAsync(int categoryId);
    Task SetTorrentCategoryAsync(string infoHash, int? categoryId);
}
