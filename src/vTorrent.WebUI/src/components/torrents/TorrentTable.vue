<script setup lang="ts">
// TorrentTable.vue — Main torrent list table.
// Sorting, column visibility toggle (right-click header), multi-select (checkbox / Ctrl / Shift).

import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useRouter } from 'vue-router';
import { useTorrentStore } from '../../stores/torrentStore';
import { useBreakpoint } from '../../composables/useBreakpoint';
import TorrentRow from './TorrentRow.vue';
import TorrentActions from './TorrentActions.vue';
import { deriveDisplayState, displayStateBadgeClass, displayStateLabel } from '../../utils/deriveDisplayState';
import type { TorrentSnapshot } from '../../types/torrent';

const router = useRouter();

// ============================================================
// Store / Breakpoint
// ============================================================

const torrentStore = useTorrentStore();
const { isMobile } = useBreakpoint();

// ============================================================
// Column definitions (20 columns)
// ============================================================

interface ColumnDef {
  key: string;
  label: string;
  alwaysVisible?: boolean;
}

const ALL_COLUMNS: ColumnDef[] = [
  { key: 'Name',          label: 'Name',          alwaysVisible: true },
  { key: 'Progress',      label: 'Progress' },
  { key: 'Size',          label: 'Size' },
  { key: 'ETA',           label: 'ETA' },
  { key: 'Seeds',         label: 'Seeds' },
  { key: 'Peers',         label: 'Peers' },
  { key: 'Status',        label: 'Status' },
  { key: 'DownloadSpeed', label: 'Down Speed' },
  { key: 'UploadSpeed',   label: 'Up Speed' },
  { key: 'Ratio',         label: 'Ratio' },
  { key: 'Downloaded',    label: 'Downloaded' },
  { key: 'Uploaded',      label: 'Uploaded' },
  { key: 'AddedOn',       label: 'Added On' },
  { key: 'CompletedOn',   label: 'Completed On' },
  { key: 'Availability',  label: 'Availability' },
  { key: 'TimeActive',    label: 'Time Active' },
  { key: 'SeedingTime',   label: 'Seeding Time' },
  { key: 'SavePath',      label: 'Save Path' },
  { key: 'Category',      label: 'Category' },
  { key: 'Tags',          label: 'Tags' },
];

/** Keys of columns that are currently visible. */
const visibleColumns = computed((): string[] =>
  ALL_COLUMNS
    .filter(col => col.alwaysVisible || torrentStore.columnVisibility[col.key])
    .map(col => col.key),
);

// ============================================================
// Column visibility dropdown (right-click header)
// ============================================================

const columnMenuVisible = ref(false);
const columnMenuX = ref(0);
const columnMenuY = ref(0);
const columnMenuRef = ref<HTMLElement | null>(null);

function openColumnMenu(event: MouseEvent): void {
  event.preventDefault();
  columnMenuX.value = event.clientX;
  columnMenuY.value = event.clientY;
  columnMenuVisible.value = true;
}

function closeColumnMenu(): void {
  columnMenuVisible.value = false;
}

function handleColumnMenuOutside(event: MouseEvent): void {
  if (!columnMenuRef.value) return;
  if (!columnMenuRef.value.contains(event.target as Node)) {
    closeColumnMenu();
  }
}

// ============================================================
// Sorting
// ============================================================

function handleSort(columnKey: string): void {
  if (torrentStore.sortColumn === columnKey) {
    torrentStore.sortDirection = torrentStore.sortDirection === 'asc' ? 'desc' : 'asc';
  } else {
    torrentStore.sortColumn = columnKey;
    torrentStore.sortDirection = 'asc';
  }
}

function sortArrow(columnKey: string): string {
  if (torrentStore.sortColumn !== columnKey) return '';
  return torrentStore.sortDirection === 'asc' ? ' ▲' : ' ▼';
}

// ============================================================
// Row selection — single, Ctrl+Click, Shift+Click
// ============================================================

let lastSelectedIndex = -1;

function handleRowSelect(hash: string, event: MouseEvent): void {
  const torrentList = torrentStore.filteredTorrents;
  const index = torrentList.findIndex(t => t.infoHash === hash);

  if (event.shiftKey && lastSelectedIndex >= 0) {
    // Range select
    const start = Math.min(lastSelectedIndex, index);
    const end = Math.max(lastSelectedIndex, index);
    torrentStore.selectedHashes.clear();
    for (let i = start; i <= end; i++) {
      torrentStore.selectedHashes.add(torrentList[i].infoHash);
    }
  } else if (event.ctrlKey || event.metaKey) {
    // Toggle individual
    if (torrentStore.selectedHashes.has(hash)) {
      torrentStore.selectedHashes.delete(hash);
    } else {
      torrentStore.selectedHashes.add(hash);
    }
    lastSelectedIndex = index;
  } else {
    // Single select
    torrentStore.selectedHashes.clear();
    torrentStore.selectedHashes.add(hash);
    torrentStore.selectedHash = hash;
    lastSelectedIndex = index;
  }
}

