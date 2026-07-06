<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue';
import { useRouter } from 'vue-router';
import {
  PhMagnifyingGlass,
  PhPlus,
  PhLink,
  PhGlobe,
  PhGear,
  PhList,
} from '@phosphor-icons/vue';
import { useTorrentStore } from '@/stores/torrentStore';
import { useSessionStore } from '@/stores/sessionStore';
import { useProfileStore } from '@/stores/profileStore';
import { useSignalR } from '@/composables/useSignalR';
import { useTheme } from '@/composables/useTheme';
import { useConnection } from '@/composables/useConnection';
import * as dhtApi from '@/api/dht';

// ============================================================
// Emits
// ============================================================

const emit = defineEmits<{
  (e: 'open-add-torrent'): void;
  (e: 'open-add-magnet'): void;
  (e: 'toggle-mobile-nav'): void;
}>();

// ============================================================
// Stores / composables
// ============================================================

const router = useRouter();
const torrentStore = useTorrentStore();
const sessionStore = useSessionStore();
const profileStore = useProfileStore();
const signalR = useSignalR();
const connection = useConnection();
const { isDark } = useTheme();

// ============================================================
// Profile dropdown
// ============================================================

const profileDropdownOpen = ref(false);

function toggleProfileDropdown(): void {
  if (profileStore.scheduleEnabled) return;
  profileDropdownOpen.value = !profileDropdownOpen.value;
}

async function selectProfile(name: string): Promise<void> {
  try {
    await profileStore.activateProfile(name);
  } catch { /* 409 if schedule active */ }
  profileDropdownOpen.value = false;
}

function closeProfileDropdown(e: MouseEvent): void {
  const target = e.target as HTMLElement;
  if (!target.closest('.app-header__profile') && !target.closest('.app-header__profile-dropdown')) {
    profileDropdownOpen.value = false;
  }
}

onMounted(() => {
  document.addEventListener('click', closeProfileDropdown);
});

onUnmounted(() => {
  document.removeEventListener('click', closeProfileDropdown);
});

// ============================================================
// Search
// ============================================================

const searchFocused = ref(false);

// ============================================================
// SignalR status indicator
// ============================================================

const connectionStatus = computed(() => signalR.status.value);

const connectionLabel = computed(() => {
  switch (connectionStatus.value) {
    case 'connected':    return 'Live';
    case 'reconnecting': return 'Reconnecting';
    default:             return 'Offline';
  }
});

// ============================================================
// DHT
// ============================================================

const dhtTooltip = computed(() => {
  const s = sessionStore.dhtStatus;
  if (!s) return 'DHT: Unknown';
  const state = s.isRunning ? 'Running' : s.isEnabled ? 'Enabled (starting)' : 'Disabled';
  return `DHT: ${state} · ${s.nodeCount} nodes`;
});

const dhtActive = computed(() => sessionStore.dhtStatus?.isRunning ?? false);

async function handleDhtToggle(): Promise<void> {
  try {
    await dhtApi.toggleDht();
  } catch (err) {
    console.warn('[AppHeader] DHT toggle failed:', err);
  }
}

// ============================================================
// Navigation
// ============================================================

function goSettings(): void {
  router.push({ name: 'settings' });
}
</script>

