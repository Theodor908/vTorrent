<script setup lang="ts">
// AddTorrentModal.vue — Modal for adding torrents via file upload or magnet link.
// Two-panel layout: left panel for settings, right panel for upload/magnet + file tree.
// Post-add workflow: add torrent → set category → set tags → show toast → close.

import { ref, computed, watch, onMounted, onUnmounted } from 'vue';
import { PhX, PhUpload, PhLink, PhWarning } from '@phosphor-icons/vue';
import { addTorrentFile, addMagnet, setTorrentCategory, setTorrentTags } from '../../api/torrents';
import { getCategories, type Category } from '../../api/categories';
import { getTags, type Tag } from '../../api/tags';
import { useToast } from '../../composables/useToast';
import { formatBytes } from '../../utils/format';
import FileTree from './FileTree.vue';
import type { FileEntry } from './FileTree.vue';

// ============================================================
// Props & Emits
// ============================================================

const props = withDefaults(
  defineProps<{
    visible: boolean;
    initialTab?: 'file' | 'magnet';
  }>(),
  {
    initialTab: 'file',
  },
);

const emit = defineEmits<{
  (e: 'close'): void;
}>();

// ============================================================
// Toast
// ============================================================

const { showToast } = useToast();

// ============================================================
// Tab state
// ============================================================

type Tab = 'file' | 'magnet';

const activeTab = ref<Tab>(props.initialTab);

watch(
  () => props.initialTab,
  (tab) => {
    activeTab.value = tab;
  },
);

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      activeTab.value = props.initialTab;
      resetForm();
      loadCategoriesAndTags();
    }
  },
);

// ============================================================
// Categories & Tags
// ============================================================

const categories = ref<Category[]>([]);
const tags = ref<Tag[]>([]);

async function loadCategoriesAndTags(): Promise<void> {
  try {
    [categories.value, tags.value] = await Promise.all([getCategories(), getTags()]);
  } catch (err) {
    console.warn('[AddTorrentModal] Failed to load categories/tags:', err);
  }
}

onMounted(() => {
  if (props.visible) {
    loadCategoriesAndTags();
  }
});

// ============================================================
// Form state
// ============================================================

const savePath = ref('');
const selectedCategoryId = ref<number | null>(null);
const selectedTagIds = ref<Set<number>>(new Set());
const startTorrent = ref(true);
const addToTopOfQueue = ref(false);
const sequentialDownload = ref(false);
const firstLastPiecePriority = ref(false);

// File tab state
const selectedFile = ref<File | null>(null);
const isDragOver = ref(false);
const fileError = ref<string | null>(null);

// Magnet tab state
const magnetUri = ref('');

// Submission state
const isSubmitting = ref(false);

// File tree (TODO: populate from server after torrent parse — starts empty)
const fileTreeFiles = ref<FileEntry[]>([]);
const fileTreeSelections = ref<number[]>([]);
const fileTreePriorities = ref<{ index: number; priority: number }[]>([]);

function resetForm(): void {
  savePath.value = '';
  selectedCategoryId.value = null;
  selectedTagIds.value = new Set();
  startTorrent.value = true;
  addToTopOfQueue.value = false;
  sequentialDownload.value = false;
  firstLastPiecePriority.value = false;
  selectedFile.value = null;
  isDragOver.value = false;
  fileError.value = null;
  magnetUri.value = '';
  isSubmitting.value = false;
  fileTreeFiles.value = [];
  fileTreeSelections.value = [];
  fileTreePriorities.value = [];
}

// ============================================================
// Category selection — auto-fill save path if category has one
// ============================================================

function handleCategoryChange(event: Event): void {
  const value = (event.target as HTMLSelectElement).value;
  selectedCategoryId.value = value === '' ? null : parseInt(value, 10);

  if (selectedCategoryId.value !== null) {
    const cat = categories.value.find((c) => c.id === selectedCategoryId.value);
    if (cat?.savePath) {
      savePath.value = cat.savePath;
    }
  }
}

