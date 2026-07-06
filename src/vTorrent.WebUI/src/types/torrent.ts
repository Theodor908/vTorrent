// torrent.ts — TypeScript types mirroring C# DTOs from vTorrent.Abstractions and vTorrent.Server
// All types match what the REST API returns as camelCase JSON.

// ============================================================
// Enums (as union types — do NOT use const enum)
// ============================================================

/**
 * TransferPhase — orthogonal dimension of TorrentStatus.
 * Maps to vTorrent.Abstractions.Enums.TransferPhase
 */
export type TransferPhase =
  | 'Idle'
  | 'Stopping'
  | 'Allocating'
  | 'CheckingResumeData'
  | 'CheckingFiles'
  | 'FetchingMetadata'
  | 'Connecting'
  | 'Downloading'
  | 'Seeding';

/**
 * FileOperation — orthogonal to transfer phase.
 * Maps to vTorrent.Abstractions.Enums.FileOperation
 */
export type FileOperation = 'None' | 'Moving' | 'Rechecking';

/**
 * UserIntent — what the user or auto-manager wants the torrent to do.
 * Maps to vTorrent.Abstractions.Enums.UserIntent
 */
export type UserIntent = 'Active' | 'Paused' | 'Queued';

/**
 * FilePriority — priority levels for selective file download.
 * Maps to vTorrent.Abstractions.Enums.FilePriority
 */
export type FilePriority = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;
// 0=Skip, 1=Lowest, 2=Low, 3=BelowNormal, 4=Normal, 5=AboveNormal, 6=High, 7=Highest

export const FilePriorityValues = {
  Skip: 0,
  Lowest: 1,
  Low: 2,
  BelowNormal: 3,
  Normal: 4,
  AboveNormal: 5,
  High: 6,
  Highest: 7,
} as const;

// ============================================================
// TorrentStatus — immutable snapshot of all torrent state dimensions
// Maps to vTorrent.Abstractions.Models.TorrentStatus
// ============================================================

export interface TorrentError {
  message: string;
  errorCode: string | null;
  filePath: string | null;
}

export interface TorrentStatus {
  phase: TransferPhase;
  fileOp: FileOperation;
  intent: UserIntent;
  error: TorrentError | null;
  missingFiles: boolean;
  isAutoManaged: boolean;
  isFinished: boolean;
  isSeed: boolean;
  // File-op progress (recheck/move) — owned by the state machine.
  // Live transfer metrics live on TorrentSnapshot, not here.
  fileOpProgress: number;
}

// ============================================================
// TorrentSnapshot — immutable point-in-time snapshot of all torrent data.
// Maps to vTorrent.Abstractions.Models.TorrentSnapshot
// ============================================================

export interface TorrentSnapshot {
  // Identity
  infoHash: string;
  infoHashV2: string | null;
  name: string;
  torrentVersionValue: number;

  // Orthogonal state
  status: TorrentStatus;

  // Progress
  totalSize: number;
  totalWanted: number;
  totalWantedDone: number;
  piecesCompleted: number;
  totalPieces: number;
  verifiedProgress: number;
  pendingPieces: number;

  // Rates (raw — formatting is consumer's job)
  payloadDownloadRate: number;
  payloadUploadRate: number;
  smoothedPayloadDownloadRate: number;
  totalDownloadRate: number;
  totalUploadRate: number;

  // Byte counters
  sessionPayloadDownloaded: number;
  sessionPayloadUploaded: number;
  totalUploaded: number;

  // Peers
  connectedPeers: number;
  connectedSeeds: number;
  totalPeers: number;
  totalSeeds: number;

  // Health & endgame
  availability: number;
  isEndgame: boolean;
  endgameWastedBytes: number;
  endgameDuplicateBlocks: number;
  isSeeding: boolean;
  isFinished: boolean;

  // Time (ISO 8601 strings)
  addedOn: string;
  completedOn: string | null;
  activeDuration: string;
  seedingDuration: string;

  // Storage & queue
  savePath: string;
  queuePosition: number;
  isForceResumed: boolean;

  // Category & tags
  categoryId: number | null;
  categoryName: string | null;
  tags: string[];

  // Error
  errorMessage: string | null;
}

// ============================================================
// Nested detail DTOs
// Maps to nested records in vTorrent.Abstractions.Models.ManagedTorrentView
// ============================================================

/** Tracker information snapshot. */
export interface TrackerInfoView {
  url: string;
  tier: number;
  status: string;
  peers: number;
  seeds: number;
  leeches: number;
  responseTime: string;
}

/** Connected peer snapshot. */
export interface PeerView {
  ipAddress: string;
  port: number;
  client: string;
  downloadRate: number;
  uploadRate: number;
  downloadRateFormatted: string;
  uploadRateFormatted: string;
  downloaded: number;
  uploaded: number;
  progress: number;
  flags: string;
  roundTripTimeMs: number;
}

/** File entry snapshot. */
export interface FileView {
  index: number;
  name: string;
  path: string;
  size: number;
  progress: number;
  priority: number;
  availability: number;
}

