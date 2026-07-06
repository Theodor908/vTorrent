// settings.ts — TypeScript types mirroring C# settings DTOs from vTorrent.Abstractions.Settings
// All types match what the REST API returns as camelCase JSON.

// ============================================================
// Enums (as union types — do NOT use const enum)
// ============================================================

/**
 * ChokingAlgorithm — download unchoking strategy.
 * Maps to vTorrent.Abstractions.Settings.Enums.ChokingAlgorithm
 */
export type ChokingAlgorithm = 'FixedSlots' | 'RateBased' | 'BitTyrant' | 'Adaptive';

/**
 * SeedChokingAlgorithm — seed unchoking strategy.
 * Maps to vTorrent.Abstractions.Settings.Enums.SeedChokingAlgorithm
 */
export type SeedChokingAlgorithm = 'FastestUpload' | 'RoundRobin' | 'AntiLeech';

/**
 * MixedModeAlgorithm — TCP/uTP bandwidth sharing strategy.
 * Maps to vTorrent.Abstractions.Settings.Enums.MixedModeAlgorithm
 */
export type MixedModeAlgorithm = 'PreferTcp' | 'PeerProportional' | 'PreferUtp';

/**
 * DiskBackendType — disk I/O backend selection strategy.
 * Maps to vTorrent.Abstractions.Settings.DiskBackendType
 */
export type DiskBackendType = 'Auto' | 'ForcePosix' | 'ForceMmap';

/**
 * DiskIoMode — OS cache behavior for disk I/O operations.
 * Maps to vTorrent.Abstractions.Settings.DiskIoMode
 */
export type DiskIoMode = 'EnableOsCache' | 'WriteThrough' | 'DisableOsCache';

/**
 * EncryptionPolicy — encryption policy for peer connections.
 * Maps to vTorrent.Abstractions.Settings.Enums.EncryptionPolicy
 */
export type EncryptionPolicy = 'Forced' | 'Enabled' | 'Disabled';

/**
 * EncryptionLevel — allowed encryption level (MSE/PE).
 * Maps to vTorrent.Abstractions.Settings.Enums.EncryptionLevel
 */
export type EncryptionLevel = 'Plaintext' | 'RC4' | 'Both';

/**
 * TorrentPriority — download priority for a torrent.
 * Maps to vTorrent.Abstractions.Settings.TorrentPriority
 */
export type TorrentPriority = 'Low' | 'Normal' | 'High';

/**
 * ProxyType — proxy protocol type.
 * Maps to vTorrent.Abstractions.Settings.ProxyType
 */
export type ProxyType = 'None' | 'Socks4' | 'Socks5' | 'Socks5Password' | 'Http' | 'HttpPassword';

/**
 * I2pDestinationMode — I2P SAM destination rotation strategy.
 * Maps to vTorrent.Abstractions.Settings.I2pDestinationMode
 */
export type I2pDestinationMode = 'Persistent' | 'Rotating' | 'SessionTransient';

// ============================================================
// ConnectionSettings
// Maps to vTorrent.Abstractions.Settings.ConnectionSettings
// ============================================================

export interface ConnectionSettings {
  maxGlobalConnections: number;
  maxConnectionsPerTorrent: number;
  maxUploadsPerTorrent: number;
  maxHalfOpenConnections: number;
  listenPort: number;
  listenPortRange: number[];
  listenInterfaces: string[];
  enableUpnp: boolean;
  enableNatPmp: boolean;
  natPmpLeaseSeconds: number;
  upnpLeaseSeconds: number;
  upnpIgnoreNonRouters: boolean;
  ipFilterFilePath: string;
  outgoingInterface: string;
  allowMultipleConnectionsPerIp: boolean;
  noConnectPrivilegedPorts: boolean;
  smoothConnects: boolean;
  announcePort: number;
  allowIdna: boolean;
  lsdAnnounceInterval: number;
}

