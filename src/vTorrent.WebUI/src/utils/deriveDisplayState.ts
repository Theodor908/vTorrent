// deriveDisplayState.ts — Pure function: derives UI display state from orthogonal TorrentStatus.
// Priority cascade mirrors Desktop's DisplayStateDeriver.cs exactly.

import type { TorrentStatus } from '../types/torrent';

/**
 * Display states for the WebUI — derived from orthogonal TorrentStatus dimensions.
 * Matches Desktop's TorrentDisplayState enum 1:1.
 */
export type DisplayState =
  | 'Downloading'
  | 'Seeding'
  | 'Paused'
  | 'Queued'
  | 'Error'
  | 'Stalled'
  | 'Allocating'
  | 'Verifying'
  | 'Checking'
  | 'Moving'
  | 'MetadataDownloading'
  | 'ForcedDownloading'
  | 'ForcedSeeding'
  | 'Stopping'
  | 'CheckingResumeData'
  | 'StalledSeeding'
  | 'MissingFiles'
  | 'Connecting'
  | 'Stopped';

/**
 * Derive a flat display state from orthogonal TorrentStatus + live transfer metrics.
 * Priority: Error > Intent > FileOp > Phase > Transfer+Stall > Fallback.
 *
 * Live metrics (downloadRate / uploadRate / connectedPeers) are passed explicitly
 * because they are NOT state-machine state — they live on TorrentSnapshot, sampled
 * each tick from the engine. Mirrors the C# DisplayStateDeriver.Derive signature.
 */
export function deriveDisplayState(
  status: TorrentStatus,
  downloadRate: number,
  uploadRate: number,
  connectedPeers: number,
): DisplayState {
  // Priority 1 — Error states (computed from error/missingFiles fields)
  if (status.error != null) return 'Error';
  if (status.missingFiles) return 'MissingFiles';

  // Priority 2 — User intent
  if (status.intent === 'Paused') return 'Paused';
  if (status.intent === 'Queued') return 'Queued';

  // Priority 3 — File operations (overlay on non-transfer phases)
  if (status.fileOp === 'Moving'
      && status.phase !== 'Downloading' && status.phase !== 'Seeding')
    return 'Moving';
  if (status.fileOp === 'Rechecking') return 'Checking';

  // Priority 4 — Transitional phases
  switch (status.phase) {
    case 'Stopping': return 'Stopping';
    case 'Allocating': return 'Allocating';
    case 'CheckingResumeData': return 'CheckingResumeData';
    case 'CheckingFiles': return 'Verifying';
    case 'FetchingMetadata': return 'MetadataDownloading';
    case 'Connecting': return 'Connecting';
  }

  // Priority 5 — Transfer activity + stall (uses live metrics)
  if (status.phase === 'Downloading') {
    if (downloadRate === 0 && connectedPeers === 0) return 'Stalled';
    if (!status.isAutoManaged) return 'ForcedDownloading';
    return 'Downloading';
  }

  if (status.phase === 'Seeding') {
    if (uploadRate === 0 && connectedPeers === 0) return 'StalledSeeding';
    if (!status.isAutoManaged) return 'ForcedSeeding';
    return 'Seeding';
  }

  // Fallback
  return 'Stopped';
}

/** Badge color CSS class for a given display state. */
export function displayStateBadgeClass(state: DisplayState): string {
  switch (state) {
    case 'Downloading':
    case 'ForcedDownloading':
    case 'MetadataDownloading':
    case 'Connecting':
      return 'badge--green';
    case 'Seeding':
    case 'ForcedSeeding':
      return 'badge--blue';
    case 'Paused':
    case 'Stopped':
    case 'Stopping':
    case 'Queued':
      return 'badge--gray';
    case 'Error':
    case 'MissingFiles':
      return 'badge--red';
    case 'Stalled':
    case 'StalledSeeding':
      return 'badge--orange';
    case 'Allocating':
    case 'Verifying':
    case 'Checking':
    case 'CheckingResumeData':
    case 'Moving':
      return 'badge--orange';
    default:
      return 'badge--gray';
  }
}

/** Human-readable label for a display state. */
export function displayStateLabel(state: DisplayState): string {
  switch (state) {
    case 'Downloading': return 'Downloading';
    case 'ForcedDownloading': return '[F] Downloading';
    case 'Seeding': return 'Seeding';
    case 'ForcedSeeding': return '[F] Seeding';
    case 'Paused': return 'Paused';
    case 'Queued': return 'Queued';
    case 'Error': return 'Error';
    case 'MissingFiles': return 'Missing Files';
    case 'Stalled': return 'Stalled';
    case 'StalledSeeding': return 'Seeding (Stalled)';
    case 'Allocating': return 'Allocating';
    case 'Verifying': return 'Verifying';
    case 'Checking': return 'Checking';
    case 'CheckingResumeData': return 'Checking Resume Data';
    case 'Moving': return 'Moving';
    case 'MetadataDownloading': return 'Fetching Metadata';
    case 'Connecting': return 'Connecting';
    case 'Stopping': return 'Stopping';
    case 'Stopped': return 'Stopped';
  }
}
