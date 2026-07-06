<script setup lang="ts">
// DownloadsTab.vue — Download settings: save paths, pre-allocation.

import { computed } from 'vue';
import type { GlobalSettings } from '@/types/settings';

const props = defineProps<{
  settings: GlobalSettings;
}>();

const emit = defineEmits<{
  (e: 'update:settings', value: GlobalSettings): void;
}>();

function patch<K extends keyof GlobalSettings>(
  section: K,
  key: keyof GlobalSettings[K],
  value: GlobalSettings[K][typeof key],
): void {
  const sectionValue = props.settings[section] as unknown as Record<string, unknown>;
  emit('update:settings', {
    ...props.settings,
    [section]: { ...sectionValue, [key]: value },
  });
}

const defaultSavePath = computed({
  get: () => props.settings.disk.defaultSavePath,
  set: (v: string) => patch('disk', 'defaultSavePath', v),
});
const incompleteSavePath = computed({
  get: () => props.settings.disk.incompleteSavePath,
  set: (v: string) => patch('disk', 'incompleteSavePath', v),
});
const preallocateFiles = computed({
  get: () => props.settings.disk.preallocateFiles,
  set: (v: boolean) => patch('disk', 'preallocateFiles', v),
});
</script>

<template>
  <div class="downloads-settings">
    <section class="downloads-settings__section">
      <h3 class="downloads-settings__section-title">Downloads</h3>
      <div class="downloads-settings__grid">
        <div class="downloads-settings__field downloads-settings__field--full">
          <label class="downloads-settings__label" for="dl-save-path">Default Save Path</label>
          <input id="dl-save-path" v-model="defaultSavePath" class="downloads-settings__input" type="text" placeholder="/downloads" spellcheck="false" />
          <p class="downloads-settings__hint">Where new torrents are saved.</p>
        </div>
        <div class="downloads-settings__field downloads-settings__field--full">
          <label class="downloads-settings__label" for="dl-incomplete">Incomplete Save Path</label>
          <input id="dl-incomplete" v-model="incompleteSavePath" class="downloads-settings__input" type="text" placeholder="Leave blank to use default" spellcheck="false" />
          <p class="downloads-settings__hint">Path for incomplete downloads.</p>
        </div>
      </div>
      <div class="downloads-settings__checkboxes">
        <div class="downloads-settings__checkbox-row">
          <input id="dl-prealloc" v-model="preallocateFiles" class="downloads-settings__checkbox" type="checkbox" />
          <label for="dl-prealloc" class="downloads-settings__label-inline">Pre-allocate disk space for files</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.downloads-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.downloads-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.downloads-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.downloads-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.downloads-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.downloads-settings__field--full {
  grid-column: 1 / -1;
}

.downloads-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.downloads-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.downloads-settings__input {
  width: 100%;
  height: 36px;
  padding: 0 var(--spacing-md);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: var(--font-md);
  outline: none;
  transition: border-color var(--transition-fast), box-shadow var(--transition-fast);
}

.downloads-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.downloads-settings__input::placeholder {
  color: var(--text-tertiary);
}

.downloads-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.downloads-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.downloads-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.downloads-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .downloads-settings__grid {
    grid-template-columns: 1fr;
  }

  .downloads-settings__field--full {
    grid-column: 1;
  }
}
</style>
