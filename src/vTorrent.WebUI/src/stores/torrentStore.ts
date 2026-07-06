// torrentStore.ts — Torrent list state store
// Source of truth for all torrent snapshots. Handles filtering, sorting,
// column visibility persistence, and initial state hydration.

import { defineStore } from 'pinia';
import { ref, computed, reactive } from 'vue';
import * as torrentsApi from '../api/torrents';
import { getCategories } from '../api/categories';
import { getTags } from '../api/tags';
import type { Category } from '../api/categories';
import type { Tag } from '../api/tags';
import type { TorrentSnapshot } from '../types/torrent';
import { deriveDisplayState } from '../utils/deriveDisplayState';
import type { DisplayState } from '../utils/deriveDisplayState';

// ============================================================
// Constants
// ============================================================

const COLUMN_VISIBILITY_KEY = 'vtorrent-columns';

/** Default column visibility — 6 columns visible out of the full set. */
const DEFAULT_COLUMN_VISIBILITY: Record<string, boolean> = {
  Name: true,
  Progress: true,
  Size: true,
  ETA: true,
  Seeds: true,
  Peers: true,
  // All others default to false
  Status: false,
  DownloadSpeed: false,
  UploadSpeed: false,
  Ratio: false,
  Downloaded: false,
  Uploaded: false,
  AddedOn: false,
  Category: false,
  Tags: false,
  SavePath: false,
};

// ============================================================
// Helper — sort comparator
// ============================================================

function compareTorrents(
  a: TorrentSnapshot,
  b: TorrentSnapshot,
  column: string,
  direction: 'asc' | 'desc',
): number {
  let valA: string | number;
  let valB: string | number;

  switch (column) {
    case 'Name':
      valA = a.name.toLowerCase();
      valB = b.name.toLowerCase();
      break;
    case 'Progress':
      valA = a.verifiedProgress;
      valB = b.verifiedProgress;
      break;
    case 'Size':
      valA = a.totalSize;
      valB = b.totalSize;
      break;
    case 'ETA': {
      // Sort by download rate as a proxy for ETA (higher rate = lower ETA)
      valA = a.payloadDownloadRate;
      valB = b.payloadDownloadRate;
      // Invert: higher rate means shorter ETA, so we flip for ascending
      const diff = valA - valB;
      return direction === 'asc' ? -diff : diff;
    }
    case 'Seeds':
      valA = a.connectedSeeds;
      valB = b.connectedSeeds;
      break;
    case 'Peers':
      valA = a.connectedPeers;
      valB = b.connectedPeers;
      break;
    case 'DownloadSpeed':
      valA = a.payloadDownloadRate;
      valB = b.payloadDownloadRate;
      break;
    case 'UploadSpeed':
      valA = a.payloadUploadRate;
      valB = b.payloadUploadRate;
      break;
    case 'Downloaded':
      valA = a.sessionPayloadDownloaded;
      valB = b.sessionPayloadDownloaded;
      break;
    case 'Uploaded':
      valA = a.totalUploaded;
      valB = b.totalUploaded;
      break;
    case 'AddedOn':
      valA = a.addedOn;
      valB = b.addedOn;
      break;
    default:
      valA = a.name.toLowerCase();
      valB = b.name.toLowerCase();
  }

  if (valA < valB) return direction === 'asc' ? -1 : 1;
  if (valA > valB) return direction === 'asc' ? 1 : -1;
  return 0;
}

// ============================================================
// Store
// ============================================================

