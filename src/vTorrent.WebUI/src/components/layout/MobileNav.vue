<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch } from 'vue';
import { useRouter } from 'vue-router';
import {
  PhX,
  PhArrowDown,
  PhArrowUp,
  PhList,
  PhFolder,
  PhTag,
  PhGear,
  PhBell,
  PhPause,
} from '@phosphor-icons/vue';
import { useTorrentStore } from '@/stores/torrentStore';
import type { Category } from '@/api/categories';
import type { Tag } from '@/api/tags';
import ThemeToggle from '@/components/common/ThemeToggle.vue';

// ============================================================
// Props / Emits
// ============================================================

const props = defineProps<{
  isOpen: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:isOpen', value: boolean): void;
}>();

// ============================================================
// State
// ============================================================

const router = useRouter();
const torrentStore = useTorrentStore();

const categories = computed(() => torrentStore.categories);
const tags = computed(() => torrentStore.tags);
const notificationsEnabled = ref(false);

// ============================================================
// Data loading
// ============================================================

onMounted(async () => {
  await torrentStore.refreshCategories();
  await torrentStore.refreshTags();
});

// ============================================================
// Keyboard: Escape to close
// ============================================================

function handleKeyDown(e: KeyboardEvent): void {
  if (e.key === 'Escape' && props.isOpen) {
    close();
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleKeyDown);
});

onUnmounted(() => {
  document.removeEventListener('keydown', handleKeyDown);
});

// ============================================================
// Body scroll lock when open
// ============================================================

watch(
  () => props.isOpen,
  (open) => {
    document.body.style.overflow = open ? 'hidden' : '';
  },
);

// ============================================================
// Helpers
// ============================================================

function close(): void {
  emit('update:isOpen', false);
}

function setStatus(key: string | null): void {
  torrentStore.statusFilter = key;
  close();
}

function setCategory(name: string | null): void {
  torrentStore.categoryFilter = name;
  close();
}

function setTag(name: string | null): void {
  torrentStore.tagFilter = name;
  close();
}

function goSettings(): void {
  router.push({ name: 'settings' });
  close();
}

function toggleNotifications(): void {
  notificationsEnabled.value = !notificationsEnabled.value;
  if (notificationsEnabled.value && 'Notification' in window) {
    Notification.requestPermission();
  }
}

const statusCounts = computed(() => torrentStore.statusCounts);
const totalCount = computed(() => torrentStore.torrents.size);
</script>