// ============================================================
// Tag toggle
// ============================================================

function toggleTag(id: number): void {
  if (selectedTagIds.value.has(id)) {
    selectedTagIds.value.delete(id);
  } else {
    selectedTagIds.value.add(id);
  }
}

// ============================================================
// File upload
// ============================================================

const MAX_FILE_SIZE = 10 * 1024 * 1024; // 10MB

function handleFileSelect(event: Event): void {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (file) validateAndSetFile(file);
  input.value = '';
}

function handleDrop(event: DragEvent): void {
  isDragOver.value = false;
  const file = event.dataTransfer?.files?.[0];
  if (file) validateAndSetFile(file);
}

function handleDragOver(event: DragEvent): void {
  isDragOver.value = true;
  event.preventDefault();
}

function handleDragLeave(): void {
  isDragOver.value = false;
}

function validateAndSetFile(file: File): void {
  fileError.value = null;
  if (!file.name.toLowerCase().endsWith('.torrent')) {
    fileError.value = 'Only .torrent files are accepted.';
    return;
  }
  if (file.size > MAX_FILE_SIZE) {
    fileError.value = `File exceeds the 10 MB limit (${formatBytes(file.size)}).`;
    return;
  }
  selectedFile.value = file;
}

function clearFile(): void {
  selectedFile.value = null;
  fileError.value = null;
}

const fileInputRef = ref<HTMLInputElement | null>(null);

function openFilePicker(): void {
  fileInputRef.value?.click();
}

// ============================================================
// Can submit
// ============================================================

const canSubmit = computed(() => {
  if (isSubmitting.value) return false;
  if (activeTab.value === 'file') return selectedFile.value !== null;
  return magnetUri.value.trim().startsWith('magnet:');
});

// ============================================================
// Submit
// ============================================================

async function handleSubmit(): Promise<void> {
  if (!canSubmit.value) return;
  isSubmitting.value = true;

  try {
    const options = {
      savePath: savePath.value.trim() || null,
      startImmediately: startTorrent.value,
      addToTopOfQueue: addToTopOfQueue.value,
      sequentialDownload: sequentialDownload.value,
      firstLastPiecePriority: firstLastPiecePriority.value,
    };

    let infoHash: string;

    if (activeTab.value === 'file' && selectedFile.value) {
      infoHash = await addTorrentFile(selectedFile.value, options);
    } else {
      infoHash = await addMagnet(magnetUri.value.trim(), options);
    }

    // Post-add: category
    if (selectedCategoryId.value !== null) {
      try {
        await setTorrentCategory(infoHash, selectedCategoryId.value);
      } catch (err) {
        console.warn('[AddTorrentModal] Failed to set category:', err);
      }
    }

    // Post-add: tags
    if (selectedTagIds.value.size > 0) {
      try {
        await setTorrentTags(infoHash, [...selectedTagIds.value]);
      } catch (err) {
        console.warn('[AddTorrentModal] Failed to set tags:', err);
      }
    }

    showToast('Torrent added successfully.', 'success');
    emit('close');
  } catch (err) {
    console.error('[AddTorrentModal] Submit failed:', err);
    showToast('Failed to add torrent. Please check the file or link and try again.', 'error');
  } finally {
    isSubmitting.value = false;
  }
}

// ============================================================
// Escape key close
// ============================================================

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && props.visible) {
    emit('close');
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleKeydown);
});

onUnmounted(() => {
  document.removeEventListener('keydown', handleKeydown);
});
</script>

