// useSignalR.ts — SignalR hub connection management with adaptive throttling.
//
// Design notes:
//   - Connects to /hub/torrent with automatic reconnect.
//   - Dispatches incoming events to stores (torrentStore, sessionStore).
//   - Applies Page Visibility API throttling:
//       Focused    → process every event immediately
//       Background → buffer updates, apply latest every 5 seconds
//       Hidden >5min → discard updates; re-sync via REST on focus return
//   - TorrentDetailUpdated is NOT stored — emitted via callback for the
//     active detail panel component to consume directly.
//   - Module-level singleton state ensures all callers share one connection.

import { ref } from 'vue';
import {
  HubConnectionBuilder,
  HubConnection,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { useTorrentStore } from '../stores/torrentStore';
import { useSessionStore } from '../stores/sessionStore';
import { useProfileStore } from '@/stores/profileStore';
import type { TorrentSnapshot } from '../types/torrent';
import type { SessionStatistics } from '../types/session';
import type { DhtStatusSnapshot } from '../stores/sessionStore';
import { ACTIVE_PROFILE_KEY, PROFILES_STORAGE_KEY } from '@/types/connection';

// ============================================================
// Types
// ============================================================

export type SignalRStatus = 'connected' | 'reconnecting' | 'disconnected';

type TorrentDetailCallback = (detail: unknown) => void;

// ============================================================
// Hub URL Builder
// ============================================================

/**
 * getHubUrl — builds the SignalR hub URL dynamically based on the active server profile.
 * Reads from localStorage to support remote server connections.
 * Falls back to '/hub/torrent' for the local profile or on any error.
 */
function getHubUrl(): string {
  try {
    const activeId = localStorage.getItem(ACTIVE_PROFILE_KEY) ?? 'local';
    if (activeId === 'local') return '/hub/torrent';
    const raw = localStorage.getItem(PROFILES_STORAGE_KEY);
    const profiles = raw ? JSON.parse(raw) : [];
    const profile = profiles.find((p: any) => p.id === activeId);
    if (!profile || !profile.host) return '/hub/torrent';
    const scheme = profile.https ? 'https' : 'http';
    return `${scheme}://${profile.host}/hub/torrent`;
  } catch {
    return '/hub/torrent';
  }
}

// ============================================================
// Module-level singleton state (shared across all useSignalR() calls)
// ============================================================

let _connection: HubConnection | null = null;
const _status = ref<SignalRStatus>('disconnected');

let _detailCallback: TorrentDetailCallback | null = null;

/** Buffer for latest TorrentsUpdated batch while page is backgrounded. */
let _bufferedSnapshots: TorrentSnapshot[] | null = null;

/** Timer that drains the buffer every 5 s while the page is backgrounded. */
let _bufferDrainTimer: ReturnType<typeof setInterval> | null = null;

/** Timestamp of when the page was last hidden. */
let _hiddenAt: number | null = null;

/** Active torrent detail subscription tracking. */
let _subscribedDetailHash: string | null = null;

// ============================================================
// Composable
// ============================================================

export function useSignalR() {
  const torrentStore = useTorrentStore();
  const sessionStore = useSessionStore();
  const profileStore = useProfileStore();

  // ----------------------------------------------------------
  // Detail update callback (component-level, not stored)
  // ----------------------------------------------------------

  function onTorrentDetailUpdated(callback: TorrentDetailCallback): void {
    _detailCallback = callback;
  }

  // ----------------------------------------------------------
  // Adaptive throttling (Page Visibility API)
  // ----------------------------------------------------------

  /** Threshold: if hidden longer than 5 min, discard updates and re-sync on return. */
  const HIDDEN_STALE_MS = 5 * 60 * 1000;

  /** How often to flush the background buffer (ms). */
  const BACKGROUND_DRAIN_INTERVAL_MS = 5_000;

  function isPageFocused(): boolean {
    return document.visibilityState === 'visible';
  }

  function isPageStale(): boolean {
    if (_hiddenAt === null) return false;
    return Date.now() - _hiddenAt > HIDDEN_STALE_MS;
  }

  function startBufferDrain(): void {
    if (_bufferDrainTimer !== null) return;
    _bufferDrainTimer = setInterval(() => {
      if (_bufferedSnapshots !== null) {
        torrentStore.updateTorrents(_bufferedSnapshots);
        _bufferedSnapshots = null;
      }
    }, BACKGROUND_DRAIN_INTERVAL_MS);
  }

  function stopBufferDrain(): void {
    if (_bufferDrainTimer !== null) {
      clearInterval(_bufferDrainTimer);
      _bufferDrainTimer = null;
    }
  }

  function flushBuffer(): void {
    if (_bufferedSnapshots !== null) {
      torrentStore.updateTorrents(_bufferedSnapshots);
      _bufferedSnapshots = null;
    }
  }

  /** Called when the page becomes visible again after being backgrounded. */
  async function handleVisibilityChange(): Promise<void> {
    if (document.visibilityState === 'visible') {
      stopBufferDrain();
      if (isPageStale()) {
        // Re-sync via REST — discard stale buffered data
        _bufferedSnapshots = null;
        await torrentStore.loadInitialState();
        // Re-subscribe to active torrent detail if one is selected
        if (torrentStore.selectedHash && _connection?.state === HubConnectionState.Connected) {
          await subscribeTorrent(torrentStore.selectedHash);
        }
      } else {
        flushBuffer();
      }
      _hiddenAt = null;
    } else {
      // Page went to background
      _hiddenAt = Date.now();
      startBufferDrain();
    }
  }

  // ----------------------------------------------------------
  // Event handlers (registered after connection is established)
  // ----------------------------------------------------------

  function registerHandlers(conn: HubConnection): void {
    // Batch torrent updates — the most frequent event
    conn.on('TorrentsUpdated', (snapshots: TorrentSnapshot[]) => {
      if (isPageFocused()) {
        torrentStore.updateTorrents(snapshots);
      } else {
        // Buffer — keep only the latest batch
        _bufferedSnapshots = snapshots;
      }
    });

    // Session statistics
    conn.on('StatsUpdated', (incoming: SessionStatistics) => {
      sessionStore.updateStats(incoming);
    });

    // Single torrent added — payload is a full snapshot
    conn.on('TorrentAdded', (snapshot: TorrentSnapshot) => {
      torrentStore.updateTorrent(snapshot);
    });

    // Torrent removed — payload is the info hash string
    conn.on('TorrentRemoved', (hash: string) => {
      torrentStore.removeTorrent(hash);
    });

    // Torrent completed — show a toast notification
    conn.on('TorrentCompleted', (hash: string) => {
      const snapshot = torrentStore.torrents.get(hash);
      const name = snapshot?.name ?? hash;
      // Dispatch a browser notification if permitted; components can also listen
      if (typeof window !== 'undefined' && 'Notification' in window) {
        if (Notification.permission === 'granted') {
          new Notification('Download complete', { body: name, icon: '/favicon.ico' });
        }
      }
      // Also update the snapshot if it is present (server may push a completed snapshot)
      if (snapshot) {
        torrentStore.updateTorrent(snapshot);
      }
    });

    // State change on a single torrent — server sends { infoHash, oldState, newState }.
    // This is a lightweight signal; the next TorrentsUpdated batch delivers the full
    // orthogonal TorrentStatus. We ignore this event since we derive display state
    // from status dimensions, not a flat state string.
    conn.on('TorrentStateChanged', (_event: { infoHash: string; oldState: string; newState: string }) => {
      // No-op: display state is derived from snapshot.status via deriveDisplayState().
      // The next periodic TorrentsUpdated batch will deliver the full snapshot.
    });

    // Torrent-level error — server sends { infoHash, errorMessage }, not a full snapshot.
    // Update error field immediately for fast UI feedback.
    conn.on('TorrentError', (event: { infoHash: string; errorMessage: string }) => {
      const existing = torrentStore.torrents.get(event.infoHash);
      if (existing) {
        torrentStore.updateTorrent({
          ...existing,
          errorMessage: event.errorMessage,
          status: { ...existing.status, error: { message: event.errorMessage, errorCode: null, filePath: null } },
        });
      }
      // Browser notification
      if (typeof window !== 'undefined' && 'Notification' in window) {
        if (Notification.permission === 'granted') {
          const name = existing?.name ?? event.infoHash;
          new Notification(`Error: ${name}`, { body: event.errorMessage, icon: '/favicon.ico' });
        }
      }
    });

    // DHT state change — server sends { isRunning, nodeCount } (no isEnabled field).
    conn.on('DhtStateChanged', (event: { isRunning: boolean; nodeCount: number }) => {
      sessionStore.updateDhtStatus({
        isRunning: event.isRunning,
        isEnabled: event.isRunning,
        nodeCount: event.nodeCount,
      });
    });

    // Torrent detail update — forwarded to the registered component callback only
    conn.on('TorrentDetailUpdated', (detail: unknown) => {
      if (_detailCallback) {
        _detailCallback(detail);
      }
    });

    // Category/tag list changed — no payload; re-fetch via REST
    conn.on('CategoriesChanged', () => {
      torrentStore.refreshCategories();
    });

    conn.on('TagsChanged', () => {
      torrentStore.refreshTags();
    });

    conn.on('ProfileChanged', () => {
      profileStore.refreshActiveState();
    });

    conn.on('ScheduleToggled', () => {
      profileStore.refreshActiveState();
    });
  }

  // ----------------------------------------------------------
  // Active torrent detail subscription
  // ----------------------------------------------------------

  async function subscribeTorrent(hash: string): Promise<void> {
    if (!_connection || _connection.state !== HubConnectionState.Connected) return;
    try {
      await _connection.invoke('SubscribeTorrent', hash);
      _subscribedDetailHash = hash;
    } catch (err) {
      console.warn('[SignalR] SubscribeTorrent failed:', err);
    }
  }

  async function unsubscribeTorrent(hash: string): Promise<void> {
    if (!_connection || _connection.state !== HubConnectionState.Connected) return;
    try {
      await _connection.invoke('UnsubscribeTorrent', hash);
      if (_subscribedDetailHash === hash) {
        _subscribedDetailHash = null;
      }
    } catch (err) {
      console.warn('[SignalR] UnsubscribeTorrent failed:', err);
    }
  }

  // ----------------------------------------------------------
  // Connect / Disconnect
  // ----------------------------------------------------------

  /**
   * connect — builds and starts the HubConnection.
   * @param tokenFactory - function that returns the current JWT access token (or null if unauthenticated).
   */
  async function connect(tokenFactory: () => string | null | Promise<string | null>): Promise<void> {
    if (_connection && _connection.state !== HubConnectionState.Disconnected) {
      return; // Already connected or connecting
    }

    // accessTokenFactory must return string | Promise<string> (null not allowed by SignalR types)
    const wrappedFactory = async (): Promise<string> => {
      const token = await tokenFactory();
      return token ?? '';
    };

    _connection = new HubConnectionBuilder()
      .withUrl(getHubUrl(), {
        accessTokenFactory: wrappedFactory,
      })
      .withAutomaticReconnect([1000, 2000, 4000, 8000, 16000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    // Wire up lifecycle callbacks
    _connection.onreconnecting(() => {
      _status.value = 'reconnecting';
    });

    _connection.onreconnected(async () => {
      _status.value = 'connected';
      // Re-sync full state after reconnect — we may have missed events
      await torrentStore.loadInitialState();
      // Re-subscribe to active torrent detail if needed
      if (_subscribedDetailHash) {
        await subscribeTorrent(_subscribedDetailHash);
      }
    });

    _connection.onclose(() => {
      _status.value = 'disconnected';
    });

    registerHandlers(_connection);

    // Start the connection
    try {
      await _connection.start();
      _status.value = 'connected';
    } catch (err) {
      _status.value = 'disconnected';
      console.error('[SignalR] Failed to connect:', err);
      throw err;
    }

    // Register Page Visibility listener
    document.addEventListener('visibilitychange', handleVisibilityChange);
  }

  /** disconnect — gracefully stops the hub connection. */
  async function disconnect(): Promise<void> {
    document.removeEventListener('visibilitychange', handleVisibilityChange);
    stopBufferDrain();
    _bufferedSnapshots = null;
    _subscribedDetailHash = null;

    if (_connection) {
      await _connection.stop();
      _connection = null;
    }
    _status.value = 'disconnected';
  }

  return {
    status: _status,
    connect,
    disconnect,
    subscribeTorrent,
    unsubscribeTorrent,
    onTorrentDetailUpdated,
  };
}
