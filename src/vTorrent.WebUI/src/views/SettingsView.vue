<script setup lang="ts">
// SettingsView.vue — Top-level settings page with 8-tab vertical sidebar layout.
// Loads settings on mount, shows vertical tab nav + content panel, saves on demand.
// Tracks dirty state so Save is only enabled when something has changed.

import { ref, computed, onMounted } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { PhArrowLeft, PhFloppyDisk, PhSpinner } from '@phosphor-icons/vue';
import { useSettingsStore } from '@/stores/settingsStore';
import { useToast } from '@/composables/useToast';
import type { GlobalSettings } from '@/types/settings';

import DownloadsTab from '@/components/settings/tabs/DownloadsTab.vue';
import ConnectionTab from '@/components/settings/tabs/ConnectionTab.vue';
import SpeedTab from '@/components/settings/tabs/SpeedTab.vue';
import BitTorrentTab from '@/components/settings/tabs/BitTorrentTab.vue';
import WebSeedsTab from '@/components/settings/tabs/WebSeedsTab.vue';
import WebUITab from '@/components/settings/tabs/WebUITab.vue';
import PrivacyTab from '@/components/settings/tabs/PrivacyTab.vue';
import AdvancedTab from '@/components/settings/tabs/AdvancedTab.vue';
import ServerProfilesTab from '@/components/settings/tabs/ServerProfilesTab.vue';
import ProfilesScheduleTab from '@/components/settings/tabs/ProfilesScheduleTab.vue';

// ============================================================
// Router / stores / composables
// ============================================================

const router = useRouter();
const route = useRoute();
const settingsStore = useSettingsStore();
const { showToast } = useToast();

// ============================================================
// Tab state
// ============================================================

type MainTab = 'downloads' | 'connection' | 'speed' | 'bittorrent' | 'webseeds' | 'webui' | 'privacy' | 'advanced' | 'profiles' | 'settingProfiles';

const tabs: { id: MainTab; label: string }[] = [
  { id: 'downloads',  label: 'Downloads'       },
  { id: 'connection', label: 'Connection'      },
  { id: 'speed',      label: 'Speed'           },
  { id: 'bittorrent', label: 'BitTorrent'      },
  { id: 'webseeds',   label: 'Web Seeds'       },
  { id: 'webui',      label: 'Web UI'          },
  { id: 'privacy',    label: 'Privacy'         },
  { id: 'advanced',   label: 'Advanced'        },
  { id: 'profiles',        label: 'Server Profiles'    },
  { id: 'settingProfiles', label: 'Profiles & Schedule' },
];

const activeTab = ref<MainTab>('downloads');

// ============================================================
// Settings state
// localSettings holds a working copy so we can detect dirty state
// without mutating the store directly.
// ============================================================

const localSettings = ref<GlobalSettings | null>(null);

/** True while save is in-flight (separate from the load indicator). */
const saving = ref(false);

/** True when localSettings diverges from the last-loaded store copy. */
const isDirty = computed<boolean>(() => {
  if (!localSettings.value || !settingsStore.globalSettings) return false;
  return JSON.stringify(localSettings.value) !== JSON.stringify(settingsStore.globalSettings);
});

// ============================================================
// Lifecycle
// ============================================================

onMounted(async () => {
  // Honour ?tab= query parameter for direct deep-links (e.g., from login screen).
  if (route.query.tab === 'profiles') {
    activeTab.value = 'profiles';
  }

  try {
    await settingsStore.loadSettings();
    // Deep-clone so localSettings is independent of the store ref.
    if (settingsStore.globalSettings) {
      localSettings.value = JSON.parse(JSON.stringify(settingsStore.globalSettings)) as GlobalSettings;
    }
  } catch {
    showToast('Failed to load settings.', 'error');
  }
});

// ============================================================
// Handlers
// ============================================================

function goBack(): void {
  router.push({ name: 'dashboard' });
}

function onSettingsUpdate(updated: GlobalSettings): void {
  localSettings.value = updated;
}

async function handleSave(): Promise<void> {
  if (!localSettings.value || !isDirty.value) return;
  saving.value = true;
  try {
    await settingsStore.saveSettings(localSettings.value);
    // Re-clone from store so dirty state resets cleanly.
    localSettings.value = JSON.parse(JSON.stringify(settingsStore.globalSettings)) as GlobalSettings;
    showToast('Settings saved successfully.', 'success');
  } catch {
    showToast('Failed to save settings. Please try again.', 'error');
  } finally {
    saving.value = false;
  }
}

