<script setup lang="ts">
// WebSeedsTab.vue — Web seed settings: connections, timeouts, request size.

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

const maxConnectionsPerTorrent = computed({
  get: () => props.settings.webSeed.maxConnectionsPerTorrent,
  set: (v: number) => patch('webSeed', 'maxConnectionsPerTorrent', v),
});
const timeoutSeconds = computed({
  get: () => props.settings.webSeed.timeoutSeconds,
  set: (v: number) => patch('webSeed', 'timeoutSeconds', v),
});
const waitRetrySeconds = computed({
  get: () => props.settings.webSeed.waitRetrySeconds,
  set: (v: number) => patch('webSeed', 'waitRetrySeconds', v),
});
const maxRequestBytes = computed({
  get: () => props.settings.webSeed.maxRequestBytes,
  set: (v: number) => patch('webSeed', 'maxRequestBytes', v),
});
</script>

<template>
  <div class="webseed-settings">
    <section class="webseed-settings__section">
      <h3 class="webseed-settings__section-title">Web Seeds</h3>
      <div class="webseed-settings__grid">
        <div class="webseed-settings__field">
          <label class="webseed-settings__label" for="ws-max-conn">Max Connections Per Torrent</label>
          <input id="ws-max-conn" v-model.number="maxConnectionsPerTorrent" class="webseed-settings__input" type="number" min="0" />
        </div>
        <div class="webseed-settings__field">
          <label class="webseed-settings__label" for="ws-timeout">Timeout</label>
          <input id="ws-timeout" v-model.number="timeoutSeconds" class="webseed-settings__input" type="number" min="0" />
          <p class="webseed-settings__hint">Seconds</p>
        </div>
        <div class="webseed-settings__field">
          <label class="webseed-settings__label" for="ws-retry">Retry Wait</label>
          <input id="ws-retry" v-model.number="waitRetrySeconds" class="webseed-settings__input" type="number" min="0" />
          <p class="webseed-settings__hint">Seconds</p>
        </div>
        <div class="webseed-settings__field">
          <label class="webseed-settings__label" for="ws-max-req">Max Request Size</label>
          <input id="ws-max-req" v-model.number="maxRequestBytes" class="webseed-settings__input" type="number" min="0" />
          <p class="webseed-settings__hint">Bytes</p>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.webseed-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.webseed-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.webseed-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.webseed-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.webseed-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.webseed-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.webseed-settings__input {
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

.webseed-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.webseed-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

@media (max-width: 640px) {
  .webseed-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