// ============================================================
// BandwidthSettings
// Maps to vTorrent.Abstractions.Settings.BandwidthSettings
// ============================================================

export interface BandwidthSettings {
  globalDownloadLimit: number;
  globalUploadLimit: number;
  perTorrentDownloadLimit: number;
  perTorrentUploadLimit: number;
  rateLimitIpOverhead: boolean;
  mixedModeAlgorithm: MixedModeAlgorithm;
}

// ============================================================
// EncryptionSettings
// Maps to vTorrent.Abstractions.Settings.EncryptionSettings
// ============================================================

export interface EncryptionSettings {
  outPolicy: EncryptionPolicy;
  inPolicy: EncryptionPolicy;
  allowedLevel: EncryptionLevel;
}

// ============================================================
// ProtocolSettings
// Maps to vTorrent.Abstractions.Settings.ProtocolSettings
// ============================================================

export interface ProtocolSettings {
  enableDht: boolean;
  enableLsd: boolean;
  enablePex: boolean;
  dhtBootstrapNodes: string[];
  encryption: EncryptionSettings;
  userAgent: string;
  peerIdPrefix: string;
  enableHolepunch: boolean;
}

// ============================================================
// DhtSettings
// Maps to vTorrent.Abstractions.Settings.DhtSettings
// ============================================================

export interface DhtSettings {
  enabled: boolean;
  port: number;
  searchBranching: number;
  queryTimeoutMs: number;
  maxPeersReply: number;
  maxPeersPerInfoHash: number;
  bootstrapNodes: string[];
  enforceNodeId: boolean;
  restrictRoutingIps: boolean;
  extendedRoutingTable: boolean;
  announceIntervalMs: number;
  enableDosBlocker: boolean;
  readOnly: boolean;
  maxSampleCount: number;
  sampleInfohashesIntervalSeconds: number;
  maxFailCount: number;
  maxInfoHashes: number;
  maxTotalPeers: number;
  blockTimeoutSeconds: number;
  uploadRateLimitBytesPerSec: number;
  blockRateLimitPacketsPerSec: number;
  maxBlockedIps: number;
  preferVerifiedNodeIds: boolean;
}

// ============================================================
// DiskSettings
// Maps to vTorrent.Abstractions.Settings.DiskSettings
// ============================================================

export interface DiskSettings {
  cacheSize: number;
  defaultSavePath: string;
  incompleteSavePath: string;
  preallocateFiles: boolean;
  hashThreads: number;
  maxOutstandingDiskRequests: number;
  noRecheckIncompleteResume: boolean;
  pieceExtentAffinity: boolean;
  pieceExtentSize: number;
  backendType: DiskBackendType;
  readMode: DiskIoMode;
  writeMode: DiskIoMode;
  mmapFileSizeCutoff: number;
  mmapMemoryCeiling: number;
  closeFileInterval: number;
  maxQueuedDiskBytes: number;
  diskSpaceWarningBytes: number;
  diskSpaceCriticalBytes: number;
  optimisticDiskRetry: number;
  maxDiskRetries: number;
  checkingMemUsage: number;
}

// ============================================================
// QueueSettings
// Maps to vTorrent.Abstractions.Settings.QueueSettings
// Note: InactiveDownRate serializes as "SlowTorrentDownloadThreshold"
//       InactiveUpRate serializes as "SlowTorrentUploadThreshold" (JsonPropertyName)
// ============================================================

export interface QueueSettings {
  maxActiveDownloads: number;
  maxActiveSeeds: number;
  maxActiveTorrents: number;
  /** Serialized as "SlowTorrentDownloadThreshold" in JSON */
  slowTorrentDownloadThreshold: number;
  /** Serialized as "SlowTorrentUploadThreshold" in JSON */
  slowTorrentUploadThreshold: number;
  dontCountSlowTorrents: boolean;
  autoManageInterval: number;
  autoManageStartup: number;
  connectSeedEveryNDownload: number;
}