function handleCancel(): void {
  // Reset local copy to the last saved state.
  if (settingsStore.globalSettings) {
    localSettings.value = JSON.parse(JSON.stringify(settingsStore.globalSettings)) as GlobalSettings;
  }
}
</script>

<template>
  <div class="settings-view">
    <!-- ── Header ─────────────────────────────────────────────── -->
    <header class="settings-view__header">
      <button
        class="settings-view__back"
        title="Back to dashboard"
        aria-label="Back to dashboard"
        @click="goBack"
      >
        <PhArrowLeft :size="18" weight="bold" />
        <span>Back</span>
      </button>

      <h1 class="settings-view__title">Settings</h1>

      <!-- Spacer -->
      <div class="settings-view__header-spacer" aria-hidden="true" />
    </header>

    <!-- ── Loading skeleton ───────────────────────────────────── -->
    <div v-if="settingsStore.isLoading && !localSettings" class="settings-view__loading" aria-live="polite" aria-busy="true">
      <PhSpinner class="settings-view__spinner" :size="28" weight="bold" />
      <span>Loading settings...</span>
    </div>

    <!-- ── Main content: sidebar + panel ──────────────────────── -->
    <div v-else-if="localSettings" class="settings-view__layout">
      <!-- Vertical sidebar tab nav -->
      <nav class="settings-view__sidebar" role="tablist" aria-label="Settings sections">
        <button
          v-for="tab in tabs"
          :key="tab.id"
          class="settings-view__tab"
          :class="{ 'settings-view__tab--active': activeTab === tab.id }"
          role="tab"
          :aria-selected="activeTab === tab.id"
          @click="activeTab = tab.id"
        >
          {{ tab.label }}
        </button>
      </nav>

      <!-- Tab content panel -->
      <div class="settings-view__panel" role="tabpanel">
        <DownloadsTab v-if="activeTab === 'downloads'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <ConnectionTab v-if="activeTab === 'connection'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <SpeedTab v-if="activeTab === 'speed'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <BitTorrentTab v-if="activeTab === 'bittorrent'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <WebSeedsTab v-if="activeTab === 'webseeds'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <WebUITab v-if="activeTab === 'webui'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <PrivacyTab v-if="activeTab === 'privacy'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <AdvancedTab v-if="activeTab === 'advanced'" :settings="localSettings" @update:settings="onSettingsUpdate" />
        <ServerProfilesTab v-if="activeTab === 'profiles'" />
        <ProfilesScheduleTab v-if="activeTab === 'settingProfiles'" />
      </div>
    </div>

    <!-- ── Error state ────────────────────────────────────────── -->
    <div v-else class="settings-view__error" role="alert">
      <p>Settings could not be loaded. Please reload the page.</p>
    </div>

    <!-- ── Bottom action bar ──────────────────────────────────── -->
    <footer class="settings-view__footer">
      <button
        class="settings-view__btn settings-view__btn--secondary"
        :disabled="!isDirty || saving"
        @click="handleCancel"
      >
        Cancel
      </button>

      <button
        class="settings-view__btn settings-view__btn--primary"
        :disabled="!isDirty || saving"
        :aria-busy="saving"
        @click="handleSave"
      >
        <PhSpinner v-if="saving" class="settings-view__btn-spinner" :size="14" weight="bold" />
        <PhFloppyDisk v-else :size="15" weight="bold" />
        <span>{{ saving ? 'Saving...' : 'Save' }}</span>
      </button>
    </footer>
  </div>
</template>

<style scoped>
/* ── Page shell ────────────────────────────────────────────── */
.settings-view {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
  min-height: 0;
  max-width: 1100px;
  width: 100%;
}

/* ── Header ─────────────────────────────────────────────────── */
.settings-view__header {
  display: flex;
  align-items: center;
  gap: var(--spacing-lg);
}

.settings-view__back {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: var(--spacing-xs) var(--spacing-md);
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  font-size: var(--font-sm);
  font-weight: 600;
  cursor: pointer;
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast);
}

.settings-view__back:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.settings-view__title {
  font-size: var(--font-xl);
  font-weight: 800;
  color: var(--text-primary);
  letter-spacing: -0.01em;
}

