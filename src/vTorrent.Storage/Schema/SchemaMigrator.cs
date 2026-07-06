using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace vTorrent.Storage.Schema;

/// <summary>
/// Runs incremental database migrations from one schema version to another.
/// </summary>
internal static class SchemaMigrator
{
    public static async Task MigrateAsync(SqliteConnection connection, ILogger logger,
        int fromVersion, int toVersion)
    {
        for (int v = fromVersion + 1; v <= toVersion; v++)
        {
            await RunMigrationAsync(connection, logger, v);
            await InsertSchemaVersionAsync(connection, v);
        }
    }

    private static async Task InsertSchemaVersionAsync(SqliteConnection connection, int version)
    {
        await connection.ExecuteAsync(
            "INSERT INTO schema_version (version, applied_at) VALUES (@version, @appliedAt)",
            new { version, appliedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
    }

    private static async Task RunMigrationAsync(SqliteConnection connection, ILogger logger, int version)
    {
        switch (version)
        {
            case 1:
                // Initial schema - no migration needed
                break;

            case 2:
                await RunMigrationV2Async(connection, logger);
                break;

            case 3:
                await RunMigrationV3Async(connection, logger);
                break;

            case 4:
                await RunMigrationV4Async(connection, logger);
                break;

            case 5:
                await RunMigrationV5Async(connection, logger);
                break;

            case 6:
                await RunMigrationV6Async(connection, logger);
                break;

            case 7:
                await RunMigrationV7Async(connection, logger);
                break;

            case 8:
                await RunMigrationV8Async(connection, logger);
                break;

            case 9:
                await RunMigrationV9Async(connection, logger);
                break;

            case 10:
                await RunMigrationV10Async(connection, logger);
                break;

            default:
                throw new InvalidOperationException($"Unknown migration version: {version}");
        }
    }

    private static async Task RunMigrationV2Async(SqliteConnection connection, ILogger logger)
    {
        const string migration = @"
            -- Categories table
            CREATE TABLE IF NOT EXISTS categories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                color TEXT,
                save_path TEXT,
                sort_order INTEGER DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            -- Tags table
            CREATE TABLE IF NOT EXISTS tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                color TEXT,
                sort_order INTEGER DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            -- Junction table for torrent-tag many-to-many
            CREATE TABLE IF NOT EXISTS torrent_tags (
                info_hash TEXT NOT NULL,
                tag_id INTEGER NOT NULL,
                PRIMARY KEY (info_hash, tag_id),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE,
                FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );

            -- Add category_id to torrents table
            ALTER TABLE torrents ADD COLUMN category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL;

            -- Indexes
            CREATE INDEX IF NOT EXISTS idx_torrent_tags_infohash ON torrent_tags(info_hash);
            CREATE INDEX IF NOT EXISTS idx_torrent_tags_tagid ON torrent_tags(tag_id);
            CREATE INDEX IF NOT EXISTS idx_torrents_category ON torrents(category_id);
        ";

        await connection.ExecuteAsync(migration);
        logger.LogInformation("Applied migration v2: Categories and Tags support");
    }

    private static async Task RunMigrationV3Async(SqliteConnection connection, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE known_peers ADD COLUMN last_connected INTEGER;",
            "ALTER TABLE known_peers ADD COLUMN trust_points INTEGER DEFAULT 0;",
            "ALTER TABLE known_peers ADD COLUMN total_uploaded INTEGER DEFAULT 0;",
            "ALTER TABLE known_peers ADD COLUMN total_downloaded INTEGER DEFAULT 0;"
        };

        foreach (var alter in alterStatements)
        {
            try { await connection.ExecuteAsync(alter); }
            catch (SqliteException) { /* Column may already exist */ }
        }

        const string newTables = @"
            CREATE TABLE IF NOT EXISTS dht_nodes (
                node_id TEXT NOT NULL,
                ip TEXT NOT NULL,
                port INTEGER NOT NULL,
                rtt_ms INTEGER DEFAULT 0,
                last_seen INTEGER NOT NULL,
                PRIMARY KEY (ip, port)
            );

            CREATE TABLE IF NOT EXISTS dht_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_dht_nodes_lastseen ON dht_nodes(last_seen);
        ";

        await connection.ExecuteAsync(newTables);
        logger.LogInformation("Applied migration v3: Persistence consolidation (known_peers upgrade + DHT tables)");
    }

    private static async Task RunMigrationV4Async(SqliteConnection connection, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE torrents ADD COLUMN first_last_piece_priority INTEGER DEFAULT 0;",
            "ALTER TABLE torrents ADD COLUMN file_priorities TEXT DEFAULT NULL;"
        };

        foreach (var alter in alterStatements)
        {
            try { await connection.ExecuteAsync(alter); }
            catch (SqliteException) { /* Column may already exist */ }
        }

        logger.LogInformation("Applied migration v4: first/last piece priority and file priorities columns");
    }