// ============================================================
// BehaviorSettings
// Maps to vTorrent.Abstractions.Settings.BehaviorSettings
// ============================================================

export interface BehaviorSettings {
  closeRedundantConnections: boolean;
  autoSequentialInSeederSwarm: boolean;
  prioritizePartialPieces: boolean;
  strictEndgameMode: boolean;
  seedRatioLimit: number;
  seedTimeLimit: number;
  removeOnSeedComplete: boolean;
  pauseOnSeedComplete: boolean;
  metadataDownloadTimeoutMinutes: number;
  sendRedundantHave: boolean;
  useParoleMode: boolean;
  seedingOutgoingConnections: boolean;
  reportRedundantBytes: boolean;
  reportTrueDownloaded: boolean;
  chokingAlgorithm: ChokingAlgorithm;
  seedChokingAlgorithm: SeedChokingAlgorithm;
  peerTurnover: number;
  peerTurnoverCutoff: number;
  peerTurnoverInterval: number;
  autoSequentialRatio: number;
}

// ============================================================
// TrackerSettings
// Maps to vTorrent.Abstractions.Settings.TrackerSettings
// ============================================================

export interface TrackerSettings {
  announceToAllTrackers: boolean;
  announceToAllTiers: boolean;
  stopTrackerTimeout: number;
  numWant: number;
  httpTimeoutSeconds: number;
  udpTimeoutSeconds: number;
  maxRetries: number;
  retryDelaySeconds: number;
  minAnnounceInterval: number;
  autoScrapeInterval: number;
  maxConcurrentAnnounces: number;
  listenPort: number;
  userAgent: string;
  parallelAnnounceAcrossTiers: boolean;
  maxParallelAnnounces: number;
  reportRedundantBytes: boolean;
  reportTrueDownloaded: boolean;
  preferUdpTrackers: boolean;
  announceCryptoSupport: boolean;
  applyIpFilterToTrackers: boolean;
  announceIp: string;
  validateHttpsTrackers: boolean;
  ssrfMitigation: boolean;
  trackerBackoff: number;
  autoScrapeMinInterval: number;
}

// ============================================================
// PeerSettings
// Maps to vTorrent.Abstractions.Settings.PeerSettings
// ============================================================

export interface PeerSettings {
  maxConnections: number;
  maxUploadsPerTorrent: number;
  connectTimeout: number;
  handshakeTimeout: number;
  utpConnectTimeoutMs: number;
  listenPort: number;
  requestTimeout: number;
  pieceTimeout: number;
  inactivityTimeout: number;
  unchokeInterval: number;
  optimisticUnchokeInterval: number;
  maxPendingBlocksPerPeer: number;
  sendBufferWatermark: number;
  sendBufferLowWatermark: number;
  sendBufferWatermarkFactor: number;
  peerId: string;
  clientVersion: string;
  enablePex: boolean;
  prioritizePartialPieces: boolean;
  strictEndgameMode: boolean;
  closeRedundantConnections: boolean;
  seedingOutgoingConnections: boolean;
  numOptimisticUnchokeSlots: number;
  utpTargetDelay: number;
  utpGainFactor: number;
  utpMinTimeout: number;
  utpSynResends: number;
  utpFinResends: number;
  utpNumResends: number;
  utpLossMultiplier: number;
  utpCwndReduceTimer: number;
  diskCacheSize: number;
}

// ============================================================
// AutoSaveSettings
// Maps to vTorrent.Abstractions.Settings.AutoSaveSettings
// ============================================================

export interface AutoSaveSettings {
  enabled: boolean;
  intervalMinutes: number;
  saveOnTorrentComplete: boolean;
  saveOnPause: boolean;
  saveOnResume: boolean;
}

// ============================================================
// LoggingSettings
// Maps to vTorrent.Abstractions.Settings.LoggingSettings
// ============================================================

