<script setup lang="ts">
import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { PhArrowLeft } from '@phosphor-icons/vue';
import { getTorrentDetails } from '@/api/torrents';
import { useSignalR } from '@/composables/useSignalR';
import { useTorrentStore } from '@/stores/torrentStore';
import type { ManagedTorrentView } from '@/types/torrent';
import { formatBytes, formatSpeed, formatDuration, formatPercent } from '@/utils/format';
import SpeedChart from '@/components/common/SpeedChart.vue';
import ProgressBar from '@/components/torrents/ProgressBar.vue';

const route = useRoute();
const router = useRouter();
const signalR = useSignalR();
const torrentStore = useTorrentStore();

const hash = computed(() => route.params.hash as string);
const details = ref<ManagedTorrentView | null>(null);
const loading = ref(true);
const error = ref<string | null>(null);

type TabId = 'trackers' | 'peers' | 'http' | 'content' | 'speed';
const activeTab = ref<TabId>('trackers');

// Speed history for the speed tab
const MAX_HISTORY = 120;
const dlHistory = ref<number[]>([]);
const ulHistory = ref<number[]>([]);
const showDl = ref(true);
const showUl = ref(true);
let _speedTimer: ReturnType<typeof setInterval> | null = null;

// Load details
async function loadDetails(): Promise<void> {
  loading.value = true;
  error.value = null;
  try {
    details.value = await getTorrentDetails(hash.value);
  } catch {
    error.value = 'Failed to load torrent details.';
  } finally {
    loading.value = false;
  }
}

// SignalR detail callback
signalR.onTorrentDetailUpdated((detail: unknown) => {
  if (detail && typeof detail === 'object') {
    details.value = detail as ManagedTorrentView;
  }
});

function sampleSpeed(): void {
  if (!details.value) return;
  dlHistory.value = [...dlHistory.value, details.value.payloadDownloadRate].slice(-MAX_HISTORY);
  ulHistory.value = [...ulHistory.value, details.value.payloadUploadRate].slice(-MAX_HISTORY);
}

// Watch hash changes
watch(hash, async (newHash, oldHash) => {
  if (oldHash) await signalR.unsubscribeTorrent(oldHash);
  dlHistory.value = [];
  ulHistory.value = [];
  if (newHash) {
    await loadDetails();
    await signalR.subscribeTorrent(newHash);
  }
}, { immediate: true });

onMounted(() => {
  _speedTimer = setInterval(sampleSpeed, 1500);
});

onUnmounted(() => {
  if (_speedTimer) clearInterval(_speedTimer);
  if (hash.value) signalR.unsubscribeTorrent(hash.value);
});

function goBack(): void {
  router.push({ name: 'dashboard' });
}

// Computed helpers
const snapshot = computed(() => torrentStore.torrents.get(hash.value) ?? null);
const isSeeding = computed(() => snapshot.value?.isSeeding || snapshot.value?.isFinished);

const etaSeconds = computed(() => {
  if (!details.value) return 0;
  const remaining = details.value.bytesRemaining;
  const rate = details.value.payloadDownloadRate;
  if (rate <= 0 || remaining <= 0) return 0;
  return Math.floor(remaining / rate);
});

function parseDuration(iso: string | null): number {
  if (!iso) return 0;
  const match = iso.match(/^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/);
  if (!match) return 0;
  return parseInt(match[1] ?? '0') * 86400 + parseInt(match[2]) * 3600 + parseInt(match[3]) * 60 + parseInt(match[4]);
}
</script>