export const useTorrentStore = defineStore('torrents', () => {
  // ----------------------------------------------------------
  // State
  // ----------------------------------------------------------

  /**
   * Source-of-truth map from info hash → snapshot.
   * Vue 3 tracks Map reactivity when wrapped in reactive().
   */
  const torrents = reactive(new Map<string, TorrentSnapshot>());

  /** Info hash of the single-selected torrent (detail panel). */
  const selectedHash = ref<string | null>(null);

  /** Info hashes of all currently selected torrents (multi-select). */
  const selectedHashes = reactive(new Set<string>());

  /** Substring filter applied to torrent names. */
  const searchQuery = ref('');

  /** Filter by display state category. Null = all. */
  const statusFilter = ref<DisplayState | 'Completed' | null>(null);

  /** Filter by category name. Null = all. */
  const categoryFilter = ref<string | null>(null);

  /** Filter by tag name. Null = all. */
  const tagFilter = ref<string | null>(null);

  /** Full list of categories fetched from the REST API. */
  const categories = ref<Category[]>([]);

  /** Full list of tags fetched from the REST API. */
  const tags = ref<Tag[]>([]);

  /** Column used for sorting. */
  const sortColumn = ref('Name');

  /** Sort direction. */
  const sortDirection = ref<'asc' | 'desc'>('asc');

  /**
   * Per-column visibility flags.
   * Loaded from localStorage on store init; persisted on every change.
   */
  const columnVisibility = ref<Record<string, boolean>>({ ...DEFAULT_COLUMN_VISIBILITY });

  // ----------------------------------------------------------
  // Computed
  // ----------------------------------------------------------

  /**
   * filteredTorrents — applies search + status + category + tag filters in order,
   * then sorts by the current column/direction. Returns a stable array.
   */
  const filteredTorrents = computed((): TorrentSnapshot[] => {
    const query = searchQuery.value.toLowerCase().trim();
    const status = statusFilter.value;
    const category = categoryFilter.value;
    const tag = tagFilter.value;
    const col = sortColumn.value;
    const dir = sortDirection.value;

    const result: TorrentSnapshot[] = [];

    for (const snapshot of torrents.values()) {
      // Search filter
      if (query && !snapshot.name.toLowerCase().includes(query)) continue;

      // Status filter — grouped categories match statusCounts behavior
      if (status) {
        const display = deriveDisplayState(
          snapshot.status,
          snapshot.payloadDownloadRate,
          snapshot.payloadUploadRate,
          snapshot.connectedPeers,
        );
        if (status === 'Completed') {
          if (!snapshot.isFinished) continue;
        } else if (status === 'Downloading') {
          if (display !== 'Downloading' && display !== 'ForcedDownloading' && display !== 'MetadataDownloading' && display !== 'Connecting') continue;
        } else if (status === 'Seeding') {
          if (display !== 'Seeding' && display !== 'ForcedSeeding' && display !== 'StalledSeeding') continue;
        } else if (status === 'Paused') {
          if (display !== 'Paused' && display !== 'Stopped' && display !== 'Stopping' && display !== 'Queued') continue;
        } else if (status === 'Error') {
          if (display !== 'Error' && display !== 'MissingFiles') continue;
        } else if (display !== status) {
          continue;
        }
      }

      // Category filter
      if (category && snapshot.categoryName !== category) continue;

      // Tag filter
      if (tag && !snapshot.tags.includes(tag)) continue;

      result.push(snapshot);
    }

    result.sort((a, b) => compareTorrents(a, b, col, dir));
    return result;
  });

  /**
   * statusCounts — counts by well-known UI category derived from deriveDisplayState.
   * Used by the sidebar filter badges.
   */
  const statusCounts = computed(() => {
    let downloading = 0;
    let seeding = 0;
    let paused = 0;
    let errored = 0;
    let completed = 0;

    for (const snapshot of torrents.values()) {
      const display = deriveDisplayState(
        snapshot.status,
        snapshot.payloadDownloadRate,
        snapshot.payloadUploadRate,
        snapshot.connectedPeers,
      );
      switch (display) {
        case 'Downloading':
        case 'ForcedDownloading':
        case 'MetadataDownloading':
        case 'Connecting':
          downloading++;
          break;
        case 'Seeding':
        case 'ForcedSeeding':
        case 'StalledSeeding':
          seeding++;
          break;
        case 'Paused':
        case 'Stopped':
        case 'Stopping':
        case 'Queued':
          paused++;
          break;
        case 'Error':
        case 'MissingFiles':
          errored++;
          break;
      }
      if (snapshot.isFinished) completed++;
    }

    return { downloading, seeding, paused, errored, completed };
  });

  // ----------------------------------------------------------
  // Actions
  // ----------------------------------------------------------

  /** updateTorrent — insert or replace a single snapshot. */
  function updateTorrent(snapshot: TorrentSnapshot): void {
    torrents.set(snapshot.infoHash, snapshot);
  }

  /** updateTorrents — batch update; each snapshot is upserted by hash. */
  function updateTorrents(snapshots: TorrentSnapshot[]): void {
    for (const snapshot of snapshots) {
      torrents.set(snapshot.infoHash, snapshot);
    }
  }

  /** removeTorrent — remove by info hash. Also clears selection if needed. */
  function removeTorrent(hash: string): void {
    torrents.delete(hash);
    selectedHashes.delete(hash);
    if (selectedHash.value === hash) {
      selectedHash.value = null;
    }
  }

  /**
   * loadInitialState — fetches the full torrent list via REST and populates the map.
   * Called once at app startup before SignalR is connected.
   */
  async function loadInitialState(): Promise<void> {
    const snapshots = await torrentsApi.getTorrents();
    torrents.clear();
    for (const snapshot of snapshots) {
      torrents.set(snapshot.infoHash, snapshot);
    }
  }

  /** refreshCategories — re-fetches the full category list from the REST API. */
  async function refreshCategories(): Promise<void> {
    categories.value = await getCategories();
  }

  /** refreshTags — re-fetches the full tag list from the REST API. */
  async function refreshTags(): Promise<void> {
    tags.value = await getTags();
  }

  /** toggleColumn — flip a column's visibility and persist. */
  function toggleColumn(name: string): void {
    columnVisibility.value[name] = !columnVisibility.value[name];
    saveColumnVisibility();
  }

  /** loadColumnVisibility — restore from localStorage; fills missing keys with defaults. */
  function loadColumnVisibility(): void {
    const raw = localStorage.getItem(COLUMN_VISIBILITY_KEY);
    if (!raw) return;
    try {
      const stored = JSON.parse(raw) as Record<string, boolean>;
      // Merge stored values over defaults so new columns get their default
      columnVisibility.value = { ...DEFAULT_COLUMN_VISIBILITY, ...stored };
    } catch {
      // Corrupted storage — keep defaults
    }
  }

  /** saveColumnVisibility — persist current visibility map to localStorage. */
  function saveColumnVisibility(): void {
    localStorage.setItem(COLUMN_VISIBILITY_KEY, JSON.stringify(columnVisibility.value));
  }

  return {
    // State (expose maps/sets for template binding)
    torrents,
    selectedHash,
    selectedHashes,
    searchQuery,
    statusFilter,
    categoryFilter,
    tagFilter,
    categories,
    tags,
    sortColumn,
    sortDirection,
    columnVisibility,
    // Computed
    filteredTorrents,
    statusCounts,
    // Actions
    updateTorrent,
    updateTorrents,
    removeTorrent,
    loadInitialState,
    refreshCategories,
    refreshTags,
    toggleColumn,
    loadColumnVisibility,
    saveColumnVisibility,
  };
});