export interface LoggingSettings {
  level: string;
  logToFile: boolean;
  logFilePath: string;
  maxLogFileSize: number;
  maxLogFiles: number;
}

// ============================================================
// UISettings
// Maps to vTorrent.Abstractions.Settings.UISettings
// ============================================================

export interface UISettings {
  theme: string;
  notificationsEnabled: boolean;
  notifyOnDownloadComplete: boolean;
  notifyOnDownloadFailed: boolean;
  notifyOnTorrentAdded: boolean;
  playNotificationSound: boolean;
}

// ============================================================
// WebSeedSettings
// Maps to vTorrent.Abstractions.Models.WebSeedSettings (defined in GlobalSettings.cs)
// ============================================================

export interface WebSeedSettings {
  maxConnectionsPerTorrent: number;
  timeoutSeconds: number;
  waitRetrySeconds: number;
  maxRequestBytes: number;
  alwaysSendUserAgent: boolean;
}

// ============================================================
// PrivacySettings
// Maps to vTorrent.Abstractions.Models.PrivacySettings (defined in GlobalSettings.cs)
// ============================================================

export interface PrivacySettings {
  secureDeletion: boolean;
  secureDeletionIncludeMetadata: boolean;
  anonymousMode: boolean;
}

// ============================================================
// ProxySettings
// Maps to vTorrent.Abstractions.Settings.ProxySettings
// ============================================================

export interface ProxySettings {
  type: ProxyType;
  hostname: string;
  port: number;
  username: string;
  password: string;
  proxyPeerConnections: boolean;
  proxyTrackerConnections: boolean;
  proxyDht: boolean;
  proxyHostnames: boolean;
}

// ============================================================
// VpnSettings
// Maps to vTorrent.Abstractions.Settings.VpnSettings
// ============================================================

export interface VpnSettings {
  killSwitchEnabled: boolean;
  vpnInterfaceName: string;
}

// ============================================================
// I2pSettings
// Maps to vTorrent.Abstractions.Settings.I2pSettings
// ============================================================

export interface I2pSettings {
  enabled: boolean;
  samHostname: string;
  samPort: number;
  inboundTunnelQuantity: number;
  outboundTunnelQuantity: number;
  inboundTunnelLength: number;
  outboundTunnelLength: number;
  destinationMode: I2pDestinationMode;
  rotationIntervalDays: number;
  allowMixedMode: boolean;
  maxActiveI2pTorrents: number;
}

// ============================================================
// PeerClassDefinition / PeerClassSettings
// Maps to vTorrent.Abstractions.Settings.PeerClassSettings
// ============================================================

export interface PeerClassDefinition {
  name: string;
  uploadLimitBytesPerSec: number;
  downloadLimitBytesPerSec: number;
  ipRanges: string[];
}

export interface PeerClassSettings {
  enabled: boolean;
  classes: PeerClassDefinition[];
}

// ============================================================
// ServerSettings
// Maps to vTorrent.Abstractions.Settings.ServerSettings
// Note: sensitive fields (localPasswordHash, jwtSecret, httpsCertPassword,
//       oidcClientSecret) are redacted by SettingsRedactor before sending to the client.
// ============================================================

export interface ServerSettings {
  enabled: boolean;
  listenPort: number;
  listenAddress: string;
  enableHttps: boolean;
  httpsCertPath: string;
  /** Always redacted — server sends empty string */
  httpsCertPassword: string;
  localUsername: string;
  /** Always redacted — server sends empty string */
  localPasswordHash: string;
  /** When true, localhost requests bypass JWT auth. */
  allowLocalAccess: boolean;
  /** Always redacted — server sends empty string */
  jwtSecret: string;
  jwtAccessTokenLifetimeMinutes: number;
  jwtRefreshTokenLifetimeDays: number;
  oidcAuthority: string;
  oidcClientId: string;
  /** Always redacted — server sends empty string */
  oidcClientSecret: string;
  oidcAllowedEmail: string;
  allowedOrigins: string;
  /** Open default browser when server starts. Desktop-only setting. */
  openBrowserOnServerStart: boolean;
  /** Custom WebUI bundle folder path. Empty = use built-in. */
  webUIBundlePath: string;

