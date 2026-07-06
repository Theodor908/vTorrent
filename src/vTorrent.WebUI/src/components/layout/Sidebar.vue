<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import {
  PhCaretLeft,
  PhCaretRight,
  PhArrowDown,
  PhArrowUp,
  PhX,
  PhCheckCircle,
  PhList,
  PhFolder,
  PhTag,
  PhGear,
  PhBell,
  PhPause,
} from '@phosphor-icons/vue';
import { useTorrentStore } from '@/stores/torrentStore';
import * as categoriesApi from '@/api/categories';
import * as tagsApi from '@/api/tags';
import type { Category } from '@/api/categories';
import type { Tag } from '@/api/tags';
import CategoryTagMenu from '@/components/layout/CategoryTagMenu.vue';
import CategoryDialog from '@/components/layout/CategoryDialog.vue';
import TagDialog from '@/components/layout/TagDialog.vue';
import ThemeToggle from '@/components/common/ThemeToggle.vue';

// ============================================================
// Props / Emits
// ============================================================

const props = defineProps<{
  collapsed: boolean;
}>();

const emit = defineEmits<{
  (e: 'update:collapsed', value: boolean): void;
}>();

// ============================================================
// State
// ============================================================

const router = useRouter();
const torrentStore = useTorrentStore();

const categories = computed(() => torrentStore.categories);
const tags = computed(() => torrentStore.tags);
const notificationsEnabled = ref(false);

// ── Context menu state ──────────────────────────────────
const ctxMenuVisible = ref(false);
const ctxMenuPosition = ref({ x: 0, y: 0 });
const ctxMenuItems = ref<Array<{ label: string; action: string; danger?: boolean }>>([]);
const ctxMenuTarget = ref<{ type: 'category' | 'tag'; item: Category | Tag | null }>({ type: 'category', item: null });

// ── Dialog state ────────────────────────────────────────
const categoryDialogVisible = ref(false);
const categoryDialogTarget = ref<Category | null>(null);
const tagDialogVisible = ref(false);
const tagDialogTarget = ref<Tag | null>(null);

// ============================================================
// Data loading
// ============================================================

onMounted(async () => {
  await torrentStore.refreshCategories();
  await torrentStore.refreshTags();
});

// ============================================================
// Sidebar status items
// ============================================================

interface StatusItem {
  key: string | null;
  label: string;
  icon: object;
  count: number;
}

const statusItems = computed((): StatusItem[] => {
  const counts = torrentStore.statusCounts;
  const total = torrentStore.torrents.size;
  return [
    { key: null,           label: 'All',         icon: PhList,        count: total },
    { key: 'Downloading',  label: 'Downloading',  icon: PhArrowDown,   count: counts.downloading },
    { key: 'Seeding',      label: 'Seeding',      icon: PhArrowUp,     count: counts.seeding },
    { key: 'Paused',       label: 'Paused',       icon: PhPause,       count: counts.paused },
    { key: 'Error',        label: 'Errored',      icon: PhX,           count: counts.errored },
    { key: 'Completed',    label: 'Completed',    icon: PhCheckCircle, count: counts.completed },
  ];
});

// ============================================================
// Actions
// ============================================================

function setStatus(key: string | null): void {
  torrentStore.statusFilter = key;
}

function setCategory(name: string | null): void {
  torrentStore.categoryFilter = name;
}

function setTag(name: string | null): void {
  torrentStore.tagFilter = name;
}

function toggleCollapsed(): void {
  emit('update:collapsed', !props.collapsed);
}

function goSettings(): void {
  router.push({ name: 'settings' });
}

function toggleNotifications(): void {
  notificationsEnabled.value = !notificationsEnabled.value;
  if (notificationsEnabled.value && 'Notification' in window) {
    Notification.requestPermission();
  }
}

// ── Context menu handlers ───────────────────────────────

function openCategoryMenu(cat: Category | null, event: MouseEvent): void {
  event.preventDefault();
  ctxMenuTarget.value = { type: 'category', item: cat };
  ctxMenuPosition.value = { x: event.clientX, y: event.clientY };
  ctxMenuItems.value = cat
    ? [
        { label: 'Edit Category', action: 'edit' },
        { label: 'Delete Category', action: 'delete', danger: true },
      ]
    : [{ label: 'Create Category', action: 'create' }];
  ctxMenuVisible.value = true;
}