// ============================================================
// Select all checkbox
// ============================================================

const allSelected = computed(() =>
  torrentStore.filteredTorrents.length > 0 &&
  torrentStore.filteredTorrents.every(t => torrentStore.selectedHashes.has(t.infoHash)),
);

function toggleSelectAll(): void {
  if (allSelected.value) {
    torrentStore.selectedHashes.clear();
  } else {
    for (const t of torrentStore.filteredTorrents) {
      torrentStore.selectedHashes.add(t.infoHash);
    }
  }
}

// ============================================================
// Context menu (TorrentActions)
// ============================================================

const contextMenuHash = ref<string | null>(null);
const contextMenuX = ref(0);
const contextMenuY = ref(0);
const contextMenuVisible = ref(false);

function handleRowContextMenu(hash: string, event: MouseEvent): void {
  // If the row isn't selected, select it
  if (!torrentStore.selectedHashes.has(hash)) {
    torrentStore.selectedHashes.clear();
    torrentStore.selectedHashes.add(hash);
    torrentStore.selectedHash = hash;
  }
  contextMenuHash.value = hash;
  contextMenuX.value = event.clientX;
  contextMenuY.value = event.clientY;
  contextMenuVisible.value = true;
}

function closeContextMenu(): void {
  contextMenuVisible.value = false;
  contextMenuHash.value = null;
}

function handleOpenDetails(): void {
  const hash = contextMenuHash.value;
  closeContextMenu();
  if (hash) {
    router.push({ name: 'torrent-details', params: { hash } });
  }
}

// ============================================================
// Double-click row → open details
// ============================================================

function handleRowDblClick(hash: string): void {
  router.push({ name: 'torrent-details', params: { hash } });
}

// ============================================================
// Mobile card helpers
// ============================================================

function handleCardTap(hash: string): void {
  torrentStore.selectedHashes.clear();
  torrentStore.selectedHashes.add(hash);
  torrentStore.selectedHash = hash;
}

function handleCardMenuTap(hash: string, event: MouseEvent): void {
  event.stopPropagation();
  contextMenuHash.value = hash;
  // Position menu near the tap — use a fixed center-bottom fallback on mobile
  contextMenuX.value = Math.min(event.clientX, window.innerWidth - 200);
  contextMenuY.value = event.clientY;
  contextMenuVisible.value = true;
}

function statusClass(torrent: TorrentSnapshot): string {
  const state = deriveDisplayState(
    torrent.status,
    torrent.payloadDownloadRate,
    torrent.payloadUploadRate,
    torrent.connectedPeers,
  );
  const badge = displayStateBadgeClass(state);
  return badge.replace('badge--', 'card__status--');
}

// ============================================================
// Lifecycle — load column visibility, global listeners
// ============================================================

onMounted(() => {
  torrentStore.loadColumnVisibility();
  document.addEventListener('mousedown', handleColumnMenuOutside, true);
});

onUnmounted(() => {
  document.removeEventListener('mousedown', handleColumnMenuOutside, true);
});
</script>

