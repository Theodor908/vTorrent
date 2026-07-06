<script setup lang="ts">
// FileTree.vue — Reusable hierarchical file tree with checkboxes and priority selection.
// Builds a virtual tree from flat file paths (split on '/'), supports tri-state checkboxes,
// folder expand/collapse, search filtering, and per-file priority dropdowns.
//
// Uses a flattened rendering list derived from the tree so the template can be a single
// flat v-for without requiring a recursive child component file.

import { ref, computed, watch } from 'vue';
import {
  PhCaretRight,
  PhCaretDown,
  PhFolder,
  PhFolderOpen,
  PhFile,
} from '@phosphor-icons/vue';
import { formatBytes } from '../../utils/format';

// ============================================================
// Props & Emits
// ============================================================

export interface FileEntry {
  index: number;
  name: string;
  size: number;
  priority: number;
}

const props = defineProps<{
  files: FileEntry[];
}>();

const emit = defineEmits<{
  (e: 'update:selections', indices: number[]): void;
  (e: 'update:priorities', entries: { index: number; priority: number }[]): void;
}>();

// ============================================================
// Priority options
// ============================================================

interface PriorityOption {
  value: number;
  label: string;
}

const PRIORITY_OPTIONS: PriorityOption[] = [
  { value: 0, label: 'Skip' },
  { value: 1, label: 'Do Not Download' },
  { value: 2, label: 'Low' },
  { value: 4, label: 'Normal' },
  { value: 6, label: 'High' },
];

// ============================================================
// Internal tree node types
// ============================================================

interface TreeFile {
  type: 'file';
  name: string;
  fullPath: string;
  index: number;
  size: number;
  depth: number;
}

interface TreeFolder {
  type: 'folder';
  name: string;
  fullPath: string;
  childIndices: number[];  // all descendant file indices
  depth: number;
}

type TreeNode = TreeFile | TreeFolder;

// ============================================================
// Build flat display list from file list
// ============================================================

function buildFlatList(files: FileEntry[]): TreeNode[] {
  // Step 1: build a nested tree structure
  interface NFolder {
    type: 'folder';
    name: string;
    children: (NFolder | NFile)[];
  }
  interface NFile {
    type: 'file';
    name: string;
    index: number;
    size: number;
  }

  const root: NFolder = { type: 'folder', name: '', children: [] };

  for (const file of files) {
    const parts = file.name.split('/');
    let current = root;

    for (let i = 0; i < parts.length - 1; i++) {
      const part = parts[i];
      let folder = current.children.find(
        (c): c is NFolder => c.type === 'folder' && c.name === part,
      );
      if (!folder) {
        folder = { type: 'folder', name: part, children: [] };
        current.children.push(folder);
      }
      current = folder;
    }

    const fileName = parts[parts.length - 1];
    current.children.push({ type: 'file', name: fileName, index: file.index, size: file.size });
  }

  // Step 2: flatten to display list
  function collectFileIndices(node: NFolder | NFile): number[] {
    if (node.type === 'file') return [node.index];
    return node.children.flatMap(collectFileIndices);
  }

  function flatten(
    nodes: (NFolder | NFile)[],
    depth: number,
    pathPrefix: string,
  ): TreeNode[] {
    const result: TreeNode[] = [];
    for (const node of nodes) {
      const fullPath = pathPrefix ? `${pathPrefix}/${node.name}` : node.name;
      if (node.type === 'folder') {
        result.push({
          type: 'folder',
          name: node.name,
          fullPath,
          childIndices: collectFileIndices(node),
          depth,
        });
        result.push(...flatten(node.children, depth + 1, fullPath));
      } else {
        result.push({
          type: 'file',
          name: node.name,
          fullPath,
          index: node.index,
          size: node.size,
          depth,
        });
      }
    }
    return result;
  }

  return flatten(root.children, 0, '');
}

const allNodes = computed(() => buildFlatList(props.files));

// ============================================================
// State: selected indices and priorities
// ============================================================

const selectedIndices = ref<Set<number>>(new Set());
const priorities = ref<Map<number, number>>(new Map());

watch(
  () => props.files,
  (files) => {
    const newSelected = new Set<number>();
    const newPriorities = new Map<number, number>();
    for (const f of files) {
      newPriorities.set(f.index, f.priority);
      if (f.priority !== 0) {
        newSelected.add(f.index);
      }
    }
    selectedIndices.value = newSelected;
    priorities.value = newPriorities;
  },
  { immediate: true },
);