.settings-view__header-spacer {
  flex: 1;
}

/* ── Loading ────────────────────────────────────────────────── */
.settings-view__loading {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--spacing-md);
  padding: var(--spacing-2xl);
  color: var(--text-secondary);
  font-size: var(--font-md);
}

.settings-view__spinner {
  color: var(--accent-cyan);
  animation: spin 0.8s linear infinite;
}

/* ── Error ──────────────────────────────────────────────────── */
.settings-view__error {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--spacing-2xl);
  color: var(--status-red);
  font-size: var(--font-md);
}

/* ── Layout: sidebar + panel ────────────────────────────────── */
.settings-view__layout {
  display: grid;
  grid-template-columns: 200px 1fr;
  gap: var(--spacing-xl);
  flex: 1;
  min-height: 0;
}

/* ── Vertical sidebar ───────────────────────────────────────── */
.settings-view__sidebar {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.settings-view__tab {
  padding: var(--spacing-sm) var(--spacing-lg);
  text-align: left;
  font-size: var(--font-md);
  font-weight: 500;
  color: var(--text-secondary);
  background: transparent;
  border: none;
  border-radius: var(--radius-md);
  cursor: pointer;
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast);
  white-space: nowrap;
}

.settings-view__tab:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.settings-view__tab--active {
  color: var(--accent-active);
  background: color-mix(in srgb, var(--accent-active) 8%, transparent);
}

.settings-view__tab--active:hover {
  color: var(--accent-active);
  background: color-mix(in srgb, var(--accent-active) 12%, transparent);
}

/* ── Panel ──────────────────────────────────────────────────── */
.settings-view__panel {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-xl);
  overflow-y: auto;
  /* Allow the panel to grow but cap its height */
  flex: 1;
  min-height: 0;
}

/* ── Footer action bar ──────────────────────────────────────── */
.settings-view__footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--spacing-md);
  padding: var(--spacing-md) 0;
  border-top: 1px solid var(--border);
}

/* ── Buttons ────────────────────────────────────────────────── */
.settings-view__btn {
  display: inline-flex;
  align-items: center;
  gap: var(--spacing-xs);
  height: 38px;
  padding: 0 var(--spacing-xl);
  border-radius: var(--radius-md);
  font-size: var(--font-md);
  font-weight: 700;
  cursor: pointer;
  transition:
    background-color var(--transition-fast),
    opacity var(--transition-fast),
    box-shadow var(--transition-fast),
    transform var(--transition-fast);
  border: 1px solid transparent;
}

.settings-view__btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
  transform: none !important;
}

.settings-view__btn--primary {
  background: var(--accent-cyan);
  color: var(--bg-primary);
  box-shadow: 0 2px 12px color-mix(in srgb, var(--accent-active) 25%, transparent);
}

.settings-view__btn--primary:hover:not(:disabled) {
  filter: brightness(1.1);
  box-shadow: 0 4px 20px color-mix(in srgb, var(--accent-active) 40%, transparent);
  transform: translateY(-1px);
}

.settings-view__btn--primary:active:not(:disabled) {
  transform: translateY(0);
  box-shadow: 0 2px 8px color-mix(in srgb, var(--accent-active) 20%, transparent);
}

.settings-view__btn--secondary {
  background: var(--bg-hover);
  color: var(--text-primary);
  border-color: var(--border);
}

.settings-view__btn--secondary:hover:not(:disabled) {
  background: var(--bg-card);
  border-color: var(--text-tertiary);
}

.settings-view__btn-spinner {
  animation: spin 0.8s linear infinite;
}

/* ── Animations ─────────────────────────────────────────────── */
@keyframes spin {
  to { transform: rotate(360deg); }
}

/* ── Responsive ─────────────────────────────────────────────── */
@media (max-width: 768px) {
  .settings-view__layout {
    grid-template-columns: 1fr;
  }

  .settings-view__sidebar {
    flex-direction: row;
    flex-wrap: wrap;
    gap: var(--spacing-xs);
  }

  .settings-view__tab {
    padding: var(--spacing-xs) var(--spacing-md);
    font-size: var(--font-sm);
  }
}

@media (max-width: 600px) {
  .settings-view__panel {
    padding: var(--spacing-md);
  }
}
</style>