<template>
  <Teleport to="body">
    <Transition name="modal-fade">
      <div v-if="visible" class="modal-overlay" aria-modal="true" role="dialog" aria-label="Add Torrent" @click.self="emit('close')">
        <div class="modal">
          <!-- ── Header ──────────────────────────────────────── -->
          <header class="modal__header">
            <h2 class="modal__title">Add Torrent</h2>
            <button class="modal__close" aria-label="Close" @click="emit('close')">
              <PhX :size="16" weight="bold" />
            </button>
          </header>

          <!-- ── Tabs ───────────────────────────────────────── -->
          <nav class="modal__tabs" role="tablist">
            <button
              class="modal__tab"
              :class="{ 'modal__tab--active': activeTab === 'file' }"
              role="tab"
              :aria-selected="activeTab === 'file'"
              @click="activeTab = 'file'"
            >
              <PhUpload :size="13" weight="bold" />
              File Upload
            </button>
            <button
              class="modal__tab"
              :class="{ 'modal__tab--active': activeTab === 'magnet' }"
              role="tab"
              :aria-selected="activeTab === 'magnet'"
              @click="activeTab = 'magnet'"
            >
              <PhLink :size="13" weight="bold" />
              Magnet Link
            </button>
          </nav>

          <!-- ── Body ───────────────────────────────────────── -->
          <div class="modal__body">
            <!-- Left panel: Settings -->
            <aside class="modal__left">
              <div class="settings-panel">
                <!-- Save Path -->
                <div class="form-group">
                  <label class="form-label" for="atm-save-path">Save Path</label>
                  <input
                    id="atm-save-path"
                    v-model="savePath"
                    class="form-input"
                    type="text"
                    placeholder="/default/save/path"
                    aria-label="Save path"
                  />
                </div>

                <!-- Category -->
                <div class="form-group">
                  <label class="form-label" for="atm-category">Category</label>
                  <select
                    id="atm-category"
                    class="form-select"
                    :value="selectedCategoryId ?? ''"
                    aria-label="Category"
                    @change="handleCategoryChange"
                  >
                    <option value="">None</option>
                    <option
                      v-for="cat in categories"
                      :key="cat.id"
                      :value="cat.id"
                    >
                      {{ cat.name }}
                    </option>
                  </select>
                </div>

                <!-- Tags -->
                <div v-if="tags.length > 0" class="form-group">
                  <span class="form-label">Tags</span>
                  <div class="tags-wrap">
                    <button
                      v-for="tag in tags"
                      :key="tag.id"
                      class="tag-btn"
                      :class="{ 'tag-btn--active': selectedTagIds.has(tag.id) }"
                      :style="tag.color ? { '--tag-color': tag.color } : {}"
                      type="button"
                      @click="toggleTag(tag.id)"
                    >
                      {{ tag.name }}
                    </button>
                  </div>
                </div>

                <!-- Checkboxes -->
                <div class="form-group">
                  <span class="form-label">Options</span>
                  <div class="options-list">
                    <label class="checkbox-label">
                      <input v-model="startTorrent" type="checkbox" class="form-checkbox" />
                      Start Torrent
                    </label>
                    <label class="checkbox-label">
                      <input v-model="addToTopOfQueue" type="checkbox" class="form-checkbox" />
                      Add to Top of Queue
                    </label>
                    <label class="checkbox-label">
                      <input v-model="sequentialDownload" type="checkbox" class="form-checkbox" />
                      Sequential Download
                    </label>
                    <label class="checkbox-label">
                      <input v-model="firstLastPiecePriority" type="checkbox" class="form-checkbox" />
                      First/Last Piece Priority
                    </label>
                  </div>
                </div>
              </div>
            </aside>

            <!-- Right panel -->
            <div class="modal__right">
              <!-- File Upload tab -->
              <template v-if="activeTab === 'file'">
                <!-- Hidden file input -->
                <input
                  ref="fileInputRef"
                  type="file"
                  accept=".torrent"
                  class="hidden-input"
                  aria-hidden="true"
                  @change="handleFileSelect"
                />

                <!-- Drop zone -->
                <div
                  class="drop-zone"
                  :class="{
                    'drop-zone--active': isDragOver,
                    'drop-zone--selected': selectedFile !== null,
                  }"
                  role="button"
                  tabindex="0"
                  aria-label="Click or drag a torrent file here"
                  @click="openFilePicker"
                  @keydown.enter="openFilePicker"
                  @keydown.space.prevent="openFilePicker"
                  @dragover.prevent="handleDragOver"
                  @dragleave="handleDragLeave"
                  @drop.prevent="handleDrop"
                >
                  <template v-if="selectedFile">
                    <PhUpload class="drop-zone__icon drop-zone__icon--done" :size="28" weight="bold" />
                    <span class="drop-zone__filename">{{ selectedFile.name }}</span>
                    <span class="drop-zone__size">{{ formatBytes(selectedFile.size) }}</span>
                    <button
                      class="drop-zone__clear"
                      type="button"
                      aria-label="Remove selected file"
                      @click.stop="clearFile"
                    >
                      <PhX :size="12" weight="bold" /> Remove
                    </button>
                  </template>
                  <template v-else>
                    <PhUpload class="drop-zone__icon" :size="28" weight="bold" />
                    <span class="drop-zone__primary">
                      {{ isDragOver ? 'Drop file here' : 'Click to browse or drag & drop' }}
                    </span>
                    <span class="drop-zone__hint">Accepts .torrent files up to 10 MB</span>
                  </template>
                </div>

                <!-- File error -->
                <div v-if="fileError" class="field-error">
                  <PhWarning :size="13" weight="bold" />
                  {{ fileError }}
                </div>
              </template>

              <!-- Magnet Link tab -->
              <template v-else>
                <div class="magnet-wrap">
                  <label class="form-label" for="atm-magnet">Magnet URI</label>
                  <textarea
                    id="atm-magnet"
                    v-model="magnetUri"
                    class="magnet-input"
                    placeholder="magnet:?xt=urn:btih:..."
                    rows="4"
                    aria-label="Magnet link URI"
                    spellcheck="false"
                  />
                  <div
                    v-if="magnetUri && !magnetUri.trim().startsWith('magnet:')"
                    class="field-error"
                  >
                    <PhWarning :size="13" weight="bold" />
                    URI must start with <code>magnet:</code>
                  </div>
                </div>
              </template>

              <!-- File Tree -->
              <!-- TODO: populate fileTreeFiles from server after torrent is parsed.
                   For now the tree starts empty. A future iteration can:
                   1. Parse the .torrent file client-side (using a WASM bencode parser), or
                   2. Upload to a /api/v1/torrents/parse endpoint and return file list.
              -->
              <div v-if="fileTreeFiles.length > 0" class="file-tree-section">
                <span class="form-label">File Selection</span>
                <FileTree
                  :files="fileTreeFiles"
                  @update:selections="fileTreeSelections = $event"
                  @update:priorities="fileTreePriorities = $event"
                />
              </div>
            </div>
          </div>

          <!-- ── Bottom bar ──────────────────────────────────── -->
          <footer class="modal__footer">
            <button class="btn btn--ghost" @click="emit('close')">Cancel</button>
            <button
              class="btn btn--primary"
              :disabled="!canSubmit"
              @click="handleSubmit"
            >
              <span v-if="isSubmitting" class="spinner" aria-hidden="true" />
              {{ isSubmitting ? 'Adding…' : 'Add Torrent' }}
            </button>
          </footer>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