function emitSelections(): void {
  emit('update:selections', [...selectedIndices.value]);
}

function emitPriorities(): void {
  const entries = [...priorities.value.entries()].map(([index, priority]) => ({ index, priority }));
  emit('update:priorities', entries);
}

// ============================================================
// Checkbox state helpers
// ============================================================

type CheckState = 'checked' | 'unchecked' | 'indeterminate';

function folderCheckState(childIndices: number[]): CheckState {
  const checkedCount = childIndices.filter((i) => selectedIndices.value.has(i)).length;
  if (checkedCount === 0) return 'unchecked';
  if (checkedCount === childIndices.length) return 'checked';
  return 'indeterminate';
}

function toggleFolder(node: TreeFolder): void {
  const state = folderCheckState(node.childIndices);
  if (state === 'checked') {
    for (const i of node.childIndices) {
      selectedIndices.value.delete(i);
      priorities.value.set(i, 0);
    }
  } else {
    for (const i of node.childIndices) {
      selectedIndices.value.add(i);
      if ((priorities.value.get(i) ?? 0) === 0) {
        priorities.value.set(i, 4);
      }
    }
  }
  emitSelections();
  emitPriorities();
}

function toggleFile(node: TreeFile): void {
  if (selectedIndices.value.has(node.index)) {
    selectedIndices.value.delete(node.index);
    priorities.value.set(node.index, 0);
  } else {
    selectedIndices.value.add(node.index);
    if ((priorities.value.get(node.index) ?? 0) === 0) {
      priorities.value.set(node.index, 4);
    }
  }
  emitSelections();
  emitPriorities();
}

function handleFilePriorityChange(fileIndex: number, event: Event): void {
  const value = parseInt((event.target as HTMLSelectElement).value, 10);
  priorities.value.set(fileIndex, value);
  if (value === 0) {
    selectedIndices.value.delete(fileIndex);
  } else {
    selectedIndices.value.add(fileIndex);
  }
  emitSelections();
  emitPriorities();
}

// ============================================================
// Select All / None
// ============================================================

function selectAll(): void {
  for (const f of props.files) {
    selectedIndices.value.add(f.index);
    if ((priorities.value.get(f.index) ?? 0) === 0) {
      priorities.value.set(f.index, 4);
    }
  }
  emitSelections();
  emitPriorities();
}

function selectNone(): void {
  selectedIndices.value.clear();
  for (const f of props.files) {
    priorities.value.set(f.index, 0);
  }
  emitSelections();
  emitPriorities();
}

// ============================================================
// Expand/collapse
// ============================================================

const expandedFolders = ref<Set<string>>(new Set());

function isFolderExpanded(fullPath: string): boolean {
  return expandedFolders.value.has(fullPath);
}

function toggleExpand(fullPath: string): void {
  if (expandedFolders.value.has(fullPath)) {
    expandedFolders.value.delete(fullPath);
  } else {
    expandedFolders.value.add(fullPath);
  }
}

// Auto-expand all top-level folders on initial load
watch(
  allNodes,
  (nodes) => {
    for (const node of nodes) {
      if (node.type === 'folder' && node.depth === 0) {
        expandedFolders.value.add(node.fullPath);
      }
    }
  },
  { immediate: true },
);

// ============================================================
// Visibility: a node is visible only if all ancestor folders are expanded
// ============================================================

function isNodeVisible(node: TreeNode): boolean {
  if (node.depth === 0) return true;
  // Check each prefix segment
  const parts = node.fullPath.split('/');
  for (let i = 1; i < parts.length; i++) {
    const ancestorPath = parts.slice(0, i).join('/');
    if (!expandedFolders.value.has(ancestorPath)) return false;
  }
  return true;
}

// ============================================================
// Search filter
// ============================================================

const searchQuery = ref('');

function nodeMatchesSearch(node: TreeNode): boolean {
  const q = searchQuery.value.toLowerCase();
  if (!q) return true;
  if (node.type === 'file') {
    return node.name.toLowerCase().includes(q);
  }
  // Folder matches if any descendant matches
  const indices = node.childIndices;
  return (
    node.name.toLowerCase().includes(q) ||
    props.files.some((f) => indices.includes(f.index) && f.name.toLowerCase().includes(q))
  );
}

// Auto-expand folders that contain matching files when searching
watch(searchQuery, (q) => {
  if (!q) return;
  for (const node of allNodes.value) {
    if (node.type === 'folder' && nodeMatchesSearch(node)) {
      expandedFolders.value.add(node.fullPath);
    }
  }
});

