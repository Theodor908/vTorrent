<script setup lang="ts">
// PrivacyTab.vue — Privacy settings: secure deletion options.

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

const secureDeletion = computed({
  get: () => props.settings.privacy.secureDeletion,
  set: (v: boolean) => patch('privacy', 'secureDeletion', v),
});
const secureDeletionIncludeMetadata = computed({
  get: () => props.settings.privacy.secureDeletionIncludeMetadata,
  set: (v: boolean) => patch('privacy', 'secureDeletionIncludeMetadata', v),
});
</script>

<template>
  <div class="privacy-settings">
    <section class="privacy-settings__section">
      <h3 class="privacy-settings__section-title">Privacy</h3>
      <div class="privacy-settings__checkboxes">
        <div class="privacy-settings__checkbox-row">
          <input id="priv-secure" v-model="secureDeletion" class="privacy-settings__checkbox" type="checkbox" />
          <label for="priv-secure" class="privacy-settings__label-inline">Securely wipe torrent data on deletion</label>
        </div>
        <div class="privacy-settings__checkbox-row">
          <input id="priv-secure-meta" v-model="secureDeletionIncludeMetadata" class="privacy-settings__checkbox" type="checkbox" />
          <label for="priv-secure-meta" class="privacy-settings__label-inline">Include metadata files in secure deletion</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.privacy-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.privacy-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.privacy-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.privacy-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.privacy-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.privacy-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

.privacy-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}
</style>