  // ── Advanced Security ──
  enableCsrfProtection: boolean;
  enableHostHeaderValidation: boolean;
  allowedHostnames: string;
  enableClickjackingProtection: boolean;
  enableReverseProxySupport: boolean;
  trustedProxies: string;
  apiKeysEnabled: boolean;
  maxAuthFailCount: number;
  authBanDurationSeconds: number;
  enableSubnetAuthBypass: boolean;
  authBypassSubnets: string;
  enableSecureCookie: boolean;
  verboseSecurityErrors: boolean;
  enableSecurityHeaders: boolean;
}

// ============================================================
// GlobalSettings — root settings object
// Maps to vTorrent.Abstractions.Settings.GlobalSettings
// ============================================================

export interface GlobalSettings {
  version: number;
  updatedOn: string; // ISO 8601
  connection: ConnectionSettings;
  bandwidth: BandwidthSettings;
  protocol: ProtocolSettings;
  dht: DhtSettings;
  disk: DiskSettings;
  queue: QueueSettings;
  behavior: BehaviorSettings;
  tracker: TrackerSettings;
  peer: PeerSettings;
  autoSave: AutoSaveSettings;
  logging: LoggingSettings;
  encryption: EncryptionSettings;
  ui: UISettings;
  webSeed: WebSeedSettings;
  privacy: PrivacySettings;
  proxy: ProxySettings;
  vpn: VpnSettings;
  i2p: I2pSettings;
  peerClasses: PeerClassSettings;
  server: ServerSettings;
}

// ============================================================
// SeedingLimits — per-torrent seeding limits
// Maps to vTorrent.Abstractions.Settings.SeedingLimits
// ============================================================

export interface SeedingLimits {
  ratioLimit: number | null;
  timeLimitMinutes: number | null;
  stopWhenComplete: boolean | null;
  pauseWhenComplete: boolean | null;
}

// ============================================================
// TorrentSettings — per-torrent settings that override global defaults
// Maps to vTorrent.Abstractions.Settings.TorrentSettings
// Values of -1 or null mean "use global setting"
// ============================================================

export interface TorrentSettings {
  infoHash: string;
  updatedOn: string; // ISO 8601

  // Connection overrides (-1 = use global)
  maxConnections: number;
  maxUploads: number;

  // Bandwidth overrides (-1 = use global, 0 = unlimited)
  uploadLimit: number;
  downloadLimit: number;

  // Download options
  sequentialDownload: boolean;
  firstLastPiecePriority: boolean;
  autoManaged: boolean;
  priority: TorrentPriority;

  // Seeding options
  superSeeding: boolean;
  seeding: SeedingLimits;

  // Additional configuration
  customTrackers: string[] | null;
  category: string | null;
  tags: string[] | null;
  savePath: string | null;

  // Medium-effort setting overrides (null = use global)
  chokingAlgorithm: ChokingAlgorithm | null;
  seedChokingAlgorithm: SeedChokingAlgorithm | null;
  numOptimisticUnchokeSlots: number;
  mixedModeAlgorithm: MixedModeAlgorithm | null;
  peerTurnover: number;
  peerTurnoverCutoff: number;
  peerTurnoverInterval: number;
  pieceExtentAffinity: boolean | null;
  pieceExtentSize: number;
  diskBackend: DiskBackendType | null;
  diskWriteMode: DiskIoMode | null;
}

// ============================================================
// UpdateSettingsRequest — body for PUT /api/v1/session/settings
// This is a partial GlobalSettings — any top-level section can be omitted.
// ============================================================

export type UpdateSettingsRequest = Partial<GlobalSettings>;
