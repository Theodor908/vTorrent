<script setup lang="ts">
import { ref, defineAsyncComponent, watch, onMounted } from 'vue';
import { useAuth } from './composables/useAuth';
import { useSignalR } from './composables/useSignalR';
import { getAccessToken } from './api/client';
import { useTorrentStore } from './stores/torrentStore';
import { useSessionStore } from './stores/sessionStore';
import { useProfileStore } from './stores/profileStore';

// Layout components loaded asynchronously — will show nothing until they exist.
const AppHeader = defineAsyncComponent(() => import('@/components/layout/AppHeader.vue'));
const Sidebar = defineAsyncComponent(() => import('@/components/layout/Sidebar.vue'));
const MobileNav = defineAsyncComponent(() => import('@/components/layout/MobileNav.vue'));
const Toast = defineAsyncComponent(() => import('@/components/common/Toast.vue'));
const AddTorrentModal = defineAsyncComponent(
  () => import('@/components/torrents/AddTorrentModal.vue'),
);

const { isAuthenticated, initialize } = useAuth();
const signalR = useSignalR();
const torrentStore = useTorrentStore();
const sessionStore = useSessionStore();
const profileStore = useProfileStore();

// ── App shell state ──────────────────────────────────────────

/** Whether the sidebar is collapsed (persisted to localStorage). */
const sidebarCollapsed = ref(localStorage.getItem('vtorrent-sidebar-collapsed') === 'true');

/** Whether the mobile slide-in nav is open. */
const mobileNavOpen = ref(false);

// ── Add Torrent Modal ─────────────────────────────────────────

type AddModalTab = 'file' | 'magnet';

const addModalVisible = ref(false);
const addModalTab = ref<AddModalTab>('file');

function openAddTorrent(): void {
  addModalTab.value = 'file';
  addModalVisible.value = true;
}

function openAddMagnet(): void {
  addModalTab.value = 'magnet';
  addModalVisible.value = true;
}

function closeAddModal(): void {
  addModalVisible.value = false;
}

watch(sidebarCollapsed, (val) => {
  localStorage.setItem('vtorrent-sidebar-collapsed', String(val));
});

// ── SignalR ──────────────────────────────────────────────────

/** Token factory passed to SignalR — reads the in-memory access token. */
function tokenFactory(): string | null {
  return getAccessToken();
}

/** Connect SignalR hub. Errors are non-fatal — the app still works without it. */
async function connectSignalR(): Promise<void> {
  try {
    await signalR.connect(tokenFactory);
  } catch (err) {
    console.warn('[App] SignalR connect failed:', err);
  }
}

onMounted(async () => {
  // 1. Check local-access bypass and attempt silent token refresh.
  const authenticated = await initialize();

  // 2. If authenticated after init, hydrate stores via REST then connect SignalR.
  if (authenticated) {
    // Hydrate stores via REST before SignalR connects — ensures
    // the UI has data immediately, even if WebSocket fails.
    await Promise.allSettled([
      torrentStore.loadInitialState(),
      sessionStore.loadInitialState(),
      profileStore.loadProfiles(),
      profileStore.loadActiveState(),
    ]);

    await connectSignalR();
  }
});

// 3. React to auth state changes triggered by login/logout after mount.
watch(isAuthenticated, async (authenticated, wasAuthenticated) => {
  if (authenticated && !wasAuthenticated) {
    // User just logged in — hydrate stores then connect.
    await Promise.allSettled([
      torrentStore.loadInitialState(),
      sessionStore.loadInitialState(),
      profileStore.loadProfiles(),
      profileStore.loadActiveState(),
    ]);
    await connectSignalR();
  } else if (!authenticated && wasAuthenticated) {
    // User just logged out — disconnect.
    await signalR.disconnect();
  }
});
</script>

<template>
  <div v-if="isAuthenticated" class="app-layout">
    <AppHeader
      @open-add-torrent="openAddTorrent"
      @open-add-magnet="openAddMagnet"
      @toggle-mobile-nav="mobileNavOpen = !mobileNavOpen"
    />

    <div class="app-content">
      <Sidebar
        v-model:collapsed="sidebarCollapsed"
      />
      <main class="main-content">
        <router-view />
      </main>
    </div>

    <MobileNav v-model:is-open="mobileNavOpen" />

    <!-- Global toast notifications -->
    <Toast />

    <!-- Add Torrent Modal — global, outside router-view -->
    <AddTorrentModal
      :visible="addModalVisible"
      :initial-tab="addModalTab"
      @close="closeAddModal"
    />
  </div>
  <router-view v-else />
</template>

<style scoped>
.app-layout {
  display: flex;
  flex-direction: column;
  height: 100vh;
  padding-top: var(--header-height);
}

.app-content {
  display: flex;
  flex-direction: row;
  flex: 1;
  overflow: hidden;
}

.main-content {
  flex: 1;
  overflow-y: auto;
  padding: var(--spacing-lg);
}

/* ── Responsive ─────────────────────────────────────────── */
@media (max-width: 767px) {
  .main-content {
    padding: var(--spacing-sm);
  }
}
</style>
