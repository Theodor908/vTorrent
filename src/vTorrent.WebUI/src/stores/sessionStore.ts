// sessionStore.ts — Session-wide statistics and DHT status store
// Updated by SignalR events (StatsUpdated, DhtStateChanged) and REST on init.

import { defineStore } from 'pinia';
import { ref } from 'vue';
import * as sessionApi from '../api/session';
import type { SessionStatistics } from '../types/session';

// ============================================================
// DHT status shape (derived from SignalR DhtStateChanged event)
// ============================================================

export interface DhtStatusSnapshot {
  isRunning: boolean;
  isEnabled: boolean;
  nodeCount: number;
}

// ============================================================
// Store
// ============================================================

export const useSessionStore = defineStore('session', () => {
  // ----------------------------------------------------------
  // State
  // ----------------------------------------------------------

  /** Latest session-wide statistics. Null until first fetch/event. */
  const stats = ref<SessionStatistics | null>(null);

  /** Latest DHT status. Null until first event or REST fetch. */
  const dhtStatus = ref<DhtStatusSnapshot | null>(null);

  // ----------------------------------------------------------
  // Actions
  // ----------------------------------------------------------

  /** updateStats — replace stats with fresh snapshot (from SignalR StatsUpdated). */
  function updateStats(incoming: SessionStatistics): void {
    stats.value = incoming;
  }

  /** updateDhtStatus — replace DHT status (from SignalR DhtStateChanged). */
  function updateDhtStatus(incoming: DhtStatusSnapshot): void {
    dhtStatus.value = incoming;
  }

  /**
   * loadInitialState — fetches session stats via REST.
   * DHT status is populated once the first DhtStateChanged event arrives.
   */
  async function loadInitialState(): Promise<void> {
    const sessionStats = await sessionApi.getSessionStats();
    stats.value = sessionStats;
  }

  return {
    stats,
    dhtStatus,
    updateStats,
    updateDhtStatus,
    loadInitialState,
  };
});