<template>
  <header class="app-header">
    <!-- Logo -->
    <div class="app-header__logo">
      <img
        :src="isDark ? '/dark_logo.svg' : '/light_logo.svg'"
        alt=""
        aria-hidden="true"
        class="app-header__logo-svg"
      />
      <span class="app-header__logo-text">vTORRENT</span>
    </div>

    <!-- Search -->
    <div class="app-header__search" :class="{ 'app-header__search--focused': searchFocused }">
      <PhMagnifyingGlass class="app-header__search-icon" :size="15" weight="bold" />
      <input
        v-model="torrentStore.searchQuery"
        class="app-header__search-input"
        type="text"
        placeholder="Search torrents…"
        aria-label="Search torrents"
        @focus="searchFocused = true"
        @blur="searchFocused = false"
      />
    </div>

    <!-- Action buttons -->
    <div class="app-header__actions">
      <!-- Add Torrent -->
      <button
        class="app-header__icon-btn"
        title="Add torrent file"
        aria-label="Add torrent file"
        @click="emit('open-add-torrent')"
      >
        <PhPlus :size="18" weight="bold" />
      </button>

      <!-- Add Magnet -->
      <button
        class="app-header__icon-btn"
        title="Add magnet link"
        aria-label="Add magnet link"
        @click="emit('open-add-magnet')"
      >
        <PhLink :size="18" weight="bold" />
      </button>

      <!-- DHT -->
      <button
        class="app-header__icon-btn"
        :class="{ 'app-header__icon-btn--active': dhtActive }"
        :title="dhtTooltip"
        :aria-label="dhtTooltip"
        @click="handleDhtToggle"
      >
        <PhGlobe :size="18" weight="bold" />
      </button>
    </div>

    <!-- Spacer -->
    <div class="app-header__spacer" aria-hidden="true" />

    <!-- Right side -->
    <div class="app-header__right">
      <!-- Remote connection badge -->
      <div v-if="connection.isRemote.value" class="app-header__connection-badge" :title="`Connected to remote server: ${connection.activeProfile.value.host}`">
        <span class="app-header__connection-icon">⬡</span>
        <span class="app-header__connection-label">
          {{ connection.activeProfile.value.name }} — {{ connection.activeProfile.value.host }}
        </span>
      </div>

      <!-- Active profile badge -->
      <div class="app-header__profile" @click.stop="toggleProfileDropdown" :title="profileStore.scheduleEnabled ? 'Schedule active — manage in Settings' : 'Click to switch profile'">
        <span class="app-header__profile-dot" :style="{ background: profileStore.activeProfileColor }" />
        <span class="app-header__profile-name">{{ profileStore.activeProfileName }}</span>
        <span v-if="profileStore.scheduleEnabled" class="app-header__profile-clock">⏱</span>
      </div>

      <!-- Profile dropdown -->
      <div v-if="profileDropdownOpen" class="app-header__profile-dropdown">
        <button
          v-for="profile in profileStore.profiles"
          :key="profile.name"
          class="app-header__profile-option"
          :class="{ 'app-header__profile-option--active': profile.name === profileStore.activeProfileName }"
          @click="selectProfile(profile.name)"
        >
          <span class="app-header__profile-dot" :style="{ background: profile.color }" />
          {{ profile.name }}
        </button>
      </div>

      <!-- SignalR status -->
      <div
        class="app-header__signal"
        :class="`app-header__signal--${connectionStatus}`"
        :title="`SignalR: ${connectionLabel}`"
      >
        <span class="app-header__signal-dot" aria-hidden="true" />
        <span class="app-header__signal-text">{{ connectionLabel }}</span>
      </div>

      <!-- Settings -->
      <button
        class="app-header__icon-btn"
        title="Settings"
        aria-label="Settings"
        @click="goSettings"
      >
        <PhGear :size="18" weight="bold" />
      </button>

      <!-- Mobile hamburger — hidden on desktop via CSS -->
      <button
        class="app-header__icon-btn app-header__hamburger"
        title="Navigation menu"
        aria-label="Open navigation menu"
        @click="emit('toggle-mobile-nav')"
      >
        <PhList :size="18" weight="bold" />
      </button>
    </div>
  </header>
</template>

<style scoped>
.app-header {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  height: var(--header-height);
  z-index: 100;
  display: flex;
  align-items: center;
  gap: var(--spacing-md);
  padding: 0 var(--spacing-lg);
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border);
  box-shadow: 0 1px 0 rgba(0, 0, 0, 0.4);
}

/* ── Logo ──────────────────────────────────────────────────── */
.app-header__logo {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
  user-select: none;
}

.app-header__logo-svg {
  height: 28px;
  width: auto;
}

.app-header__logo-text {
  font-size: 16px;
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.12em;
  line-height: 1;
}

/* ── Search ────────────────────────────────────────────────── */
.app-header__search {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  flex: 1;
  max-width: 400px;
  min-width: 160px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: 0 var(--spacing-md);
  height: 32px;
  transition:
    border-color var(--transition-fast),
    box-shadow var(--transition-fast);
}

.app-header__search--focused {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.app-header__search-icon {
  color: var(--text-tertiary);
  flex-shrink: 0;
}

.app-header__search--focused .app-header__search-icon {
  color: var(--accent-cyan);
}

.app-header__search-input {
  flex: 1;
  background: transparent;
  border: none;
  outline: none;
  color: var(--text-primary);
  font-size: var(--font-sm);
  padding: 0;
  min-width: 0;
}

.app-header__search-input::placeholder {
  color: var(--text-tertiary);
}

/* ── Action buttons ────────────────────────────────────────── */
.app-header__actions {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  flex-shrink: 0;
}

.app-header__icon-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast);
}

