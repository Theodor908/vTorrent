<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useSessionStore } from '@/stores/sessionStore';
import { useSettingsStore } from '@/stores/settingsStore';
import { useTorrentStore } from '@/stores/torrentStore';
import { formatDuration, formatBytes } from '@/utils/format';

const sessionStore = useSessionStore();
const settingsStore = useSettingsStore();
const torrentStore = useTorrentStore();

// Load settings on mount to get the listen port
onMounted(async () => {
  if (!settingsStore.globalSettings) {
    try { await settingsStore.loadSettings(); } catch { /* non-fatal */ }
  }
});

const uptime = computed(() => {
  if (!sessionStore.stats?.uptime) return '—';
  const raw = sessionStore.stats.uptime;
  const match = raw.match(/^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/);
  if (!match) return raw;
  const days = parseInt(match[1] ?? '0', 10);
  const hours = parseInt(match[2], 10);
  const minutes = parseInt(match[3], 10);
  const seconds = parseInt(match[4], 10);
  return formatDuration(days * 86400 + hours * 3600 + minutes * 60 + seconds);
});

// Listen port from settings (not stats — stats.listenPort is never populated by the engine)
const listenPort = computed(() => settingsStore.globalSettings?.connection.listenPort ?? '—');

const externalIp = computed(() => sessionStore.stats?.externalIpAddress ?? '—');

const isPaused = computed(() => sessionStore.stats?.isPaused ?? false);

const torrents = computed(() => sessionStore.stats?.totalTorrents ?? 0);
const active = computed(() => sessionStore.stats?.activeTorrents ?? 0);

// Total downloaded/uploaded — aggregated from torrent store (payload only, like desktop)
const totalDownloaded = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.sessionPayloadDownloaded;
  return formatBytes(sum);
});

const totalUploaded = computed(() => {
  let sum = 0;
  for (const t of torrentStore.torrents.values()) sum += t.totalUploaded;
  return formatBytes(sum);
});
</script>

<template>
  <div class="session-card">
    <h3 class="session-card__title">SESSION</h3>
    <div class="session-card__list">
      <div class="session-card__row">
        <span class="session-card__label">Uptime</span>
        <span class="session-card__value">{{ uptime }}</span>
      </div>
      <div class="session-card__row">
        <span class="session-card__label">Listen Port</span>
        <span class="session-card__value">{{ listenPort }}</span>
      </div>
      <div class="session-card__row">
        <span class="session-card__label">External IP</span>
        <span class="session-card__value">{{ externalIp }}</span>
      </div>
      <div class="session-card__row">
        <span class="session-card__label">Active</span>
        <span class="session-card__value">{{ active }} / {{ torrents }}</span>
      </div>
      <div class="session-card__row">
        <span class="session-card__label">Downloaded</span>
        <span class="session-card__value">{{ totalDownloaded }}</span>
      </div>
      <div class="session-card__row">
        <span class="session-card__label">Uploaded</span>
        <span class="session-card__value">{{ totalUploaded }}</span>
      </div>
      <div v-if="isPaused" class="session-card__row">
        <span class="session-card__label">Status</span>
        <span class="session-card__value session-card__value--warn">Paused</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.session-card {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-lg);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.session-card__title {
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-secondary);
  letter-spacing: 0.1em;
}

.session-card__list {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
}

.session-card__row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  font-size: var(--font-sm);
}

.session-card__label {
  color: var(--text-tertiary);
}

.session-card__value {
  color: var(--text-primary);
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

.session-card__value--warn {
  color: var(--status-orange);
}
</style>