<template>
  <div class="details-view">
    <!-- Header -->
    <header class="details-view__header">
      <button class="details-view__back" @click="goBack">
        <PhArrowLeft :size="18" weight="bold" />
        <span>Back</span>
      </button>
      <h1 class="details-view__title">Torrent Details</h1>
    </header>

    <!-- Loading -->
    <div v-if="loading" class="details-view__loading">Loading details…</div>

    <!-- Error -->
    <div v-else-if="error" class="details-view__error">{{ error }}</div>

    <!-- Content -->
    <template v-else-if="details">
      <!-- Summary -->
      <section class="details-summary">
        <h2 class="details-summary__name">{{ details.name }}</h2>

        <div class="details-summary__progress">
          <ProgressBar :value="details.progress" :variant="isSeeding ? 'seeding' : 'download'" />
          <span class="details-summary__pct">{{ formatPercent(details.progress) }}</span>
        </div>

        <!-- Transfer grid -->
        <div class="details-summary__section-title">TRANSFER</div>
        <div class="details-summary__grid">
          <div class="details-summary__stat">
            <span class="details-summary__label">Time Active</span>
            <span class="details-summary__value">{{ parseDuration(details.activeDuration) > 0 ? formatDuration(parseDuration(details.activeDuration)) : '—' }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">ETA</span>
            <span class="details-summary__value">{{ etaSeconds > 0 ? formatDuration(etaSeconds) : '—' }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Connections</span>
            <span class="details-summary__value">{{ details.connectedPeers + details.connectedSeeds }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Downloaded</span>
            <span class="details-summary__value">{{ formatBytes(details.downloaded) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Uploaded</span>
            <span class="details-summary__value">{{ formatBytes(details.uploaded) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Seeds</span>
            <span class="details-summary__value">{{ details.connectedSeeds }} ({{ details.trackerSeeders }})</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">DL Speed</span>
            <span class="details-summary__value" style="color: var(--speed-dl-color)">{{ formatSpeed(details.payloadDownloadRate) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">UL Speed</span>
            <span class="details-summary__value" style="color: var(--speed-ul-color)">{{ formatSpeed(details.payloadUploadRate) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Peers</span>
            <span class="details-summary__value">{{ details.connectedPeers }} ({{ details.trackerLeechers }})</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Share Ratio</span>
            <span class="details-summary__value">{{ details.statsRatio.toFixed(2) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Wasted</span>
            <span class="details-summary__value">{{ formatBytes(details.totalWastedBytes) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Reannounce</span>
            <span class="details-summary__value">{{ details.reannounceIn ?? '—' }}</span>
          </div>
        </div>

        <!-- Info grid -->
        <div class="details-summary__section-title">INFORMATION</div>
        <div class="details-summary__grid details-summary__grid--info">
          <div class="details-summary__stat details-summary__stat--full">
            <span class="details-summary__label">Save Path</span>
            <span class="details-summary__value">{{ details.savePath }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Total Size</span>
            <span class="details-summary__value">{{ formatBytes(details.totalSize) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Pieces</span>
            <span class="details-summary__value">{{ details.piecesCompleted }} / {{ details.totalPieces }} × {{ formatBytes(details.pieceSize) }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Created On</span>
            <span class="details-summary__value">{{ details.creationDate ?? '—' }}</span>
          </div>
          <div class="details-summary__stat details-summary__stat--full">
            <span class="details-summary__label">Info Hash</span>
            <span class="details-summary__value details-summary__value--mono">{{ details.infoHash }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Created By</span>
            <span class="details-summary__value">{{ details.creator ?? '—' }}</span>
          </div>
          <div class="details-summary__stat">
            <span class="details-summary__label">Private</span>
            <span class="details-summary__value">{{ details.isPrivate ? 'Yes' : 'No' }}</span>
          </div>
          <div v-if="details.comment" class="details-summary__stat details-summary__stat--full">
            <span class="details-summary__label">Comment</span>
            <span class="details-summary__value">{{ details.comment }}</span>
          </div>
        </div>
      </section>

      <!-- Tabs -->
      <nav class="details-view__tabs" role="tablist">
        <button v-for="tab in [
          { id: 'trackers', label: 'Trackers' },
          { id: 'peers', label: 'Peers' },
          { id: 'http', label: 'HTTP Sources' },
          { id: 'content', label: 'Content' },
          { id: 'speed', label: 'Speed' },
        ]" :key="tab.id"
          class="details-view__tab"
          :class="{ 'details-view__tab--active': activeTab === tab.id }"
          @click="activeTab = tab.id as TabId"
        >
          {{ tab.label }}
        </button>
      </nav>

      <!-- Tab content -->
      <div class="details-view__panel">
        <!-- Trackers -->
        <div v-if="activeTab === 'trackers'" class="tab-table">
          <table>
            <thead>
              <tr>
                <th>Tier</th><th>URL</th><th>Status</th><th>Peers</th><th>Seeds</th><th>Leeches</th><th>Response</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="t in details.trackers" :key="t.url">
                <td>{{ t.tier }}</td><td class="truncate">{{ t.url }}</td><td>{{ t.status }}</td>
                <td>{{ t.peers }}</td><td>{{ t.seeds }}</td><td>{{ t.leeches }}</td><td>{{ t.responseTime }}</td>
              </tr>
              <tr v-if="details.trackers.length === 0"><td colspan="7" class="empty">No trackers</td></tr>
            </tbody>
          </table>
        </div>

        <!-- Peers -->
        <div v-if="activeTab === 'peers'" class="tab-table">
          <table>
            <thead>
              <tr>
                <th>IP</th><th>Port</th><th>Client</th><th>Progress</th><th>DL Speed</th><th>UL Speed</th><th>Flags</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="p in details.peers" :key="`${p.ipAddress}:${p.port}`">
                <td>{{ p.ipAddress }}</td><td>{{ p.port }}</td><td class="truncate">{{ p.client }}</td>
                <td>{{ formatPercent(p.progress) }}</td>
                <td>{{ formatSpeed(p.downloadRate) }}</td><td>{{ formatSpeed(p.uploadRate) }}</td>
                <td>{{ p.flags }}</td>
              </tr>
              <tr v-if="details.peers.length === 0"><td colspan="7" class="empty">No peers connected</td></tr>
            </tbody>
          </table>
        </div>

        <!-- HTTP Sources -->
        <div v-if="activeTab === 'http'" class="tab-table">
          <table>
            <thead>
              <tr>
                <th>URL</th><th>Type</th><th>Status</th><th>DL Speed</th><th>Downloaded</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="w in details.webSeeds" :key="w.url">
                <td class="truncate">{{ w.url }}</td><td>{{ w.type }}</td><td>{{ w.status }}</td>
                <td>{{ formatSpeed(w.downloadRate) }}</td><td>{{ formatBytes(w.downloaded) }}</td>
              </tr>
              <tr v-if="details.webSeeds.length === 0"><td colspan="5" class="empty">No HTTP sources</td></tr>
            </tbody>
          </table>
        </div>

        <!-- Content / Files -->
        <div v-if="activeTab === 'content'" class="tab-table">
          <table>
            <thead>
              <tr>
                <th>Name</th><th>Size</th><th>Progress</th><th>Priority</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="f in details.files" :key="f.index">
                <td class="truncate">{{ f.name }}</td><td>{{ formatBytes(f.size) }}</td>
                <td>{{ formatPercent(f.progress) }}</td>
                <td>{{ f.priority === 0 ? 'Skip' : f.priority === 1 ? 'Normal' : f.priority === 4 ? 'High' : f.priority === 7 ? 'Maximum' : f.priority }}</td>
              </tr>
              <tr v-if="details.files.length === 0"><td colspan="4" class="empty">No files</td></tr>
            </tbody>
          </table>
        </div>

        <!-- Speed -->
        <div v-if="activeTab === 'speed'" class="tab-speed">
          <div class="tab-speed__header">
            <button class="tab-speed__toggle" :class="{ 'tab-speed__toggle--active': showDl }" @click="showDl = !showDl">
              <span class="tab-speed__dot" style="background: var(--speed-dl-color)" /> Download
            </button>
            <button class="tab-speed__toggle" :class="{ 'tab-speed__toggle--active': showUl }" @click="showUl = !showUl">
              <span class="tab-speed__dot" style="background: var(--speed-ul-color)" /> Upload
            </button>
          </div>
          <div class="tab-speed__chart">
            <SpeedChart :download-history="dlHistory" :upload-history="ulHistory" :show-download="showDl" :show-upload="showUl" />
          </div>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.details-view {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
  max-width: 1200px;
}

/* Header */
.details-view__header {
  display: flex;
  align-items: center;
  gap: var(--spacing-lg);
}

.details-view__back {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: var(--spacing-xs) var(--spacing-md);
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  font-size: var(--font-sm);
  font-weight: 600;
  cursor: pointer;
}

.details-view__back:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.details-view__title {
  font-size: var(--font-xl);
  font-weight: 800;
  color: var(--text-primary);
}

/* Loading / Error */
.details-view__loading, .details-view__error {
  padding: var(--spacing-2xl);
  text-align: center;
  color: var(--text-secondary);
}

.details-view__error { color: var(--status-red); }

/* Summary */
.details-summary {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-xl);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.details-summary__name {
  font-size: var(--font-lg);
  font-weight: 700;
  color: var(--text-primary);
  word-break: break-all;
}

.details-summary__progress {
  display: flex;
  align-items: center;
  gap: var(--spacing-md);
}

.details-summary__progress > :first-child { flex: 1; }

.details-summary__pct {
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  min-width: 48px;
  text-align: right;
}

.details-summary__section-title {
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-tertiary);
  letter-spacing: 0.1em;
  margin-top: var(--spacing-sm);
  padding-bottom: var(--spacing-xs);
  border-bottom: 1px solid var(--border);
}

.details-summary__grid {
  display: grid;
  grid-template-columns: repeat(3, 1fr);
  gap: var(--spacing-sm) var(--spacing-lg);
}

.details-summary__grid--info {
  grid-template-columns: repeat(3, 1fr);
}

.details-summary__stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.details-summary__stat--full {
  grid-column: 1 / -1;
}

.details-summary__label {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
}

.details-summary__value {
  font-size: var(--font-sm);
  color: var(--text-primary);
  font-variant-numeric: tabular-nums;
}

.details-summary__value--mono {
  font-family: monospace;
  font-size: var(--font-xs);
  word-break: break-all;
}

/* Tabs */
.details-view__tabs {
  display: flex;
  gap: 2px;
  border-bottom: 2px solid var(--border);
}

.details-view__tab {
  padding: var(--spacing-sm) var(--spacing-xl);
  font-size: var(--font-md);
  font-weight: 600;
  color: var(--text-secondary);
  border-bottom: 2px solid transparent;
  margin-bottom: -2px;
  cursor: pointer;
}

.details-view__tab:hover { color: var(--text-primary); }

.details-view__tab--active {
  color: var(--accent-active);
  border-bottom-color: var(--accent-active);
}

/* Tab panel */
.details-view__panel {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  overflow: hidden;
  min-height: 200px;
}

/* Tab tables */
.tab-table {
  overflow-x: auto;
}

.tab-table table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--font-sm);
}

.tab-table th {
  text-align: left;
  padding: var(--spacing-sm) var(--spacing-md);
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  border-bottom: 2px solid var(--border);
  white-space: nowrap;
  background: var(--bg-secondary);
}

.tab-table td {
  padding: 5px var(--spacing-md);
  color: var(--text-primary);
  border-bottom: 1px solid var(--border);
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
}

.tab-table .truncate {
  max-width: 300px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.tab-table .empty {
  text-align: center;
  color: var(--text-tertiary);
  padding: var(--spacing-xl);
}

/* Speed tab */
.tab-speed {
  padding: var(--spacing-lg);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.tab-speed__header {
  display: flex;
  gap: var(--spacing-sm);
}

.tab-speed__toggle {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: 3px var(--spacing-sm);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-tertiary);
  cursor: pointer;
}

.tab-speed__toggle--active {
  color: var(--text-primary);
  border-color: var(--text-tertiary);
}

.tab-speed__dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
}

.tab-speed__chart {
  min-height: 200px;
}

/* Responsive */
@media (max-width: 767px) {
  .details-summary__grid {
    grid-template-columns: 1fr 1fr;
  }
}
</style>