.app-header__icon-btn:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.app-header__icon-btn--active {
  color: var(--accent-cyan);
}

.app-header__icon-btn--active:hover {
  color: var(--accent-cyan);
  background: color-mix(in srgb, var(--accent-active) 10%, transparent);
}

/* ── Spacer ────────────────────────────────────────────────── */
.app-header__spacer {
  flex: 1;
}

/* ── Right side ────────────────────────────────────────────── */
.app-header__right {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  flex-shrink: 0;
}

/* Remote connection badge */
.app-header__connection-badge {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.25rem 0.75rem;
  border-radius: 16px;
  background: rgba(79, 195, 247, 0.12);
  border: 1px solid rgba(79, 195, 247, 0.3);
  font-size: var(--font-xs);
  color: #4fc3f7;
  white-space: nowrap;
  cursor: default;
  user-select: none;
}

.app-header__connection-icon {
  font-size: 0.7rem;
  flex-shrink: 0;
}

.app-header__connection-label {
  font-weight: 500;
  letter-spacing: 0.02em;
}

/* SignalR status */
.app-header__signal {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: var(--spacing-xs) var(--spacing-sm);
  border-radius: var(--radius-md);
  background: var(--bg-hover);
  border: 1px solid var(--border);
  cursor: default;
  user-select: none;
}

.app-header__signal-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  flex-shrink: 0;
}

.app-header__signal--connected .app-header__signal-dot {
  background: var(--status-green);
  box-shadow: 0 0 6px var(--status-green);
  animation: pulse-green 2s ease-in-out infinite;
}

.app-header__signal--reconnecting .app-header__signal-dot {
  background: var(--status-orange);
  animation: pulse-orange 1s ease-in-out infinite;
}

.app-header__signal--disconnected .app-header__signal-dot {
  background: var(--status-red);
}

.app-header__signal-text {
  font-size: var(--font-xs);
  font-weight: 500;
  color: var(--text-secondary);
  letter-spacing: 0.02em;
}

.app-header__signal--connected .app-header__signal-text {
  color: var(--status-green);
}

.app-header__signal--reconnecting .app-header__signal-text {
  color: var(--status-orange);
}

.app-header__signal--disconnected .app-header__signal-text {
  color: var(--status-red);
}

/* ── Mobile hamburger ──────────────────────────────────────── */
.app-header__hamburger {
  display: none;
}

@media (max-width: 767px) {
  .app-header__hamburger {
    display: flex;
  }

  /* Hide search bar on mobile — space is too tight */
  .app-header__search {
    display: none;
  }

  /* Tighten horizontal padding */
  .app-header {
    padding: 0 var(--spacing-sm);
    gap: var(--spacing-sm);
  }

  /* Hide DHT button on mobile to save space */
  .app-header__actions {
    display: none;
  }

  /* Hide signal status text on mobile */
  .app-header__signal-text {
    display: none;
  }
}

/* ── Profile badge & dropdown ──────────────────────────────── */
.app-header__profile {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.25rem 0.65rem;
  border-radius: 16px;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.1);
  cursor: pointer;
  font-size: 0.8rem;
  white-space: nowrap;
  position: relative;
}
.app-header__profile-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}
.app-header__profile-name {
  font-weight: 500;
}
.app-header__profile-clock {
  font-size: 0.7rem;
  opacity: 0.7;
}
.app-header__profile-dropdown {
  position: absolute;
  top: calc(100% + 0.5rem);
  right: 0;
  background: var(--bg-card, #1a1a2e);
  border: 1px solid var(--border, #333);
  border-radius: 8px;
  padding: 0.35rem;
  min-width: 160px;
  z-index: 1000;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
}
.app-header__profile-option {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  padding: 0.45rem 0.65rem;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary, #e0e0e0);
  font-size: 0.85rem;
  cursor: pointer;
  text-align: left;
}
.app-header__profile-option:hover {
  background: rgba(255, 255, 255, 0.08);
}
.app-header__profile-option--active {
  background: rgba(79, 195, 247, 0.12);
  color: #4fc3f7;
}

/* ── Animations ────────────────────────────────────────────── */
@keyframes pulse-green {
  0%, 100% { box-shadow: 0 0 4px var(--status-green); }
  50%       { box-shadow: 0 0 10px var(--status-green); }
}

@keyframes pulse-orange {
  0%, 100% { box-shadow: 0 0 4px var(--status-orange); opacity: 1; }
  50%       { box-shadow: 0 0 10px var(--status-orange); opacity: 0.6; }
}
</style>