<template>
  <div class="torrent-table-wrapper">
    <!-- Empty state -->
    <div v-if="torrentStore.filteredTorrents.length === 0" class="empty-state">
      <span class="empty-state__icon">📭</span>
      <p class="empty-state__text">No torrents found</p>
      <p class="empty-state__hint">Add a torrent to get started</p>
    </div>

    <!-- Mobile card list -->
    <div v-else-if="isMobile.value" class="card-list">
      <div
        v-for="torrent in torrentStore.filteredTorrents"
        :key="torrent.infoHash"
        class="torrent-card"
        :class="{ 'torrent-card--selected': torrentStore.selectedHashes.has(torrent.infoHash) }"
        @click="handleCardTap(torrent.infoHash)"
        @dblclick="handleRowDblClick(torrent.infoHash)"
      >
        <!-- Name row -->
        <div class="card__name-row">
          <span class="card__name">{{ torrent.name }}</span>
          <button
            class="card__menu-btn"
            aria-label="Actions"
            @click="handleCardMenuTap(torrent.infoHash, $event)"
          >
            ⋯
          </button>
        </div>

        <!-- Progress bar -->
        <div class="card__progress-row">
          <div class="card__progress-bar">
            <div
              class="card__progress-fill"
              :style="{ width: `${(torrent.verifiedProgress * 100).toFixed(1)}%` }"
            />
          </div>
          <span class="card__progress-pct">{{ (torrent.verifiedProgress * 100).toFixed(1) }}%</span>
        </div>

        <!-- Speed & status row -->
        <div class="card__meta-row">
          <span class="card__speed card__speed--down">
            ↓ {{ torrent.payloadDownloadRate > 0 ? (torrent.payloadDownloadRate / 1024).toFixed(1) + ' KB/s' : '—' }}
          </span>
          <span class="card__speed card__speed--up">
            ↑ {{ torrent.payloadUploadRate > 0 ? (torrent.payloadUploadRate / 1024).toFixed(1) + ' KB/s' : '—' }}
          </span>
          <span class="card__status" :class="statusClass(torrent)">
            {{ displayStateLabel(deriveDisplayState(torrent.status, torrent.payloadDownloadRate, torrent.payloadUploadRate, torrent.connectedPeers)) }}
          </span>
        </div>
      </div>
    </div>

    <!-- Desktop/tablet table -->
    <div v-else class="table-scroll">
      <table class="torrent-table" @contextmenu.prevent>
        <!-- ── Header ─────────────────────────────────────────── -->
        <thead>
          <tr @contextmenu.prevent="openColumnMenu">
            <!-- Select-all checkbox -->
            <th class="th-checkbox">
              <input
                type="checkbox"
                :checked="allSelected"
                :indeterminate="torrentStore.selectedHashes.size > 0 && !allSelected"
                @change="toggleSelectAll"
                aria-label="Select all torrents"
              />
            </th>

            <!-- Column headers -->
            <th
              v-for="col in ALL_COLUMNS"
              v-show="col.alwaysVisible || torrentStore.columnVisibility[col.key]"
              :key="col.key"
              class="th-col"
              :class="{ 'th-col--active': torrentStore.sortColumn === col.key }"
              @click="handleSort(col.key)"
            >
              {{ col.label }}<span class="sort-arrow">{{ sortArrow(col.key) }}</span>
            </th>
          </tr>
        </thead>

        <!-- ── Body ──────────────────────────────────────────── -->
        <tbody>
          <TorrentRow
            v-for="torrent in torrentStore.filteredTorrents"
            :key="torrent.infoHash"
            :torrent="torrent"
            :visible-columns="visibleColumns"
            :is-selected="torrentStore.selectedHashes.has(torrent.infoHash)"
            @select="handleRowSelect(torrent.infoHash, $event)"
            @context-menu="handleRowContextMenu(torrent.infoHash, $event)"
            @dblclick="handleRowDblClick(torrent.infoHash)"
          />
        </tbody>
      </table>
    </div>

    <!-- ── Column visibility dropdown (right-click header) ── -->
    <Teleport to="body">
      <div
        v-if="columnMenuVisible"
        ref="columnMenuRef"
        class="column-menu"
        :style="{ left: `${columnMenuX}px`, top: `${columnMenuY}px` }"
        role="menu"
        aria-label="Toggle columns"
        @click.stop
      >
        <div class="column-menu__title">Visible Columns</div>
        <label
          v-for="col in ALL_COLUMNS"
          :key="col.key"
          class="column-menu__item"
          :class="{ 'column-menu__item--disabled': col.alwaysVisible }"
        >
          <input
            type="checkbox"
            :checked="col.alwaysVisible || torrentStore.columnVisibility[col.key]"
            :disabled="col.alwaysVisible"
            @change="torrentStore.toggleColumn(col.key)"
          />
          {{ col.label }}
        </label>
      </div>
    </Teleport>

    <!-- ── Context menu ───────────────────────────────────── -->
    <TorrentActions
      v-if="contextMenuHash"
      :hash="contextMenuHash"
      :position="{ x: contextMenuX, y: contextMenuY }"
      :visible="contextMenuVisible"
      @close="closeContextMenu"
    />
  </div>
</template>

<style scoped>
/* ── Wrapper ────────────────────────────────────────────── */
.torrent-table-wrapper {
  width: 100%;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  overflow: hidden;
}

/* ── Scroll container ───────────────────────────────────── */
.table-scroll {
  flex: 1;
  overflow: auto;
  min-height: 0;
}

/* ── Table ──────────────────────────────────────────────── */
.torrent-table {
  width: 100%;
  table-layout: fixed;
  border-collapse: collapse;
  font-size: var(--font-sm);
}

/* ── Header ─────────────────────────────────────────────── */
thead {
  position: sticky;
  top: 0;
  z-index: 10;
  background: var(--bg-secondary);
}

.th-checkbox {
  width: 32px;
  padding: var(--spacing-sm) var(--spacing-xs);
  text-align: center;
  border-bottom: 2px solid var(--border);
}

.th-checkbox input[type="checkbox"] {
  cursor: pointer;
  accent-color: var(--accent-cyan);
}

.th-col {
  padding: var(--spacing-sm) var(--spacing-sm);
  text-align: left;
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  border-bottom: 2px solid var(--border);
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
  overflow: hidden;
  transition: color var(--transition-fast), background var(--transition-fast);
}