function openTagMenu(tag: Tag | null, event: MouseEvent): void {
  event.preventDefault();
  ctxMenuTarget.value = { type: 'tag', item: tag };
  ctxMenuPosition.value = { x: event.clientX, y: event.clientY };
  ctxMenuItems.value = tag
    ? [
        { label: 'Edit Tag', action: 'edit' },
        { label: 'Delete Tag', action: 'delete', danger: true },
      ]
    : [{ label: 'Create Tag', action: 'create' }];
  ctxMenuVisible.value = true;
}

function handleCtxAction(action: string): void {
  const { type, item } = ctxMenuTarget.value;

  if (type === 'category') {
    if (action === 'create') {
      categoryDialogTarget.value = null;
      categoryDialogVisible.value = true;
    } else if (action === 'edit' && item) {
      categoryDialogTarget.value = item as Category;
      categoryDialogVisible.value = true;
    } else if (action === 'delete' && item) {
      deleteCategoryDirect((item as Category).id);
    }
  } else {
    if (action === 'create') {
      tagDialogTarget.value = null;
      tagDialogVisible.value = true;
    } else if (action === 'edit' && item) {
      tagDialogTarget.value = item as Tag;
      tagDialogVisible.value = true;
    } else if (action === 'delete' && item) {
      deleteTagDirect((item as Tag).id);
    }
  }
}

async function deleteCategoryDirect(id: number): Promise<void> {
  try {
    await categoriesApi.deleteCategory(id);
    await refreshData();
  } catch { /* handled by toast in future */ }
}

async function deleteTagDirect(id: number): Promise<void> {
  try {
    await tagsApi.deleteTag(id);
    await refreshData();
  } catch { /* handled by toast in future */ }
}

async function refreshData(): Promise<void> {
  await torrentStore.refreshCategories();
  await torrentStore.refreshTags();
}
</script>