    private static async Task RunMigrationV5Async(SqliteConnection connection, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE torrents ADD COLUMN info_hash_v2 TEXT;",
            "ALTER TABLE torrents ADD COLUMN torrent_version INTEGER NOT NULL DEFAULT 1;"
        };

        foreach (var alter in alterStatements)
        {
            try { await connection.ExecuteAsync(alter); }
            catch (SqliteException) { /* Column may already exist */ }
        }

        logger.LogInformation("Applied migration v5: BEP 52 info_hash_v2 and torrent_version columns");
    }

    private static async Task RunMigrationV6Async(SqliteConnection connection, ILogger logger)
    {
        var alterStatements = new[]
        {
            "ALTER TABLE torrents ADD COLUMN transfer_phase TEXT;",
            "ALTER TABLE torrents ADD COLUMN file_operation TEXT;",
            "ALTER TABLE torrents ADD COLUMN user_intent TEXT;",
            "ALTER TABLE torrents ADD COLUMN health TEXT;"
        };

        foreach (var alter in alterStatements)
        {
            try { await connection.ExecuteAsync(alter); }
            catch (SqliteException) { /* Column may already exist */ }
        }

        logger.LogInformation("Applied migration v6: orthogonal state dimension columns");
    }

    private static async Task RunMigrationV7Async(SqliteConnection connection, ILogger logger)
    {
        const string migration = @"
            CREATE TABLE IF NOT EXISTS trusted_certificates (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint TEXT NOT NULL UNIQUE,
                label TEXT NOT NULL,
                certificate_data BLOB NOT NULL,
                signer_name TEXT,
                added_date TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_trusted_certs_fingerprint ON trusted_certificates(fingerprint);
            CREATE INDEX IF NOT EXISTS idx_trusted_certs_signer ON trusted_certificates(signer_name);
        ";

        await connection.ExecuteAsync(migration);
        logger.LogInformation("Applied migration v7: BEP 35 trusted_certificates table");
    }

    private static async Task RunMigrationV8Async(SqliteConnection connection, ILogger logger)
    {
        const string migration = @"
            CREATE TABLE IF NOT EXISTS refresh_tokens (
                id TEXT PRIMARY KEY,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                revoked_at INTEGER,
                replaced_by TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_refresh_tokens_expires ON refresh_tokens(expires_at);
            CREATE INDEX IF NOT EXISTS ix_refresh_tokens_revoked ON refresh_tokens(revoked_at);
        ";
        await connection.ExecuteAsync(migration);
        logger.LogInformation("Migration v8: Added refresh_tokens table for auth system");
    }

    private static async Task RunMigrationV9Async(SqliteConnection connection, ILogger logger)
    {
        const string populateIntent = @"
            UPDATE torrents SET user_intent = CASE
                WHEN state IN ('paused', 'stopped') THEN 'Paused'
                WHEN state = 'queued' THEN 'Queued'
                ELSE 'Active'
            END
            WHERE user_intent IS NULL OR user_intent = '';
        ";
        await connection.ExecuteAsync(populateIntent);

        const string createIndex = @"
            CREATE INDEX IF NOT EXISTS idx_torrents_user_intent ON torrents(user_intent);
        ";
        await connection.ExecuteAsync(createIndex);

        const string dropOldIndex = @"
            DROP INDEX IF EXISTS idx_torrents_state;
        ";
        await connection.ExecuteAsync(dropOldIndex);

        logger.LogInformation("Migration v9: Populated user_intent from legacy state, created index, dropped state index");
    }

    private static async Task RunMigrationV10Async(SqliteConnection connection, ILogger logger)
    {
        const string migration = @"
            CREATE TABLE IF NOT EXISTS web_seeds (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                info_hash TEXT NOT NULL,
                url TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'BEP19',
                added_at INTEGER NOT NULL,

                UNIQUE(info_hash, url),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_web_seeds_infohash ON web_seeds(info_hash);
        ";

        await connection.ExecuteAsync(migration);
        logger.LogInformation("Applied migration v10: web_seeds table for runtime web seed persistence");
    }
}
