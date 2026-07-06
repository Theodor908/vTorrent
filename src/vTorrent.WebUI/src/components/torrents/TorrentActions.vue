<script setup lang="ts">
// TorrentActions.vue — Context menu for a single torrent row.
// Appears at a fixed screen position; closes on outside click or Escape.

import { ref, watch, onMounted, onUnmounted, nextTick } from 'vue';
import { useRouter } from 'vue-router';
import * as torrentsApi from '../../api/torrents';
import * as categoriesApi from '../../api/categories';
import * as tagsApi from '../../api/tags';
import type { Category } from '../../api/categories';
import type { Tag } from '../../api/tags';
import { useTorrentStore } from '../../stores/torrentStore';

// ============================================================
// Props / Emits
// ============================================================

const props = defineProps<{
  hash: string;
  position: { x: number; y: number };
  visible: boolean;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
}>();

const router = useRouter();

// ============================================================
// Store access — to read current torrent state
// ============================================================

const torrentStore = useTorrentStore();

// ============================================================
// Submenu state
// ============================================================

const showCategorySubmenu = ref(false);
const showTagsSubmenu = ref(false);
const showDeleteConfirm = ref(false);
const categories = ref<Category[]>([]);
const tags = ref<Tag[]>([]);

// ============================================================
// Computed helpers
// ============================================================

function currentTorrent() {
  return torrentStore.torrents.get(props.hash) ?? null;
}

function isActive(): boolean {
  const t = currentTorrent();
  if (!t) return false;
  const phase = t.status.phase;
  return phase === 'Downloading' || phase === 'Seeding' || phase === 'FetchingMetadata' || phase === 'Connecting';
}

function isPaused(): boolean {
  const t = currentTorrent();
  if (!t) return false;
  return t.status.intent === 'Paused' || t.status.phase === 'Idle';
}

// ============================================================
// Menu positioning — clamp to viewport
// ============================================================

const menuRef = ref<HTMLElement | null>(null);
const computedX = ref(props.position.x);
const computedY = ref(props.position.y);

async function clampPosition(): Promise<void> {
  await nextTick();
  if (!menuRef.value) return;
  const rect = menuRef.value.getBoundingClientRect();
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  computedX.value = props.position.x + rect.width > vw
    ? Math.max(0, vw - rect.width - 8)
    : props.position.x;
  computedY.value = props.position.y + rect.height > vh
    ? Math.max(0, vh - rect.height - 8)
    : props.position.y;
}

// ============================================================
// Load categories & tags when visible
// ============================================================

watch(
  () => props.visible,
  async (visible) => {
    if (visible) {
      showCategorySubmenu.value = false;
      showTagsSubmenu.value = false;
      showDeleteConfirm.value = false;
      await clampPosition();
      try {
        [categories.value, tags.value] = await Promise.all([
          categoriesApi.getCategories(),
          tagsApi.getTags(),
        ]);
      } catch {
        // Non-fatal — submenus will be empty
      }
    }
  },
);

// ============================================================
// Close on outside click / Escape
// ============================================================

function handleGlobalClick(event: MouseEvent): void {
  if (!menuRef.value) return;
  if (!menuRef.value.contains(event.target as Node)) {
    emit('close');
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    emit('close');
  }
}

onMounted(() => {
  document.addEventListener('mousedown', handleGlobalClick, true);
  document.addEventListener('keydown', handleKeydown);
});

onUnmounted(() => {
  document.removeEventListener('mousedown', handleGlobalClick, true);
  document.removeEventListener('keydown', handleKeydown);
});

// ============================================================
// Actions
// ============================================================

async function pause(): Promise<void> {
  emit('close');
  try { await torrentsApi.pauseTorrent(props.hash); } catch { /* ignore */ }
}

async function resume(): Promise<void> {
  emit('close');
  try { await torrentsApi.resumeTorrent(props.hash); } catch { /* ignore */ }
}

async function forceStart(): Promise<void> {
  emit('close');
  try { await torrentsApi.forceStartTorrent(props.hash); } catch { /* ignore */ }
}

async function recheck(): Promise<void> {
  emit('close');
  try { await torrentsApi.recheckTorrent(props.hash); } catch { /* ignore */ }
}

async function confirmDelete(): Promise<void> {
  emit('close');
  try { await torrentsApi.deleteTorrent(props.hash); } catch { /* ignore */ }
}

function openDetails(): void {
  const hash = props.hash;
  emit('close');
  router.push({ name: 'torrent-details', params: { hash } });
}

async function setCategory(id: number | null): Promise<void> {
  emit('close');
  try { await torrentsApi.setTorrentCategory(props.hash, id); } catch { /* ignore */ }
}

