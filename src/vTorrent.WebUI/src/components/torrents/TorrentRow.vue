<script setup lang="ts">
// TorrentRow.vue — A single row in the torrent table.
// Renders exactly the visible columns, formatted appropriately.

import ProgressBar from './ProgressBar.vue';
import type { TorrentSnapshot } from '../../types/torrent';
import { formatBytes, formatSpeed, formatDuration, formatPercent } from '../../utils/format';
import { deriveDisplayState, displayStateBadgeClass, displayStateLabel } from '../../utils/deriveDisplayState';

// ============================================================
// Props / Emits
// ============================================================

const props = defineProps<{
  torrent: TorrentSnapshot;
  visibleColumns: string[];
  isSelected: boolean;
}>();

const emit = defineEmits<{
  (e: 'select', event: MouseEvent): void;
  (e: 'context-menu', event: MouseEvent): void;
}>();

// ============================================================
// Helpers
// ============================================================

function isVisible(col: string): boolean {
  return props.visibleColumns.includes(col);
}

/** Format ISO 8601 date string to short locale date. */
function formatDate(iso: string | null): string {
  if (!iso) return '—';
  try {
    return new Date(iso).toLocaleDateString();
  } catch {
    return iso;
  }
}

/** Parse ISO 8601 duration string (e.g. "1.02:03:04") or TimeSpan to seconds. */
function parseDuration(iso: string | null): number {
  if (!iso) return 0;
  // .NET TimeSpan: [-][d.]hh:mm:ss[.fffffff]
  const match = iso.match(/^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/);
  if (!match) return 0;
  const days = parseInt(match[1] ?? '0', 10);
  const hours = parseInt(match[2], 10);
  const minutes = parseInt(match[3], 10);
  const seconds = parseInt(match[4], 10);
  return days * 86400 + hours * 3600 + minutes * 60 + seconds;
}

/** Compute a rough ETA in seconds based on remaining bytes and current download rate. */
function etaSeconds(): number {
  const remaining = props.torrent.totalWanted - props.torrent.totalWantedDone;
  const rate = props.torrent.payloadDownloadRate;
  if (rate <= 0 || remaining <= 0) return 0;
  return Math.floor(remaining / rate);
}

/** Derived display state for this torrent. */
function displayState() {
  return deriveDisplayState(
    props.torrent.status,
    props.torrent.payloadDownloadRate,
    props.torrent.payloadUploadRate,
    props.torrent.connectedPeers,
  );
}

/** Status badge color class. */
function statusColor(): string {
  return displayStateBadgeClass(displayState());
}

/** Ratio of totalUploaded to totalWantedDone. */
function ratio(): number {
  if (props.torrent.totalWantedDone <= 0) return 0;
  return props.torrent.totalUploaded / props.torrent.totalWantedDone;
}
</script>