/* ── Overlay ─────────────────────────────────────────────────── */
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.65);
  z-index: 500;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--spacing-lg);
  box-sizing: border-box;
}

.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity var(--transition-normal);
}

.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}

/* ── Modal box ───────────────────────────────────────────────── */
.modal {
  width: 100%;
  max-width: 820px;
  max-height: 90vh;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: var(--radius-xl);
  box-shadow: 0 24px 64px rgba(0, 0, 0, 0.6);
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

/* ── Header ──────────────────────────────────────────────────── */
.modal__header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--spacing-md) var(--spacing-lg);
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.modal__title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  margin: 0;
}

.modal__close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  background: none;
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  cursor: pointer;
  transition:
    color var(--transition-fast),
    border-color var(--transition-fast),
    background var(--transition-fast);
}

.modal__close:hover {
  color: var(--text-primary);
  border-color: var(--border-focus);
  background: var(--bg-hover);
}

/* ── Tabs ────────────────────────────────────────────────────── */
.modal__tabs {
  display: flex;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.modal__tab {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: var(--spacing-sm) var(--spacing-lg);
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--text-secondary);
  font-size: var(--font-sm);
  cursor: pointer;
  white-space: nowrap;
  transition:
    color var(--transition-fast),
    border-color var(--transition-fast);
}