/** Web seed (HTTP source) snapshot. */
export interface WebSeedView {
  url: string;
  /** "BEP 19" or "BEP 17" */
  type: string;
  status: string;
  downloadRate: number;
  downloadRateFormatted: string;
  downloaded: number;
}

// ============================================================
// ManagedTorrentView — full detail DTO
// Maps to vTorrent.Abstractions.Models.ManagedTorrentView
// ============================================================

export interface ManagedTorrentView {
  // Identity
  infoHash: string;
  infoHashV2: string | null;
  name: string;

  // Metadata (null-safe for magnet links)
  creator: string | null;
  comment: string | null;
  creationDate: string | null;
  isPrivate: boolean;
  pieceSize: number;
  pieceCount: number;
  fileCount: number;
  totalSize: number;

  // State
  status: TorrentStatus;
  errorMessage: string | null;
  isFinished: boolean;
  isSeed: boolean;
  isAutoManaged: boolean;
  sequentialDownload: boolean;
  firstLastPiecePriority: boolean;

  // Progress / Rates
  progress: number;
  downloaded: number;
  uploaded: number;
  ratio: number;
  downloadRate: number;
  uploadRate: number;

  // Stats (detailed)
  piecesCompleted: number;
  totalPieces: number;
  availability: number;
  payloadDownloadRate: number;
  payloadUploadRate: number;
  smoothedPayloadDownloadRate: number;
  allTimeDownloaded: number;
  allTimeUploaded: number;
  bytesRemaining: number;
  totalWastedBytes: number;
  statsRatio: number;
  connectedSeeds: number;
  connectedPeers: number;
  trackerSeeders: number;
  trackerLeechers: number;
  /** BEP 33: Estimated seeds from DHT bloom filter scrape. Null if no scrape data. */
  dhtSeeds: number | null;
  /** BEP 33: Estimated peers from DHT bloom filter scrape. Null if no scrape data. */
  dhtPeers: number | null;
  activeDuration: string;
  seedingDuration: string;
  reannounceIn: string | null;
  lastSeenComplete: string | null;

  // Engine
  isEngineRunning: boolean;
  maxConnections: number;
  downloadBandwidthLimit: number;
  uploadBandwidthLimit: number;
  isDownloadLimited: boolean;
  isUploadLimited: boolean;

  // Time (ISO 8601 strings)
  addedTime: string;
  completedTime: string | null;
  lastActiveTime: string | null;

  // Storage
  savePath: string;
  queuePosition: number;

  // Category / Tags
  categoryId: number | null;
  categoryName: string | null;
  tags: string[];

  // Magnet Link
  isMagnetLink: boolean;
  hasMetadata: boolean;
  metadataProgress: number;

  // Nested detail lists
  trackers: TrackerInfoView[];
  peers: PeerView[];
  files: FileView[];
  webSeeds: WebSeedView[];
}

// ============================================================
// Request types
// ============================================================

/** Query parameters for GET /api/v1/torrents */
export interface TorrentListParams {
  phase?: string;
  intent?: string;
  health?: string;
  category?: number;
  tag?: string;
  sort?: string;
  limit?: number;
  offset?: number;
}

/** Options sent with a .torrent file upload (JSON-serialized in the `options` form field). */
export interface AddTorrentOptions {
  savePath?: string | null;
  startImmediately?: boolean;
  sequentialDownload?: boolean;
  firstLastPiecePriority?: boolean;
  addToTopOfQueue?: boolean;
  filePriorities?: FilePriority[] | null;
}

/** POST /api/v1/torrents/magnet — body */
export interface AddMagnetRequest {
  magnetUri: string;
  savePath?: string | null;
  startImmediately?: boolean;
  sequentialDownload?: boolean;
  firstLastPiecePriority?: boolean;
  addToTopOfQueue?: boolean;
  filePriorities?: FilePriority[] | null;
}

/** DELETE /api/v1/torrents/{hash} — query params */
export interface DeleteTorrentParams {
  deleteFiles?: boolean;
  secureWipe?: boolean;
  wipeMetadata?: boolean;
}

/** POST /api/v1/torrents/{hash}/location — body */
export interface ChangeLocationRequest {
  savePath: string;
}

/** PUT /api/v1/torrents/{hash}/files/priorities — individual entry */
export interface FilePriorityEntry {
  fileIndex: number;
  priority: FilePriority;
}

/** PUT /api/v1/torrents/{hash}/files/priorities — body */
export interface SetFilePrioritiesRequest {
  priorities: FilePriorityEntry[];
}

/** PUT /api/v1/torrents/{hash}/category — body */
export interface SetCategoryRequest {
  categoryId: number | null;
}

/** PUT /api/v1/torrents/{hash}/tags — body */
export interface SetTagsRequest {
  tagIds: number[];
}

/** Piece state codes for GET /api/v1/torrents/{hash}/pieces */
export type PieceState = 0 | 1 | 2;
// 0 = not downloaded, 1 = downloading, 2 = complete

/** Response from POST /api/v1/torrents and POST /api/v1/torrents/magnet */
export interface AddTorrentResponse {
  infoHash: string;
}
