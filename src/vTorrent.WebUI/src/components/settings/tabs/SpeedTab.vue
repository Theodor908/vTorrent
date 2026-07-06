<script setup lang="ts">
// SpeedTab.vue — Bandwidth/speed settings: global and per-torrent limits.

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

const globalDownloadLimit = computed({
  get: () => props.settings.bandwidth.globalDownloadLimit,
  set: (v: number) => patch('bandwidth', 'globalDownloadLimit', v),
});
const globalUploadLimit = computed({
  get: () => props.settings.bandwidth.globalUploadLimit,
  set: (v: number) => patch('bandwidth', 'globalUploadLimit', v),
});
const perTorrentDownloadLimit = computed({
  get: () => props.settings.bandwidth.perTorrentDownloadLimit,
  set: (v: number) => patch('bandwidth', 'perTorrentDownloadLimit', v),
});
const perTorrentUploadLimit = computed({
  get: () => props.settings.bandwidth.perTorrentUploadLimit,
  set: (v: number) => patch('bandwidth', 'perTorrentUploadLimit', v),
});
const rateLimitIpOverhead = computed({
  get: () => props.settings.bandwidth.rateLimitIpOverhead,
  set: (v: boolean) => patch('bandwidth', 'rateLimitIpOverhead', v),
});
const mixedModeAlgorithm = computed({
  get: () => props.settings.bandwidth.mixedModeAlgorithm,
  set: (v: string) => patch('bandwidth', 'mixedModeAlgorithm', v as GlobalSettings['bandwidth']['mixedModeAlgorithm']),
});
</script>

<template>
  <div class="speed-settings">
    <section class="speed-settings__section">
      <h3 class="speed-settings__section-title">Speed Limits</h3>
      <div class="speed-settings__grid">
        <div class="speed-settings__field">
          <label class="speed-settings__label" for="spd-dl-global">Global Download Limit</label>
          <input id="spd-dl-global" v-model.number="globalDownloadLimit" class="speed-settings__input" type="number" min="0" />
          <p class="speed-settings__hint">KB/s, 0 = unlimited</p>
        </div>
        <div class="speed-settings__field">
          <label class="speed-settings__label" for="spd-ul-global">Global Upload Limit</label>
          <input id="spd-ul-global" v-model.number="globalUploadLimit" class="speed-settings__input" type="number" min="0" />
          <p class="speed-settings__hint">KB/s, 0 = unlimited</p>
        </div>
        <div class="speed-settings__field">
          <label class="speed-settings__label" for="spd-dl-torrent">Per-Torrent Download Limit</label>
          <input id="spd-dl-torrent" v-model.number="perTorrentDownloadLimit" class="speed-settings__input" type="number" min="0" />
          <p class="speed-settings__hint">KB/s</p>
        </div>
        <div class="speed-settings__field">
          <label class="speed-settings__label" for="spd-ul-torrent">Per-Torrent Upload Limit</label>
          <input id="spd-ul-torrent" v-model.number="perTorrentUploadLimit" class="speed-settings__input" type="number" min="0" />
          <p class="speed-settings__hint">KB/s</p>
        </div>
        <div class="speed-settings__field">
          <label class="speed-settings__label" for="spd-mixed">Mixed Mode Algorithm</label>
          <select id="spd-mixed" v-model="mixedModeAlgorithm" class="speed-settings__select">
            <option value="PreferTcp">Prefer TCP</option>
            <option value="PeerProportional">Peer Proportional</option>
            <option value="PreferUtp">Prefer uTP</option>
          </select>
        </div>
      </div>
      <div class="speed-settings__checkboxes">
        <div class="speed-settings__checkbox-row">
          <input id="spd-ip-overhead" v-model="rateLimitIpOverhead" class="speed-settings__checkbox" type="checkbox" />
          <label for="spd-ip-overhead" class="speed-settings__label-inline">Rate Limit IP Overhead</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.speed-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.speed-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.speed-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.speed-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.speed-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.speed-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.speed-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.speed-settings__input {
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

.speed-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.speed-settings__select {
  width: 100%;
  height: 36px;
  padding: 0 var(--spacing-md);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: var(--font-md);
  outline: none;
  appearance: none;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8'%3E%3Cpath d='M1 1l5 5 5-5' stroke='%2364748b' stroke-width='1.5' fill='none' stroke-linecap='round'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right var(--spacing-md) center;
  padding-right: var(--spacing-2xl);
  cursor: pointer;
  transition: border-color var(--transition-fast), box-shadow var(--transition-fast);
}

.speed-settings__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.speed-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.speed-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.speed-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.speed-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .speed-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
