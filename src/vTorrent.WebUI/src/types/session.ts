// session.ts — TypeScript types mirroring session-related C# DTOs from vTorrent.Abstractions
// All types match what the REST API returns as camelCase JSON.

// ============================================================
// SessionStatistics — session-wide statistics aggregated across all torrents.
// Maps to vTorrent.Abstractions.Models.SessionStatistics
// Note: computed properties (TotalTorrents, ActiveTorrents, DiskCacheHitRatio,
//       PiecePassRate, Uptime) are included because ASP.NET serializes them.
// ============================================================

export interface SessionStatistics {
  // Transfer Statistics
  totalBytesSent: number;
  totalBytesReceived: number;

  // Rate Statistics
  globalDownloadRate: number;
  globalUploadRate: number;

  // Torrent Counts
  downloadingTorrents: number;
  seedingTorrents: number;
  pausedTorrents: number;
  checkingTorrents: number;
  errorTorrents: number;
  uploadOnlyTorrents: number;

  // Computed torrent counts (serialized from C# computed properties)
  totalTorrents: number;
  activeTorrents: number;

  // Connection Statistics
  totalPeersConnected: number;
  totalConnectedSeeds: number;
  halfOpenConnections: number;
  uploadingPeers: number;
  downloadingPeers: number;
  unchokedPeers: number;
  connectionAttempts: number;
  connectionsRejected: number;

  // DHT Statistics
  dhtNodes: number;
  dhtNodeCache: number;
  dhtTorrents: number;
  dhtBytesSent: number;
  dhtBytesReceived: number;

  // Tracker Statistics
  trackerRequestsSent: number;
  trackerResponsesReceived: number;
  trackerErrors: number;

  // Disk Statistics
  diskReadQueue: number;
  diskWriteQueue: number;
  diskBytesRead: number;
  diskBytesWritten: number;
  diskReadCount: number;
  diskWriteCount: number;
  diskCacheSize: number;
  diskCacheHits: number;
  diskCacheMisses: number;

  // Computed disk stats
  diskCacheHitRatio: number;

  // Piece Statistics
  piecesPassed: number;
  piecesFailed: number;

  // Computed piece stats
  piecePassRate: number;

  // Session Info
  sessionStartTime: string; // ISO 8601
  /** Computed from sessionStartTime — serialized as TimeSpan string */
  uptime: string;
  isPaused: boolean;
  listenPort: number;
  externalIpAddress: string | null;
}

// ============================================================
// SessionOverview — immutable point-in-time snapshot of session-wide statistics.
// Maps to vTorrent.Abstractions.Models.SessionOverview
// This is the batch stats event carrier alongside changed torrent snapshots.
// ============================================================

export interface SessionOverview {
  // Rates (raw)
  globalDownloadRate: number;
  globalUploadRate: number;

  // Session totals
  sessionDownloaded: number;
  sessionUploaded: number;
  allTimeDownloaded: number;
  allTimeUploaded: number;

  // Torrent counts
  totalTorrents: number;
  activeDownloads: number;
  activeUploads: number;
  pausedTorrents: number;
  checkingTorrents: number;
  queuedTorrents: number;
  errorTorrents: number;

  // Connections
  connectedPeers: number;
  totalConnections: number;
  halfOpenConnections: number;

  // DHT
  dhtNodes: number;
  dhtEnabled: boolean;

  // Disk
  diskReadQueue: number;
  diskWriteQueue: number;
  diskBytesRead: number;
  diskBytesWritten: number;

  // Network
  listenPort: number;
  portOpen: boolean;
  externalIp: string | null;
  downloadLimit: number;
  uploadLimit: number;

  // Session state
  isPaused: boolean;
  /** TimeSpan serialized as string (e.g., "01:23:45") */
  uptime: string;
  freeSpace: number;
}

// ============================================================
// DhtStatus — DHT-specific status snapshot.
// Used by GET /api/v1/dht
// ============================================================

export interface DhtStatus {
  enabled: boolean;
  nodes: number;
  nodeCache: number;
  torrents: number;
  bytesSent: number;
  bytesReceived: number;
}

// ============================================================
// SessionCounts — lightweight torrent count summary.
// Used for sidebar/filter badges.
// ============================================================

export interface SessionCounts {
  total: number;
  downloading: number;
  seeding: number;
  paused: number;
  checking: number;
  error: number;
  queued: number;
  active: number;
}
