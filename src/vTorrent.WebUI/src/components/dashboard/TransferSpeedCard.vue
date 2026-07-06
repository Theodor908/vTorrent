<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useTorrentStore } from '@/stores/torrentStore';
import SpeedChart from '@/components/common/SpeedChart.vue';
import { formatSpeed, formatBytes } from '@/utils/format';

const torrentStore = useTorrentStore();

const MAX_HISTORY = 60;
const downloadHistory = ref<number[]>([]);
const uploadHistory = ref<number[]>([]);
const showDl = ref(true);
const showUl = ref(true);

// Aggregate payload rates from all torrents (excludes protocol overhead)
// This matches how the desktop UI calculates global speeds.
const aggregatedDlRate = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.payloadDownloadRate;
  return sum;
});

const aggregatedUlRate = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.payloadUploadRate;
  return sum;
});

const aggregatedDlBytes = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.sessionPayloadDownloaded;
  return sum;
});

const aggregatedUlBytes = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.totalUploaded;
  return sum;
});

// Sample speed history from aggregated payload rates
let _historyTimer: ReturnType<typeof setInterval> | null = null;

function sampleSpeeds(): void {
  downloadHistory.value = [...downloadHistory.value, aggregatedDlRate.value].slice(-MAX_HISTORY);
  uploadHistory.value = [...uploadHistory.value, aggregatedUlRate.value].slice(-MAX_HISTORY);
}

// Start sampling on mount (every 1.5s to match SignalR broadcast interval)
import { onMounted, onUnmounted } from 'vue';

onMounted(() => {
  sampleSpeeds(); // Initial sample
  _historyTimer = setInterval(sampleSpeeds, 1500);
});

onUnmounted(() => {
  if (_historyTimer) clearInterval(_historyTimer);
});

const dlSpeed = computed(() => formatSpeed(aggregatedDlRate.value));
const ulSpeed = computed(() => formatSpeed(aggregatedUlRate.value));
const totalReceived = computed(() => formatBytes(aggregatedDlBytes.value));
const totalSent = computed(() => formatBytes(aggregatedUlBytes.value)
);
</script>

<template>
  <div class="transfer-card">
    <div class="transfer-card__header">
      <h3 class="transfer-card__title">GLOBAL TRANSFER SPEED</h3>
      <div class="transfer-card__legend">
        <button
          class="transfer-card__legend-btn"
          :class="{ 'transfer-card__legend-btn--active': showDl }"
          @click="showDl = !showDl"
        >
          <span class="transfer-card__dot transfer-card__dot--dl" /> DOWNLOAD
        </button>
        <button
          class="transfer-card__legend-btn"
          :class="{ 'transfer-card__legend-btn--active': showUl }"
          @click="showUl = !showUl"
        >
          <span class="transfer-card__dot transfer-card__dot--ul" /> UPLOAD
        </button>
      </div>
    </div>
    <div class="transfer-card__chart">
      <SpeedChart
        :download-history="downloadHistory"
        :upload-history="uploadHistory"
        :show-download="showDl"
        :show-upload="showUl"
      />
    </div>
    <div class="transfer-card__footer">
      <span class="transfer-card__dl">↓ {{ dlSpeed }}</span>
      <span class="transfer-card__ul">↑ {{ ulSpeed }}</span>
      <span class="transfer-card__totals">Recv: {{ totalReceived }} · Sent: {{ totalSent }}</span>
    </div>
  </div>
</template>

<style scoped>
.transfer-card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-lg);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
}

.transfer-card__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.transfer-card__title {
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-secondary);
  letter-spacing: 0.1em;
}

.transfer-card__legend {
  display: flex;
  gap: var(--spacing-sm);
}

.transfer-card__legend-btn {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: 3px var(--spacing-sm);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-tertiary);
  background: transparent;
  cursor: pointer;
  transition: border-color var(--transition-fast), color var(--transition-fast);
}

.transfer-card__legend-btn--active {
  color: var(--text-primary);
  border-color: var(--text-tertiary);
}

.transfer-card__dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
}

.transfer-card__dot--dl {
  background: var(--speed-dl-color);
}

.transfer-card__dot--ul {
  background: var(--speed-ul-color);
}

.transfer-card__chart {
  min-height: 100px;
}

.transfer-card__footer {
  display: flex;
  align-items: center;
  gap: var(--spacing-lg);
  font-size: var(--font-sm);
  font-variant-numeric: tabular-nums;
}

.transfer-card__dl {
  color: var(--speed-dl-color);
  font-weight: 600;
}

.transfer-card__ul {
  color: var(--speed-ul-color);
  font-weight: 600;
}

.transfer-card__totals {
  color: var(--text-tertiary);
  margin-left: auto;
  font-size: var(--font-xs);
}
</style>