<template>
  <aside class="sidebar" :class="{ 'sidebar--collapsed': collapsed }" :aria-label="'Navigation sidebar'">
    <!-- Toggle button -->
    <button
      class="sidebar__toggle"
      :title="collapsed ? 'Expand sidebar' : 'Collapse sidebar'"
      :aria-label="collapsed ? 'Expand sidebar' : 'Collapse sidebar'"
      @click="toggleCollapsed"
    >
      <PhCaretLeft v-if="!collapsed" :size="14" weight="bold" />
      <PhCaretRight v-else :size="14" weight="bold" />
    </button>

    <nav class="sidebar__nav">
      <!-- ── OVERVIEW ─────────────────────────────────────── -->
      <section class="sidebar__section">
        <span v-if="!collapsed" class="sidebar__section-label">OVERVIEW</span>

        <button
          v-for="item in statusItems"
          :key="String(item.key)"
          class="sidebar__item"
          :class="{
            'sidebar__item--active': torrentStore.statusFilter === item.key,
          }"
          :title="collapsed ? item.label : undefined"
          :aria-label="item.label"
          @click="setStatus(item.key)"
        >
          <component :is="item.icon" class="sidebar__item-icon" :size="16" weight="bold" />
          <span v-if="!collapsed" class="sidebar__item-label">{{ item.label }}</span>
          <span v-if="!collapsed" class="sidebar__item-count" :class="{ 'sidebar__item-count--zero': item.count === 0 }">
            {{ item.count }}
          </span>
        </button>
      </section>

      <!-- ── CATEGORIES ─────────────────────────────────── -->
      <section class="sidebar__section">
        <span v-if="!collapsed" class="sidebar__section-label" @contextmenu.prevent="openCategoryMenu(null, $event)">CATEGORIES</span>

        <button
          class="sidebar__item"
          :class="{ 'sidebar__item--active': torrentStore.categoryFilter === null }"
          :title="collapsed ? 'All Categories' : undefined"
          aria-label="All categories"
          @click="setCategory(null)"
          @contextmenu.prevent="openCategoryMenu(null, $event)"
        >
          <PhFolder class="sidebar__item-icon" :size="16" weight="bold" />
          <span v-if="!collapsed" class="sidebar__item-label">All</span>
        </button>

        <button
          v-for="cat in categories"
          :key="cat.id"
          class="sidebar__item"
          :class="{ 'sidebar__item--active': torrentStore.categoryFilter === cat.name }"
          :title="collapsed ? cat.name : undefined"
          :aria-label="cat.name"
          @click="setCategory(cat.name)"
          @contextmenu.prevent="openCategoryMenu(cat, $event)"
        >
          <span
            v-if="cat.color"
            class="sidebar__item-dot"
            :style="{ background: cat.color }"
            aria-hidden="true"
          />
          <PhFolder v-else class="sidebar__item-icon" :size="16" />
          <span v-if="!collapsed" class="sidebar__item-label sidebar__item-label--truncate">
            {{ cat.name }}
          </span>
        </button>
      </section>

      <!-- ── TAGS ─────────────────────────────────────────── -->
      <section class="sidebar__section">
        <span v-if="!collapsed" class="sidebar__section-label" @contextmenu.prevent="openTagMenu(null, $event)">TAGS</span>

        <button
          class="sidebar__item"
          :class="{ 'sidebar__item--active': torrentStore.tagFilter === null }"
          :title="collapsed ? 'All Tags' : undefined"
          aria-label="All tags"
          @click="setTag(null)"
          @contextmenu.prevent="openTagMenu(null, $event)"
        >
          <PhTag class="sidebar__item-icon" :size="16" weight="bold" />
          <span v-if="!collapsed" class="sidebar__item-label">All</span>
        </button>

        <button
          v-for="tag in tags"
          :key="tag.id"
          class="sidebar__item"
          :class="{ 'sidebar__item--active': torrentStore.tagFilter === tag.name }"
          :title="collapsed ? tag.name : undefined"
          :aria-label="tag.name"
          @click="setTag(tag.name)"
          @contextmenu.prevent="openTagMenu(tag, $event)"
        >
          <span
            class="sidebar__item-dot"
            :style="{ background: tag.color ?? 'var(--text-tertiary)' }"
            aria-hidden="true"
          />
          <span v-if="!collapsed" class="sidebar__item-label sidebar__item-label--truncate">
            {{ tag.name }}
          </span>
        </button>
      </section>
    </nav>

    <!-- ── Bottom: settings, notifications, theme ──────── -->
    <div class="sidebar__bottom">
      <!-- Settings -->
      <button
        class="sidebar__item sidebar__item--bottom"
        :title="collapsed ? 'Settings' : undefined"
        aria-label="Settings"
        @click="goSettings"
      >
        <PhGear class="sidebar__item-icon" :size="16" weight="bold" />
        <span v-if="!collapsed" class="sidebar__item-label">Settings</span>
      </button>

      <!-- Notifications -->
      <button
        class="sidebar__item sidebar__item--bottom"
        :class="{ 'sidebar__item--active': notificationsEnabled }"
        :title="collapsed ? (notificationsEnabled ? 'Notifications on' : 'Notifications off') : undefined"
        :aria-label="notificationsEnabled ? 'Disable notifications' : 'Enable notifications'"
        @click="toggleNotifications"
      >
        <PhBell class="sidebar__item-icon" :size="16" weight="bold" />
        <span v-if="!collapsed" class="sidebar__item-label">Notifications</span>
        <span v-if="!collapsed" class="sidebar__notif-toggle" :class="{ 'sidebar__notif-toggle--on': notificationsEnabled }">
          <span class="sidebar__notif-thumb" />
        </span>
      </button>

      <!-- Theme toggle -->
      <div v-if="!collapsed" class="sidebar__theme">
        <ThemeToggle />
      </div>
    </div>

    <!-- Context menu -->
    <CategoryTagMenu
      :visible="ctxMenuVisible"
      :position="ctxMenuPosition"
      :items="ctxMenuItems"
      @close="ctxMenuVisible = false"
      @action="handleCtxAction"
    />

    <!-- Category dialog -->
    <CategoryDialog
      :visible="categoryDialogVisible"
      :category="categoryDialogTarget"
      @close="categoryDialogVisible = false"
      @saved="refreshData"
    />

    <!-- Tag dialog -->
    <TagDialog
      :visible="tagDialogVisible"
      :tag="tagDialogTarget"
      @close="tagDialogVisible = false"
      @saved="refreshData"
    />
  </aside>
</template>

<style scoped>
.sidebar {
  position: relative;
  display: flex;
  flex-direction: column;
  width: var(--sidebar-width);
  height: 100%;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border);
  flex-shrink: 0;
  transition: width var(--transition-normal);
  overflow: hidden;
}

.sidebar--collapsed {
  width: var(--sidebar-collapsed-width);
}

/* ── Toggle ────────────────────────────────────────────────── */
.sidebar__toggle {
  position: absolute;
  top: var(--spacing-md);
  right: -1px;
  z-index: 10;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: var(--radius-sm) 0 0 var(--radius-sm);
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-right: none;
  color: var(--text-tertiary);
  transition: color var(--transition-fast), background-color var(--transition-fast);
  cursor: pointer;
}