async function moveQueue(pos: torrentsApi.QueuePosition): Promise<void> {
  emit('close');
  try { await torrentsApi.setQueuePosition(props.hash, pos); } catch { /* ignore */ }
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      ref="menuRef"
      class="context-menu"
      :style="{ left: `${computedX}px`, top: `${computedY}px` }"
      role="menu"
      aria-label="Torrent actions"
      @click.stop
    >
      <!-- Pause / Resume (conditional) -->
      <template v-if="isActive()">
        <button class="menu-item" role="menuitem" @click="pause">
          <span class="menu-icon">⏸</span>
          Pause
        </button>
      </template>
      <template v-if="isPaused()">
        <button class="menu-item" role="menuitem" @click="resume">
          <span class="menu-icon">▶</span>
          Resume
        </button>
      </template>

      <button class="menu-item" role="menuitem" @click="forceStart">
        <span class="menu-icon">⚡</span>
        Force Start
      </button>

      <button class="menu-item" role="menuitem" @click="recheck">
        <span class="menu-icon">🔍</span>
        Recheck
      </button>

      <button class="menu-item" role="menuitem" @click="openDetails">
        <span class="menu-icon">ℹ</span>
        Details
      </button>

      <div class="menu-separator" role="separator" />

      <!-- Delete with inline confirm -->
      <template v-if="!showDeleteConfirm">
        <button class="menu-item menu-item--danger" role="menuitem" @click="showDeleteConfirm = true">
          <span class="menu-icon">🗑</span>
          Delete
        </button>
      </template>
      <template v-else>
        <div class="delete-confirm">
          <span class="delete-confirm__label">Delete this torrent?</span>
          <div class="delete-confirm__buttons">
            <button class="btn btn--danger" @click="confirmDelete">Yes</button>
            <button class="btn" @click="showDeleteConfirm = false">Cancel</button>
          </div>
        </div>
      </template>

      <div class="menu-separator" role="separator" />

      <!-- Set Category submenu -->
      <div
        class="menu-item menu-item--submenu"
        role="menuitem"
        aria-haspopup="true"
        :aria-expanded="showCategorySubmenu"
        @mouseenter="showCategorySubmenu = true; showTagsSubmenu = false"
      >
        <span class="menu-icon">📁</span>
        Set Category
        <span class="menu-arrow">›</span>
        <div v-if="showCategorySubmenu" class="submenu">
          <button class="menu-item" @click="setCategory(null)">
            (None)
          </button>
          <button
            v-for="cat in categories"
            :key="cat.id"
            class="menu-item"
            @click="setCategory(cat.id)"
          >
            {{ cat.name }}
          </button>
          <span v-if="categories.length === 0" class="menu-item menu-item--disabled">No categories</span>
        </div>
      </div>

      <!-- Set Tags submenu -->
      <div
        class="menu-item menu-item--submenu"
        role="menuitem"
        aria-haspopup="true"
        :aria-expanded="showTagsSubmenu"
        @mouseenter="showTagsSubmenu = true; showCategorySubmenu = false"
      >
        <span class="menu-icon">🏷</span>
        Set Tags
        <span class="menu-arrow">›</span>
        <div v-if="showTagsSubmenu" class="submenu">
          <span v-if="tags.length === 0" class="menu-item menu-item--disabled">No tags</span>
          <button
            v-for="tag in tags"
            :key="tag.id"
            class="menu-item"
            @click="setCategory(null)"
          >
            {{ tag.name }}
          </button>
        </div>
      </div>

      <div class="menu-separator" role="separator" />

      <!-- Queue -->
      <button class="menu-item" role="menuitem" @click="moveQueue('top')">
        <span class="menu-icon">⏫</span>
        Move to Top
      </button>
      <button class="menu-item" role="menuitem" @click="moveQueue('up')">
        <span class="menu-icon">↑</span>
        Move Up
      </button>
      <button class="menu-item" role="menuitem" @click="moveQueue('down')">
        <span class="menu-icon">↓</span>
        Move Down
      </button>
      <button class="menu-item" role="menuitem" @click="moveQueue('bottom')">
        <span class="menu-icon">⏬</span>
        Move to Bottom
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.context-menu {
  position: fixed;
  z-index: 9999;
  min-width: 200px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  padding: var(--spacing-xs) 0;
  font-size: var(--font-sm);
  color: var(--text-primary);
}

.menu-item {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  width: 100%;
  padding: 6px var(--spacing-md);
  background: none;
  border: none;
  color: var(--text-primary);
  font-size: var(--font-sm);
  text-align: left;
  cursor: pointer;
  white-space: nowrap;
  position: relative;
}

.menu-item:hover {
  background: var(--bg-hover);
}

.menu-item--danger {
  color: var(--status-red);
}

.menu-item--disabled {
  color: var(--text-tertiary);
  cursor: default;
}

.menu-item--disabled:hover {
  background: none;
}

.menu-icon {
  width: 16px;
  text-align: center;
  font-size: 12px;
}

.menu-arrow {
  margin-left: auto;
  color: var(--text-secondary);
}

.menu-separator {
  height: 1px;
  background: var(--border);
  margin: var(--spacing-xs) 0;
}

/* Submenu */
.menu-item--submenu {
  cursor: pointer;
}

.submenu {
  position: absolute;
  left: 100%;
  top: 0;
  min-width: 180px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  padding: var(--spacing-xs) 0;
  z-index: 10000;
}

/* Delete confirm inline */
.delete-confirm {
  padding: var(--spacing-sm) var(--spacing-md);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
}

.delete-confirm__label {
  font-size: var(--font-sm);
  color: var(--text-secondary);
}

.delete-confirm__buttons {
  display: flex;
  gap: var(--spacing-sm);
}

.btn {
  flex: 1;
  padding: 4px var(--spacing-sm);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  background: var(--bg-hover);
  color: var(--text-primary);
  font-size: var(--font-xs);
  cursor: pointer;
}

.btn:hover {
  background: var(--border);
}

.btn--danger {
  background: var(--status-red);
  border-color: var(--status-red);
  color: #fff;
}

.btn--danger:hover {
  opacity: 0.85;
}
</style>