<template>
  <Teleport to="body">
    <Transition name="mobile-nav">
      <div v-if="isOpen" class="mobile-nav" role="dialog" aria-modal="true" aria-label="Navigation menu">
        <!-- Backdrop -->
        <div class="mobile-nav__backdrop" aria-hidden="true" @click="close" />

        <!-- Panel -->
        <div class="mobile-nav__panel">
          <!-- Header -->
          <div class="mobile-nav__header">
            <div class="mobile-nav__logo">
              <span class="mobile-nav__logo-v">V</span>
              <span class="mobile-nav__logo-text">TORRENT</span>
            </div>
            <button
              class="mobile-nav__close"
              aria-label="Close navigation"
              @click="close"
            >
              <PhX :size="20" weight="bold" />
            </button>
          </div>

          <!-- Nav content -->
          <nav class="mobile-nav__nav">
            <!-- OVERVIEW -->
            <section class="mobile-nav__section">
              <span class="mobile-nav__section-label">OVERVIEW</span>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.statusFilter === null }"
                @click="setStatus(null)"
              >
                <PhList :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">All</span>
                <span v-if="totalCount > 0" class="mobile-nav__item-count">{{ totalCount }}</span>
              </button>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.statusFilter === 'Downloading' }"
                @click="setStatus('Downloading')"
              >
                <PhArrowDown :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">Downloading</span>
                <span v-if="statusCounts.downloading > 0" class="mobile-nav__item-count">
                  {{ statusCounts.downloading }}
                </span>
              </button>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.statusFilter === 'Seeding' }"
                @click="setStatus('Seeding')"
              >
                <PhArrowUp :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">Seeding</span>
                <span v-if="statusCounts.seeding > 0" class="mobile-nav__item-count">
                  {{ statusCounts.seeding }}
                </span>
              </button>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.statusFilter === 'Paused' }"
                @click="setStatus('Paused')"
              >
                <PhPause :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span>Paused</span>
                <span v-if="torrentStore.statusCounts.paused" class="mobile-nav__badge">{{ torrentStore.statusCounts.paused }}</span>
              </button>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.statusFilter === 'Error' }"
                @click="setStatus('Error')"
              >
                <PhX :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">Errored</span>
                <span v-if="statusCounts.errored > 0" class="mobile-nav__item-count">
                  {{ statusCounts.errored }}
                </span>
              </button>
            </section>

            <!-- CATEGORIES -->
            <section class="mobile-nav__section">
              <span class="mobile-nav__section-label">CATEGORIES</span>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.categoryFilter === null }"
                @click="setCategory(null)"
              >
                <PhFolder :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">All</span>
              </button>

              <button
                v-for="cat in categories"
                :key="cat.id"
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.categoryFilter === cat.name }"
                @click="setCategory(cat.name)"
              >
                <span
                  v-if="cat.color"
                  class="mobile-nav__item-dot"
                  :style="{ background: cat.color }"
                />
                <PhFolder v-else :size="18" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">{{ cat.name }}</span>
              </button>
            </section>

            <!-- TAGS -->
            <section class="mobile-nav__section">
              <span class="mobile-nav__section-label">TAGS</span>

              <button
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.tagFilter === null }"
                @click="setTag(null)"
              >
                <PhTag :size="18" weight="bold" class="mobile-nav__item-icon" />
                <span class="mobile-nav__item-label">All</span>
              </button>

              <button
                v-for="tag in tags"
                :key="tag.id"
                class="mobile-nav__item"
                :class="{ 'mobile-nav__item--active': torrentStore.tagFilter === tag.name }"
                @click="setTag(tag.name)"
              >
                <span
                  class="mobile-nav__item-dot"
                  :style="{ background: tag.color ?? 'var(--text-tertiary)' }"
                />
                <span class="mobile-nav__item-label">{{ tag.name }}</span>
              </button>
            </section>
          </nav>

          <!-- Bottom -->
          <div class="mobile-nav__bottom">
            <button
              class="mobile-nav__item mobile-nav__item--bottom"
              @click="goSettings"
            >
              <PhGear :size="18" weight="bold" class="mobile-nav__item-icon" />
              <span class="mobile-nav__item-label">Settings</span>
            </button>

            <button
              class="mobile-nav__item mobile-nav__item--bottom"
              :class="{ 'mobile-nav__item--active': notificationsEnabled }"
              @click="toggleNotifications"
            >
              <PhBell :size="18" weight="bold" class="mobile-nav__item-icon" />
              <span class="mobile-nav__item-label">Notifications</span>
              <span class="mobile-nav__notif-toggle" :class="{ 'mobile-nav__notif-toggle--on': notificationsEnabled }">
                <span class="mobile-nav__notif-thumb" />
              </span>
            </button>

            <div class="mobile-nav__theme">
              <ThemeToggle />
            </div>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.mobile-nav {
  position: fixed;
  inset: 0;
  z-index: 200;
  display: flex;
}

/* ── Backdrop ──────────────────────────────────────────────── */
.mobile-nav__backdrop {
  position: absolute;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  backdrop-filter: blur(2px);
}

/* ── Panel ─────────────────────────────────────────────────── */
.mobile-nav__panel {
  position: relative;
  display: flex;
  flex-direction: column;
  width: min(320px, 85vw);
  height: 100%;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border);
  box-shadow: 4px 0 24px rgba(0, 0, 0, 0.5);
  overflow: hidden;
}

/* ── Header ────────────────────────────────────────────────── */
.mobile-nav__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 var(--spacing-lg);
  height: var(--header-height);
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.mobile-nav__logo {
  display: flex;
  align-items: baseline;
  gap: 3px;
  user-select: none;
}