.modal__tab:hover {
  color: var(--text-primary);
}

.modal__tab--active {
  color: var(--accent-cyan);
  border-bottom-color: var(--accent-cyan);
  font-weight: 600;
}

/* ── Body ────────────────────────────────────────────────────── */
.modal__body {
  display: flex;
  flex: 1;
  overflow: hidden;
  min-height: 0;
}

/* ── Left panel ──────────────────────────────────────────────── */
.modal__left {
  width: 240px;
  flex-shrink: 0;
  border-right: 1px solid var(--border);
  overflow-y: auto;
  background: var(--bg-primary);
}

.settings-panel {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
  padding: var(--spacing-md);
}

/* ── Right panel ─────────────────────────────────────────────── */
.modal__right {
  flex: 1;
  overflow-y: auto;
  padding: var(--spacing-md);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
  min-width: 0;
}

/* ── Form elements ───────────────────────────────────────────── */
.form-group {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.form-label {
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.form-input,
.form-select {
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  color: var(--text-primary);
  font-size: var(--font-sm);
  padding: var(--spacing-xs) var(--spacing-sm);
  outline: none;
  transition: border-color var(--transition-fast);
  width: 100%;
  box-sizing: border-box;
}

.form-input:focus,
.form-select:focus {
  border-color: var(--border-focus);
}

.form-input::placeholder {
  color: var(--text-tertiary);
}

/* ── Tags ────────────────────────────────────────────────────── */
.tags-wrap {
  display: flex;
  flex-wrap: wrap;
  gap: var(--spacing-xs);
}

.tag-btn {
  padding: 2px var(--spacing-sm);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  background: var(--bg-hover);
  color: var(--text-secondary);
  font-size: var(--font-xs);
  cursor: pointer;
  transition:
    color var(--transition-fast),
    background var(--transition-fast),
    border-color var(--transition-fast);
}

.tag-btn--active {
  background: var(--accent-cyan);
  border-color: var(--accent-cyan);
  color: var(--bg-primary);
  font-weight: 600;
}

.tag-btn:not(.tag-btn--active):hover {
  border-color: var(--border-focus);
  color: var(--text-primary);
}

/* ── Options (checkboxes) ────────────────────────────────────── */
.options-list {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.checkbox-label {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  font-size: var(--font-sm);
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.form-checkbox {
  accent-color: var(--accent-cyan);
  width: 13px;
  height: 13px;
  cursor: pointer;
  flex-shrink: 0;
}

/* ── Hidden file input ───────────────────────────────────────── */
.hidden-input {
  position: absolute;
  width: 0;
  height: 0;
  opacity: 0;
  pointer-events: none;
}

/* ── Drop zone ───────────────────────────────────────────────── */
.drop-zone {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--spacing-sm);
  border: 2px dashed var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-2xl) var(--spacing-xl);
  cursor: pointer;
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast);
  text-align: center;
  min-height: 140px;
  background: var(--bg-primary);
}

.drop-zone:hover,
.drop-zone--active {
  border-color: var(--accent-cyan);
  background: color-mix(in srgb, var(--accent-active) 4%, transparent);
}

.drop-zone--selected {
  border-color: var(--status-green);
  background: rgba(16, 185, 129, 0.04);
}

.drop-zone__icon {
  color: var(--text-tertiary);
}

.drop-zone__icon--done {
  color: var(--status-green);
}

.drop-zone__primary {
  font-size: var(--font-sm);
  font-weight: 500;
  color: var(--text-primary);
}

.drop-zone__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
}

