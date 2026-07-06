using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using vTorrent.Abstractions.Interfaces.Storage;

namespace vTorrent.Storage;

public static class StorageServiceRegistration
{
    public static IServiceCollection AddVTorrentStorage(this IServiceCollection services, string dataDirectory)
    {
        services.AddSingleton<ITorrentDatabase>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<TorrentDatabase>>();
            var dbPath = Path.Combine(dataDirectory, "vtorrent.db");
            return new TorrentDatabase(dbPath, logger);
        });
        return services;
    }
}