.mobile-nav__logo-v {
  font-size: 20px;
  font-weight: 900;
  color: var(--accent-active);
  letter-spacing: -0.5px;
}

.mobile-nav__logo-text {
  font-size: var(--font-sm);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.12em;
}

.mobile-nav__close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  transition: color var(--transition-fast), background-color var(--transition-fast);
  cursor: pointer;
}

.mobile-nav__close:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

/* ── Nav ───────────────────────────────────────────────────── */
.mobile-nav__nav {
  flex: 1;
  overflow-y: auto;
  padding: var(--spacing-lg) 0 var(--spacing-md);
  scrollbar-width: thin;
  scrollbar-color: var(--border) transparent;
}

.mobile-nav__section {
  margin-bottom: var(--spacing-xl);
}

.mobile-nav__section-label {
  display: block;
  padding: 0 var(--spacing-lg) var(--spacing-sm);
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-tertiary);
  letter-spacing: 0.1em;
}

/* ── Items ─────────────────────────────────────────────────── */
.mobile-nav__item {
  display: flex;
  align-items: center;
  gap: var(--spacing-md);
  width: 100%;
  padding: 10px var(--spacing-lg);
  color: var(--text-secondary);
  font-size: var(--font-md);
  border-left: 2px solid transparent;
  transition: color var(--transition-fast), background-color var(--transition-fast);
  cursor: pointer;
  text-align: left;
}

.mobile-nav__item:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.mobile-nav__item--active {
  color: var(--text-primary);
  border-left-color: var(--accent-cyan);
  background: color-mix(in srgb, var(--accent-active) 6%, transparent);
}

.mobile-nav__item--active .mobile-nav__item-icon {
  color: var(--accent-cyan);
}

.mobile-nav__item-icon {
  flex-shrink: 0;
  color: inherit;
}

.mobile-nav__item-label {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.mobile-nav__item-count {
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-tertiary);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 0 6px;
  min-width: 22px;
  text-align: center;
  flex-shrink: 0;
}

.mobile-nav__item--active .mobile-nav__item-count {
  color: var(--accent-cyan);
  border-color: color-mix(in srgb, var(--accent-active) 30%, transparent);
  background: color-mix(in srgb, var(--accent-active) 8%, transparent);
}

.mobile-nav__item-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

/* ── Bottom ────────────────────────────────────────────────── */
.mobile-nav__bottom {
  border-top: 1px solid var(--border);
  padding: var(--spacing-sm) 0;
}

.mobile-nav__item--bottom {
  font-size: var(--font-sm);
}

/* Notifications mini-toggle */
.mobile-nav__notif-toggle {
  position: relative;
  display: inline-flex;
  align-items: center;
  width: 32px;
  height: 18px;
  border-radius: 9px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  flex-shrink: 0;
  transition: background-color var(--transition-fast), border-color var(--transition-fast);
}

.mobile-nav__notif-toggle--on {
  background: var(--accent-cyan);
  border-color: var(--accent-cyan);
}

.mobile-nav__notif-thumb {
  position: absolute;
  left: 2px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: var(--text-tertiary);
  transition: transform var(--transition-fast), background-color var(--transition-fast);
}

.mobile-nav__notif-toggle--on .mobile-nav__notif-thumb {
  transform: translateX(14px);
  background: var(--bg-primary);
}

.mobile-nav__theme {
  padding: var(--spacing-xs) var(--spacing-sm);
}

/* ── Transition ─────────────────────────────────────────────── */
.mobile-nav-enter-active,
.mobile-nav-leave-active {
  transition: opacity var(--transition-normal);
}

.mobile-nav-enter-active .mobile-nav__panel,
.mobile-nav-leave-active .mobile-nav__panel {
  transition: transform var(--transition-normal);
}

.mobile-nav-enter-from,
.mobile-nav-leave-to {
  opacity: 0;
}

.mobile-nav-enter-from .mobile-nav__panel,
.mobile-nav-leave-to .mobile-nav__panel {
  transform: translateX(-100%);
}
</style>