const visibleNodes = computed(() =>
  allNodes.value.filter((n) => isNodeVisible(n) && nodeMatchesSearch(n)),
);

// ============================================================
// Footer: selected size
// ============================================================

const selectedSize = computed(() => {
  let total = 0;
  for (const f of props.files) {
    if (selectedIndices.value.has(f.index)) {
      total += f.size;
    }
  }
  return total;
});
</script>

<template>
  <div class="file-tree">
    <!-- ── Toolbar ──────────────────────────────────────────── -->
    <div class="file-tree__toolbar">
      <button class="file-tree__btn" @click="selectAll">Select All</button>
      <button class="file-tree__btn" @click="selectNone">Select None</button>
      <div class="file-tree__search-wrap">
        <input
          v-model="searchQuery"
          class="file-tree__search-input"
          type="text"
          placeholder="Filter files…"
          aria-label="Filter files"
        />
      </div>
    </div>

    <!-- ── Empty state ─────────────────────────────────────── -->
    <div v-if="files.length === 0" class="file-tree__empty">
      No files available.
    </div>

    <!-- ── Tree rows ───────────────────────────────────────── -->
    <div v-else class="file-tree__scroll">
      <template v-for="node in visibleNodes" :key="node.fullPath">
        <!-- FOLDER row -->
        <div
          v-if="node.type === 'folder'"
          class="file-tree__row file-tree__row--folder"
          :style="{ paddingLeft: `${8 + node.depth * 16}px` }"
        >
          <!-- Expand caret -->
          <button
            class="file-tree__caret"
            :aria-label="isFolderExpanded(node.fullPath) ? 'Collapse' : 'Expand'"
            @click="toggleExpand(node.fullPath)"
          >
            <PhCaretDown v-if="isFolderExpanded(node.fullPath)" :size="12" weight="bold" />
            <PhCaretRight v-else :size="12" weight="bold" />
          </button>

          <!-- Tri-state checkbox -->
          <input
            type="checkbox"
            class="file-tree__checkbox"
            :checked="folderCheckState(node.childIndices) === 'checked'"
            :indeterminate="folderCheckState(node.childIndices) === 'indeterminate'"
            :aria-label="`Select folder ${node.name}`"
            @change="toggleFolder(node)"
          />

          <!-- Icon + name -->
          <PhFolderOpen
            v-if="isFolderExpanded(node.fullPath)"
            class="file-tree__icon file-tree__icon--folder"
            :size="14"
          />
          <PhFolder v-else class="file-tree__icon file-tree__icon--folder" :size="14" />
          <span class="file-tree__name">{{ node.name }}</span>
        </div>

        <!-- FILE row -->
        <div
          v-else-if="node.type === 'file'"
          class="file-tree__row file-tree__row--file"
          :style="{ paddingLeft: `${8 + node.depth * 16}px` }"
        >
          <!-- Spacer matching caret width -->
          <span class="file-tree__caret-spacer" aria-hidden="true" />

          <!-- Checkbox -->
          <input
            type="checkbox"
            class="file-tree__checkbox"
            :checked="selectedIndices.has(node.index)"
            :aria-label="`Select file ${node.name}`"
            @change="toggleFile(node)"
          />

          <!-- Icon + name -->
          <PhFile class="file-tree__icon" :size="14" />
          <span class="file-tree__name" :title="node.fullPath">{{ node.name }}</span>

          <!-- Size -->
          <span class="file-tree__size">{{ formatBytes(node.size) }}</span>

          <!-- Priority dropdown -->
          <select
            class="file-tree__priority"
            :value="priorities.get(node.index) ?? 4"
            :aria-label="`Priority for ${node.name}`"
            @change="handleFilePriorityChange(node.index, $event)"
          >
            <option
              v-for="opt in PRIORITY_OPTIONS"
              :key="opt.value"
              :value="opt.value"
            >
              {{ opt.label }}
            </option>
          </select>
        </div>
      </template>
    </div>

    <!-- ── Footer ──────────────────────────────────────────── -->
    <div class="file-tree__footer">
      <span>Selected: <strong>{{ formatBytes(selectedSize) }}</strong></span>
      <span class="file-tree__footer-count">{{ selectedIndices.size }} / {{ files.length }} files</span>
    </div>
  </div>
</template>