.th-col:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.th-col--active {
  color: var(--accent-cyan);
}

.sort-arrow {
  font-size: 10px;
  margin-left: 2px;
}

/* ── Alternating row backgrounds ────────────────────────── */
tbody tr:nth-child(even) {
  background: color-mix(in srgb, var(--text-primary) 2%, transparent);
}

/* ── Empty state ────────────────────────────────────────── */
.empty-state {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--spacing-sm);
  padding: var(--spacing-2xl);
  color: var(--text-secondary);
}

.empty-state__icon {
  font-size: 48px;
}

.empty-state__text {
  font-size: var(--font-lg);
  font-weight: 600;
  margin: 0;
}

.empty-state__hint {
  font-size: var(--font-sm);
  color: var(--text-tertiary);
  margin: 0;
}

/* ── Column visibility dropdown ─────────────────────────── */
.column-menu {
  position: fixed;
  z-index: 9999;
  min-width: 180px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  padding: var(--spacing-sm);
  font-size: var(--font-sm);
}

.column-menu__title {
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-tertiary);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
  margin-bottom: var(--spacing-xs);
}

.column-menu__item {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  padding: 4px var(--spacing-xs);
  cursor: pointer;
  color: var(--text-primary);
  border-radius: var(--radius-sm);
  transition: background var(--transition-fast);
}

.column-menu__item:hover {
  background: var(--bg-hover);
}

.column-menu__item--disabled {
  color: var(--text-tertiary);
  cursor: default;
}

.column-menu__item--disabled:hover {
  background: none;
}

.column-menu__item input[type="checkbox"] {
  accent-color: var(--accent-cyan);
  cursor: pointer;
}

.column-menu__item--disabled input[type="checkbox"] {
  cursor: default;
}

/* ── Mobile card list ────────────────────────────────────── */
.card-list {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 1px;
  background: var(--border);
  min-height: 0;
}

.torrent-card {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
  padding: var(--spacing-sm) var(--spacing-md);
  background: var(--bg-card);
  cursor: pointer;
  user-select: none;
  transition: background var(--transition-fast);
}

.torrent-card:active {
  background: var(--bg-hover);
}

.torrent-card--selected {
  background: color-mix(in srgb, var(--accent-active) 6%, transparent);
  border-left: 2px solid var(--accent-cyan);
}

/* Name row */
.card__name-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.card__name {
  flex: 1;
  min-width: 0;
  font-size: var(--font-sm);
  font-weight: 500;
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card__menu-btn {
  flex-shrink: 0;
  width: 28px;
  height: 28px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: none;
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  color: var(--text-secondary);
  font-size: 16px;
  cursor: pointer;
  transition: color var(--transition-fast), background var(--transition-fast);
}

.card__menu-btn:active {
  background: var(--bg-hover);
  color: var(--text-primary);
}

/* Progress row */
.card__progress-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.card__progress-bar {
  flex: 1;
  height: 4px;
  background: var(--bg-input);
  border-radius: 2px;
  overflow: hidden;
}

.card__progress-fill {
  height: 100%;
  background: var(--accent-cyan);
  border-radius: 2px;
  transition: width var(--transition-normal);
}

.card__progress-pct {
  flex-shrink: 0;
  font-size: var(--font-xs);
  color: var(--text-secondary);
  min-width: 42px;
  text-align: right;
}

/* Meta row */
.card__meta-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.card__speed {
  font-size: var(--font-xs);
  color: var(--text-secondary);
}

.card__speed--down {
  color: var(--status-green);
}

.card__speed--up {
  color: var(--accent-cyan);
}

.card__status {
  margin-left: auto;
  font-size: var(--font-xs);
  font-weight: 600;
  padding: 1px 6px;
  border-radius: var(--radius-sm);
  border: 1px solid var(--border);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.card__status--downloading {
  color: var(--status-green);
  border-color: rgba(16, 185, 129, 0.4);
  background: rgba(16, 185, 129, 0.08);
}

.card__status--seeding {
  color: var(--accent-cyan);
  border-color: color-mix(in srgb, var(--accent-active) 40%, transparent);
  background: color-mix(in srgb, var(--accent-active) 8%, transparent);
}

.card__status--error {
  color: var(--status-red);
  border-color: rgba(239, 68, 68, 0.4);
  background: rgba(239, 68, 68, 0.08);
}

.card__status--paused {
  color: var(--text-tertiary);
  border-color: var(--border);
}

.card__status--other {
  color: var(--text-secondary);
  border-color: var(--border);
}

.card__status--green { color: var(--status-green, #10b981); }
.card__status--blue { color: var(--status-blue, #3b82f6); }
.card__status--gray { color: var(--text-secondary, #64748b); }
.card__status--red { color: var(--status-red, #ef4444); }
.card__status--orange { color: var(--status-orange, #f59e0b); }
</style>
