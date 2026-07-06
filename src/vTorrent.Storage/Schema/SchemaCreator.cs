using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace vTorrent.Storage.Schema;

/// <summary>
/// Creates the initial database schema (all tables and indexes).
/// </summary>
internal static class SchemaCreator
{
    public static async Task CreateSchemaAsync(SqliteConnection connection, ILogger logger)
    {
        const string schema = @"
            -- Schema version tracking
            CREATE TABLE schema_version (
                version INTEGER PRIMARY KEY,
                applied_at INTEGER NOT NULL
            );

            -- Categories table
            CREATE TABLE categories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                color TEXT,
                save_path TEXT,
                sort_order INTEGER DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            -- Tags table
            CREATE TABLE tags (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                color TEXT,
                sort_order INTEGER DEFAULT 0,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            -- Core torrent data
            CREATE TABLE torrents (
                info_hash TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                comment TEXT,
                created_by TEXT,

                total_size INTEGER NOT NULL,
                piece_count INTEGER NOT NULL,
                piece_size INTEGER NOT NULL,
                file_count INTEGER NOT NULL DEFAULT 1,
                is_private INTEGER NOT NULL DEFAULT 0,

                save_path TEXT NOT NULL,
                torrent_file_path TEXT,

                state TEXT NOT NULL DEFAULT 'paused',
                error_message TEXT,
                progress REAL NOT NULL DEFAULT 0,
                is_finished INTEGER NOT NULL DEFAULT 0,
                is_seed INTEGER NOT NULL DEFAULT 0,

                total_uploaded INTEGER NOT NULL DEFAULT 0,
                total_downloaded INTEGER NOT NULL DEFAULT 0,
                total_payload_uploaded INTEGER NOT NULL DEFAULT 0,
                total_payload_downloaded INTEGER NOT NULL DEFAULT 0,
                total_failed_bytes INTEGER NOT NULL DEFAULT 0,
                total_redundant_bytes INTEGER NOT NULL DEFAULT 0,

                active_seconds INTEGER NOT NULL DEFAULT 0,
                seeding_seconds INTEGER NOT NULL DEFAULT 0,
                finished_seconds INTEGER NOT NULL DEFAULT 0,

                added_at INTEGER NOT NULL,
                started_at INTEGER,
                completed_at INTEGER,
                last_seen_complete INTEGER,
                last_upload INTEGER,
                last_download INTEGER,
                last_active_at INTEGER,

                max_connections INTEGER DEFAULT -1,
                max_uploads INTEGER DEFAULT -1,
                download_limit INTEGER DEFAULT -1,
                upload_limit INTEGER DEFAULT -1,
                sequential_download INTEGER DEFAULT 0,
                first_last_piece_priority INTEGER DEFAULT 0,
                file_priorities TEXT DEFAULT NULL,
                auto_managed INTEGER DEFAULT 1,

                queue_position INTEGER DEFAULT 0,
                category_id INTEGER REFERENCES categories(id) ON DELETE SET NULL,

                is_magnet_link INTEGER NOT NULL DEFAULT 0,
                magnet_uri TEXT,

                info_hash_v2 TEXT,
                torrent_version INTEGER NOT NULL DEFAULT 1,

                transfer_phase TEXT,
                file_operation TEXT,
                user_intent TEXT,
                health TEXT,

                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );

            -- Junction table for torrent-tag many-to-many
            CREATE TABLE torrent_tags (
                info_hash TEXT NOT NULL,
                tag_id INTEGER NOT NULL,
                PRIMARY KEY (info_hash, tag_id),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE,
                FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE CASCADE
            );

            -- Tracker URLs
            CREATE TABLE trackers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                info_hash TEXT NOT NULL,
                url TEXT NOT NULL,
                tier INTEGER NOT NULL DEFAULT 0,

                status TEXT DEFAULT 'idle',
                message TEXT,

                last_announce INTEGER,
                next_announce INTEGER,
                min_announce_interval INTEGER DEFAULT 1800,
                announce_interval INTEGER DEFAULT 1800,

                last_scrape INTEGER,
                seeders INTEGER,
                leechers INTEGER,
                downloaded INTEGER,

                UNIQUE(info_hash, url),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            -- Web seeds (BEP 17/19)
            CREATE TABLE web_seeds (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                info_hash TEXT NOT NULL,
                url TEXT NOT NULL,
                type TEXT NOT NULL DEFAULT 'BEP19',
                added_at INTEGER NOT NULL,

                UNIQUE(info_hash, url),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            -- File information
            CREATE TABLE files (
                info_hash TEXT NOT NULL,
                file_index INTEGER NOT NULL,
                path TEXT NOT NULL,
                size INTEGER NOT NULL,
                priority INTEGER NOT NULL DEFAULT 4,
                progress REAL DEFAULT 0,

                PRIMARY KEY (info_hash, file_index),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            -- Known peers (includes V3 columns: last_connected, trust_points, total_uploaded, total_downloaded)
            CREATE TABLE known_peers (
                info_hash TEXT NOT NULL,
                ip TEXT NOT NULL,
                port INTEGER NOT NULL,
                source TEXT DEFAULT 'tracker',
                last_seen INTEGER,
                failed_count INTEGER DEFAULT 0,
                last_connected INTEGER,
                trust_points INTEGER DEFAULT 0,
                total_uploaded INTEGER DEFAULT 0,
                total_downloaded INTEGER DEFAULT 0,

                PRIMARY KEY (info_hash, ip, port),
                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            -- Banned peers
            CREATE TABLE banned_peers (
                ip TEXT PRIMARY KEY,
                reason TEXT,
                banned_at INTEGER NOT NULL
            );

            -- Statistics snapshots
            CREATE TABLE statistics_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                info_hash TEXT,
                timestamp INTEGER NOT NULL,
                download_rate INTEGER,
                upload_rate INTEGER,
                downloaded INTEGER,
                uploaded INTEGER,
                peers INTEGER,
                seeds INTEGER,

                FOREIGN KEY (info_hash) REFERENCES torrents(info_hash) ON DELETE CASCADE
            );

            -- DHT nodes (V3)
            CREATE TABLE dht_nodes (
                node_id TEXT NOT NULL,
                ip TEXT NOT NULL,
                port INTEGER NOT NULL,
                rtt_ms INTEGER DEFAULT 0,
                last_seen INTEGER NOT NULL,
                PRIMARY KEY (ip, port)
            );

            -- DHT state (V3)
            CREATE TABLE dht_state (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            -- Indexes
            CREATE INDEX idx_dht_nodes_lastseen ON dht_nodes(last_seen);
            CREATE INDEX idx_torrents_state ON torrents(state);
            CREATE INDEX idx_torrents_added ON torrents(added_at);
            CREATE INDEX idx_torrents_queue ON torrents(queue_position);
            CREATE INDEX idx_torrents_category ON torrents(category_id);
            CREATE INDEX idx_trackers_infohash ON trackers(info_hash);
            CREATE INDEX idx_web_seeds_infohash ON web_seeds(info_hash);
            CREATE INDEX idx_files_infohash ON files(info_hash);
            CREATE INDEX idx_peers_infohash ON known_peers(info_hash);
            CREATE INDEX idx_stats_timestamp ON statistics_history(timestamp);
            CREATE INDEX idx_stats_infohash ON statistics_history(info_hash);
            CREATE INDEX idx_torrent_tags_infohash ON torrent_tags(info_hash);
            CREATE INDEX idx_torrent_tags_tagid ON torrent_tags(tag_id);

            -- Refresh tokens for auth (vTorrent.Server)
            CREATE TABLE refresh_tokens (
                id TEXT PRIMARY KEY,
                created_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL,
                revoked_at INTEGER,
                replaced_by TEXT
            );

            CREATE INDEX ix_refresh_tokens_expires ON refresh_tokens(expires_at);
            CREATE INDEX ix_refresh_tokens_revoked ON refresh_tokens(revoked_at);

            -- API keys for authentication
            CREATE TABLE IF NOT EXISTS api_keys (
                key_hash   TEXT PRIMARY KEY,
                key_prefix TEXT NOT NULL,
                label      TEXT NOT NULL,
                created_at INTEGER NOT NULL,
                last_used  INTEGER,
                revoked_at INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_api_keys_revoked ON api_keys(revoked_at);
        ";

        await connection.ExecuteAsync(schema);
        logger.LogInformation("Database schema created");
    }
}
