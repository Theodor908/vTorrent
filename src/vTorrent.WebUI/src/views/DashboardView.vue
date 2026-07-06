<script setup lang="ts">
import { computed } from 'vue';
import { useSessionStore } from '@/stores/sessionStore';
import { formatBytes } from '@/utils/format';
import TransferSpeedCard from '@/components/dashboard/TransferSpeedCard.vue';
import SessionCard from '@/components/dashboard/SessionCard.vue';
import StatTile from '@/components/dashboard/StatTile.vue';
import TorrentTable from '@/components/torrents/TorrentTable.vue';

const sessionStore = useSessionStore();

// ── Stat tile data ──────────────────────────────────────────

const s = computed(() => sessionStore.stats);

const torrentsTotal = computed(() => String(s.value?.totalTorrents ?? 0));
const torrentItems = computed(() => [
  { label: 'Downloading', value: s.value?.downloadingTorrents ?? 0, color: 'var(--status-green)' },
  { label: 'Seeding', value: s.value?.seedingTorrents ?? 0, color: 'var(--accent-active)' },
  { label: 'Paused', value: s.value?.pausedTorrents ?? 0 },
  { label: 'Error', value: s.value?.errorTorrents ?? 0, color: 'var(--status-red)' },
  { label: 'Checking', value: s.value?.checkingTorrents ?? 0 },
  { label: 'Completed', value: s.value?.totalTorrents != null ? (s.value.totalTorrents - s.value.downloadingTorrents - s.value.checkingTorrents - s.value.errorTorrents) : 0, color: 'var(--status-green)' },
]);

// Peers tile — only show fields the server actually populates
const peersConnected = computed(() => String(s.value?.totalPeersConnected ?? 0));
const peerItems = computed(() => [
  { label: 'Seeds', value: s.value?.totalConnectedSeeds ?? 0 },
]);

// DHT tile — reads from dhtStatus (SignalR DhtStateChanged), NOT from stats
// The stats.dhtNodes field is never populated by the engine aggregation loop.
const dht = computed(() => sessionStore.dhtStatus);
const dhtNodes = computed(() => String(dht.value?.nodeCount ?? 0));
const dhtStatus = computed(() => dht.value?.isRunning ? 'Running' : 'Stopped');
const dhtItems = computed(() => [
  { label: 'Status', value: dhtStatus.value },
]);
</script>

<template>
  <div class="dashboard">
    <!-- Top row: Transfer Speed + Session -->
    <section class="dashboard__top" aria-label="Transfer and session statistics">
      <div class="dashboard__speed-card">
        <TransferSpeedCard />
      </div>
      <SessionCard />
    </section>

    <!-- Stat tiles row -->
    <section class="dashboard__tiles" aria-label="Quick statistics">
      <StatTile title="TORRENTS" :headline="torrentsTotal" headline-label="total" :items="torrentItems" />
      <StatTile title="PEERS" :headline="peersConnected" headline-label="connected" :items="peerItems" />
      <StatTile title="DHT" :headline="dhtNodes" headline-label="nodes" :items="dhtItems" />
    </section>

    <!-- Torrent table -->
    <section class="dashboard__table" aria-label="Torrent list">
      <TorrentTable />
    </section>

  </div>
</template>

<style scoped>
.dashboard {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
  min-height: 0;
}

.dashboard__top {
  display: grid;
  grid-template-columns: 7fr 3fr;
  gap: var(--spacing-lg);
}

.dashboard__tiles {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: var(--spacing-lg);
}

.dashboard__table {
  flex: 1;
  min-height: 200px;
  overflow: hidden;
}

/* Responsive: stack on tablet */
@media (max-width: 1279px) {
  .dashboard__top {
    grid-template-columns: 1fr;
  }
  .dashboard__tiles {
    grid-template-columns: repeat(2, 1fr);
  }
}

/* Responsive: single column on mobile — hide speed chart, stack everything */
@media (max-width: 767px) {
  .dashboard {
    gap: var(--spacing-sm);
  }
  .dashboard__speed-card {
    display: none;
  }
  .dashboard__tiles {
    grid-template-columns: 1fr;
  }
}
</style>