.drop-zone__filename {
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--status-green);
  word-break: break-all;
}

.drop-zone__size {
  font-size: var(--font-xs);
  color: var(--text-secondary);
}

.drop-zone__clear {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 2px var(--spacing-sm);
  background: var(--bg-hover);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  color: var(--text-secondary);
  font-size: var(--font-xs);
  cursor: pointer;
  margin-top: var(--spacing-xs);
  transition: color var(--transition-fast), border-color var(--transition-fast);
}

.drop-zone__clear:hover {
  color: var(--status-red);
  border-color: var(--status-red);
}

/* ── Field error ─────────────────────────────────────────────── */
.field-error {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  font-size: var(--font-xs);
  color: var(--status-red);
  padding: var(--spacing-xs) 0;
}

/* ── Magnet input ────────────────────────────────────────────── */
.magnet-wrap {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.magnet-input {
  width: 100%;
  box-sizing: border-box;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: var(--font-sm);
  font-family: monospace;
  padding: var(--spacing-sm);
  outline: none;
  resize: vertical;
  transition: border-color var(--transition-fast);
}

.magnet-input:focus {
  border-color: var(--border-focus);
}

.magnet-input::placeholder {
  color: var(--text-tertiary);
}

/* ── File tree section ───────────────────────────────────────── */
.file-tree-section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

/* ── Footer ──────────────────────────────────────────────────── */
.modal__footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--spacing-sm);
  padding: var(--spacing-md) var(--spacing-lg);
  border-top: 1px solid var(--border);
  background: var(--bg-secondary);
  flex-shrink: 0;
}

/* ── Buttons ─────────────────────────────────────────────────── */
.btn {
  display: inline-flex;
  align-items: center;
  gap: var(--spacing-xs);
  padding: var(--spacing-xs) var(--spacing-lg);
  border-radius: var(--radius-md);
  font-size: var(--font-sm);
  font-weight: 600;
  cursor: pointer;
  transition:
    background var(--transition-fast),
    border-color var(--transition-fast),
    color var(--transition-fast),
    opacity var(--transition-fast);
}

.btn--ghost {
  background: none;
  border: 1px solid var(--border);
  color: var(--text-secondary);
}

.btn--ghost:hover {
  border-color: var(--border-focus);
  color: var(--text-primary);
}

.btn--primary {
  background: var(--accent-cyan);
  border: 1px solid var(--accent-cyan);
  color: var(--bg-primary);
}

.btn--primary:hover:not(:disabled) {
  filter: brightness(0.9);
}

.btn--primary:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

/* ── Spinner ─────────────────────────────────────────────────── */
.spinner {
  display: inline-block;
  width: 13px;
  height: 13px;
  border: 2px solid rgba(0, 0, 0, 0.3);
  border-top-color: #000;
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
}

@keyframes spin {
  to { transform: rotate(360deg); }
}

/* ── Responsive ──────────────────────────────────────────────── */
@media (max-width: 767px) {
  /* Full-screen modal on mobile */
  .modal-overlay {
    padding: 0;
    align-items: flex-end;
  }

  .modal {
    max-width: 100%;
    max-height: 95vh;
    border-radius: var(--radius-xl) var(--radius-xl) 0 0;
    border-bottom: none;
  }

  /* Stack panels vertically: settings above, upload/magnet below */
  .modal__body {
    flex-direction: column;
  }

  .modal__left {
    width: 100%;
    border-right: none;
    border-bottom: 1px solid var(--border);
    /* Limit height so the right panel is reachable */
    max-height: 40vh;
  }

  /* Tighten footer on mobile */
  .modal__footer {
    padding: var(--spacing-sm) var(--spacing-md);
  }

  /* Full-width buttons on mobile */
  .modal__footer .btn {
    flex: 1;
    justify-content: center;
  }
}
</style>