<style scoped>
.file-tree {
  display: flex;
  flex-direction: column;
  min-height: 0;
  background: var(--bg-primary);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  overflow: hidden;
}

/* ── Toolbar ────────────────────────────────────────────────── */
.file-tree__toolbar {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  padding: var(--spacing-sm) var(--spacing-md);
  border-bottom: 1px solid var(--border);
  background: var(--bg-secondary);
  flex-shrink: 0;
}

.file-tree__btn {
  padding: 2px var(--spacing-sm);
  background: var(--bg-hover);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  color: var(--text-secondary);
  font-size: var(--font-xs);
  cursor: pointer;
  white-space: nowrap;
  transition:
    color var(--transition-fast),
    border-color var(--transition-fast);
}

.file-tree__btn:hover {
  color: var(--text-primary);
  border-color: var(--border-focus);
}

.file-tree__search-wrap {
  flex: 1;
}

.file-tree__search-input {
  width: 100%;
  box-sizing: border-box;
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  color: var(--text-primary);
  font-size: var(--font-xs);
  padding: 3px var(--spacing-sm);
  outline: none;
  transition: border-color var(--transition-fast);
}

.file-tree__search-input:focus {
  border-color: var(--border-focus);
}

.file-tree__search-input::placeholder {
  color: var(--text-tertiary);
}

/* ── Scroll area ────────────────────────────────────────────── */
.file-tree__scroll {
  flex: 1;
  overflow-y: auto;
  min-height: 80px;
  max-height: 280px;
}

/* ── Empty ──────────────────────────────────────────────────── */
.file-tree__empty {
  padding: var(--spacing-xl);
  text-align: center;
  color: var(--text-tertiary);
  font-size: var(--font-sm);
}

/* ── Rows ───────────────────────────────────────────────────── */
.file-tree__row {
  display: flex;
  align-items: center;
  gap: var(--spacing-xs);
  min-height: 26px;
  border-bottom: 1px solid rgba(42, 42, 74, 0.3);
  font-size: var(--font-xs);
  color: var(--text-primary);
  padding-right: var(--spacing-sm);
  box-sizing: border-box;
}

.file-tree__row:hover {
  background: var(--bg-hover);
}

.file-tree__row--folder {
  background: var(--bg-secondary);
  font-weight: 500;
}

.file-tree__row--folder:hover {
  background: var(--bg-hover);
}

/* ── Caret ──────────────────────────────────────────────────── */
.file-tree__caret {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  background: none;
  border: none;
  color: var(--text-tertiary);
  cursor: pointer;
  border-radius: var(--radius-sm);
  transition: color var(--transition-fast);
}

.file-tree__caret:hover {
  color: var(--text-primary);
}

.file-tree__caret-spacer {
  width: 16px;
  flex-shrink: 0;
}

/* ── Checkbox ───────────────────────────────────────────────── */
.file-tree__checkbox {
  flex-shrink: 0;
  cursor: pointer;
  width: 13px;
  height: 13px;
  accent-color: var(--accent-cyan);
}

/* ── Icons ──────────────────────────────────────────────────── */
.file-tree__icon {
  flex-shrink: 0;
  color: var(--text-secondary);
}

.file-tree__icon--folder {
  color: var(--accent-active);
}

/* ── Name ───────────────────────────────────────────────────── */
.file-tree__name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}

/* ── Size ───────────────────────────────────────────────────── */
.file-tree__size {
  flex-shrink: 0;
  color: var(--text-secondary);
  font-size: var(--font-xs);
  min-width: 60px;
  text-align: right;
}

/* ── Priority dropdown ──────────────────────────────────────── */
.file-tree__priority {
  flex-shrink: 0;
  width: 110px;
  font-size: var(--font-xs);
  background: var(--bg-input);
  color: var(--text-primary);
  border: 1px solid var(--border);
  border-radius: var(--radius-sm);
  padding: 1px 4px;
  cursor: pointer;
  outline: none;
  transition: border-color var(--transition-fast);
}

.file-tree__priority:focus {
  border-color: var(--border-focus);
}

/* ── Footer ─────────────────────────────────────────────────── */
.file-tree__footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: var(--spacing-xs) var(--spacing-md);
  border-top: 1px solid var(--border);
  background: var(--bg-secondary);
  font-size: var(--font-xs);
  color: var(--text-secondary);
  flex-shrink: 0;
}

.file-tree__footer strong {
  color: var(--text-primary);
}

.file-tree__footer-count {
  color: var(--text-tertiary);
}
</style>