.sidebar__toggle:hover {
  color: var(--accent-active);
  background: var(--bg-hover);
}

/* ── Nav ───────────────────────────────────────────────────── */
.sidebar__nav {
  flex: 1;
  overflow-y: auto;
  overflow-x: hidden;
  padding: var(--spacing-xl) 0 var(--spacing-md);
  scrollbar-width: thin;
  scrollbar-color: var(--border) transparent;
}

/* ── Sections ──────────────────────────────────────────────── */
.sidebar__section {
  margin-bottom: var(--spacing-xl);
}

.sidebar__section-label {
  display: block;
  padding: 0 var(--spacing-lg) var(--spacing-xs);
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-tertiary);
  letter-spacing: 0.1em;
  white-space: nowrap;
}

/* ── Items ─────────────────────────────────────────────────── */
.sidebar__item {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  width: 100%;
  padding: 6px var(--spacing-lg);
  border-radius: 0;
  color: var(--text-secondary);
  font-size: var(--font-sm);
  transition:
    color var(--transition-fast),
    background-color var(--transition-fast);
  cursor: pointer;
  position: relative;
  white-space: nowrap;
  text-align: left;
  /* Active indicator — invisible by default */
  border-left: 2px solid transparent;
}

.sidebar--collapsed .sidebar__item {
  padding: 8px 0;
  justify-content: center;
}

.sidebar__item:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.sidebar__item--active {
  color: var(--text-primary);
  border-left-color: var(--accent-active);
  background: color-mix(in srgb, var(--accent-active) 6%, transparent);
}

.sidebar__item--active .sidebar__item-icon {
  color: var(--accent-active);
}

/* ── Item parts ────────────────────────────────────────────── */
.sidebar__item-icon {
  flex-shrink: 0;
  color: inherit;
}

.sidebar__item-label {
  flex: 1;
  min-width: 0;
}

.sidebar__item-label--truncate {
  overflow: hidden;
  text-overflow: ellipsis;
}

.sidebar__item-count {
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-tertiary);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 0 6px;
  min-width: 20px;
  text-align: center;
  flex-shrink: 0;
}

.sidebar__item--active .sidebar__item-count {
  color: var(--accent-active);
  border-color: color-mix(in srgb, var(--accent-active) 30%, transparent);
  background: color-mix(in srgb, var(--accent-active) 8%, transparent);
}

.sidebar__item-count--zero {
  color: var(--text-tertiary);
  border-color: transparent;
  background: transparent;
}

.sidebar__item-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

/* ── Bottom ────────────────────────────────────────────────── */
.sidebar__bottom {
  border-top: 1px solid var(--border);
  padding: var(--spacing-sm) 0;
}

.sidebar__item--bottom {
  color: var(--text-secondary);
}

/* Notifications mini-toggle */
.sidebar__notif-toggle {
  position: relative;
  display: inline-flex;
  align-items: center;
  width: 28px;
  height: 16px;
  border-radius: 8px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  flex-shrink: 0;
  transition: background-color var(--transition-fast), border-color var(--transition-fast);
}

.sidebar__notif-toggle--on {
  background: var(--accent-active);
  border-color: var(--accent-active);
}

.sidebar__notif-thumb {
  position: absolute;
  left: 2px;
  width: 10px;
  height: 10px;
  border-radius: 50%;
  background: var(--text-tertiary);
  transition: transform var(--transition-fast), background-color var(--transition-fast);
}

.sidebar__notif-toggle--on .sidebar__notif-thumb {
  transform: translateX(12px);
  background: var(--bg-primary);
}

.sidebar__theme {
  padding: var(--spacing-xs) var(--spacing-sm);
}

/* ── Tablet: force icon-only collapsed mode ─────────────── */
@media (max-width: 1279px) and (min-width: 768px) {
  .sidebar {
    width: var(--sidebar-collapsed-width);
  }

  /* Hide text labels, counts, section labels, and bottom text */
  .sidebar__section-label,
  .sidebar__item-label,
  .sidebar__item-count,
  .sidebar__notif-toggle,
  .sidebar__theme {
    display: none;
  }

  /* Center icons */
  .sidebar__item {
    padding: 8px 0;
    justify-content: center;
  }

  /* Hide the expand/collapse toggle — sidebar is always collapsed on tablet */
  .sidebar__toggle {
    display: none;
  }
}

/* ── Mobile: hide sidebar entirely (MobileNav takes over) ── */
@media (max-width: 767px) {
  .sidebar {
    display: none;
  }
}
</style>