<template>
  <tr
    class="torrent-row"
    :class="{ 'torrent-row--selected': isSelected }"
    @click="emit('select', $event)"
    @contextmenu.prevent="emit('context-menu', $event)"
  >
    <!-- Checkbox (always visible) -->
    <td class="col-checkbox" @click.stop>
      <input
        type="checkbox"
        :checked="isSelected"
        @change="emit('select', $event as MouseEvent)"
        aria-label="Select torrent"
      />
    </td>

    <!-- Name -->
    <td v-if="isVisible('Name')" class="col-name">
      <span class="truncate" :title="torrent.name">{{ torrent.name }}</span>
    </td>

    <!-- Progress -->
    <td v-if="isVisible('Progress')" class="col-progress">
      <div class="progress-cell">
        <ProgressBar :value="torrent.verifiedProgress" :variant="torrent.isSeeding || torrent.isFinished ? 'seeding' : 'download'" />
        <span class="progress-text">{{ formatPercent(torrent.verifiedProgress) }}</span>
      </div>
    </td>

    <!-- Size -->
    <td v-if="isVisible('Size')" class="col-size col-right">
      {{ formatBytes(torrent.totalSize) }}
    </td>

    <!-- ETA -->
    <td v-if="isVisible('ETA')" class="col-eta col-right">
      <template v-if="etaSeconds() > 0">{{ formatDuration(etaSeconds()) }}</template>
      <template v-else>—</template>
    </td>

    <!-- Seeds -->
    <td v-if="isVisible('Seeds')" class="col-seeds col-right">
      {{ torrent.connectedSeeds }}
    </td>

    <!-- Peers -->
    <td v-if="isVisible('Peers')" class="col-peers col-right">
      {{ torrent.connectedPeers }}
    </td>

    <!-- Status -->
    <td v-if="isVisible('Status')" class="col-status">
      <span class="badge" :class="statusColor()">{{ displayStateLabel(displayState()) }}</span>
    </td>

    <!-- Down Speed -->
    <td v-if="isVisible('DownloadSpeed')" class="col-speed col-right">
      {{ torrent.payloadDownloadRate > 0 ? formatSpeed(torrent.payloadDownloadRate) : '—' }}
    </td>

    <!-- Up Speed -->
    <td v-if="isVisible('UploadSpeed')" class="col-speed col-right">
      {{ torrent.payloadUploadRate > 0 ? formatSpeed(torrent.payloadUploadRate) : '—' }}
    </td>

    <!-- Ratio -->
    <td v-if="isVisible('Ratio')" class="col-ratio col-right">
      {{ ratio().toFixed(2) }}
    </td>

    <!-- Downloaded -->
    <td v-if="isVisible('Downloaded')" class="col-size col-right">
      {{ formatBytes(torrent.sessionPayloadDownloaded) }}
    </td>

    <!-- Uploaded -->
    <td v-if="isVisible('Uploaded')" class="col-size col-right">
      {{ formatBytes(torrent.totalUploaded) }}
    </td>

    <!-- Added On -->
    <td v-if="isVisible('AddedOn')" class="col-date">
      {{ formatDate(torrent.addedOn) }}
    </td>

    <!-- Completed On -->
    <td v-if="isVisible('CompletedOn')" class="col-date">
      {{ formatDate(torrent.completedOn) }}
    </td>

    <!-- Availability -->
    <td v-if="isVisible('Availability')" class="col-ratio col-right">
      {{ torrent.availability.toFixed(2) }}
    </td>

    <!-- Time Active -->
    <td v-if="isVisible('TimeActive')" class="col-duration col-right">
      {{ parseDuration(torrent.activeDuration) > 0 ? formatDuration(parseDuration(torrent.activeDuration)) : '—' }}
    </td>

    <!-- Seeding Time -->
    <td v-if="isVisible('SeedingTime')" class="col-duration col-right">
      {{ parseDuration(torrent.seedingDuration) > 0 ? formatDuration(parseDuration(torrent.seedingDuration)) : '—' }}
    </td>

    <!-- Save Path -->
    <td v-if="isVisible('SavePath')" class="col-path">
      <span class="truncate" :title="torrent.savePath">{{ torrent.savePath }}</span>
    </td>

    <!-- Category -->
    <td v-if="isVisible('Category')" class="col-category">
      {{ torrent.categoryName ?? '—' }}
    </td>

    <!-- Tags -->
    <td v-if="isVisible('Tags')" class="col-tags">
      {{ torrent.tags.join(', ') || '—' }}
    </td>
  </tr>
</template>

<style scoped>
.torrent-row {
  cursor: pointer;
  transition: background var(--transition-fast);
}

.torrent-row:hover {
  background: var(--bg-hover);
}

.torrent-row--selected {
  background: color-mix(in srgb, var(--accent-active) 8%, transparent);
}

.torrent-row--selected:hover {
  background: color-mix(in srgb, var(--accent-active) 12%, transparent);
}

/* All td base */
.torrent-row td {
  padding: 5px var(--spacing-sm);
  font-size: var(--font-sm);
  color: var(--text-primary);
  border-bottom: 1px solid var(--border);
  vertical-align: middle;
  white-space: nowrap;
  overflow: hidden;
}

/* Checkbox */
.col-checkbox {
  width: 32px;
  text-align: center;
  padding: 0 var(--spacing-xs) !important;
}

.col-checkbox input[type="checkbox"] {
  cursor: pointer;
  accent-color: var(--accent-cyan);
}

/* Right-aligned numeric columns */
.col-right {
  text-align: right;
  font-variant-numeric: tabular-nums;
}

/* Name — wider, truncated */
.col-name {
  min-width: 200px;
  max-width: 300px;
}

/* Progress cell */
.col-progress {
  min-width: 120px;
}

.progress-cell {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.progress-text {
  font-size: var(--font-xs);
  color: var(--text-secondary);
  min-width: 38px;
  text-align: right;
  font-variant-numeric: tabular-nums;
}

/* Truncated text columns */
.truncate {
  display: block;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* Status badge */
.badge {
  display: inline-block;
  padding: 2px 6px;
  border-radius: var(--radius-sm);
  font-size: var(--font-xs);
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.badge--green {
  background: rgba(16, 185, 129, 0.15);
  color: var(--status-green);
}

.badge--blue {
  background: rgba(59, 130, 246, 0.15);
  color: var(--status-blue);
}

.badge--gray {
  background: rgba(100, 116, 139, 0.15);
  color: var(--text-secondary);
}

.badge--red {
  background: rgba(239, 68, 68, 0.15);
  color: var(--status-red);
}

.badge--orange {
  background: rgba(245, 158, 11, 0.15);
  color: var(--status-orange);
}

/* Column widths */
.col-size { min-width: 80px; }
.col-eta { min-width: 70px; }
.col-seeds, .col-peers { min-width: 50px; }
.col-status { min-width: 100px; }
.col-speed { min-width: 80px; }
.col-ratio { min-width: 50px; }
.col-date { min-width: 90px; }
.col-duration { min-width: 70px; }
.col-path { min-width: 140px; max-width: 220px; }
.col-category, .col-tags { min-width: 80px; }
</style>
