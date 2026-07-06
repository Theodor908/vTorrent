<script setup lang="ts">
// BitTorrentTab.vue — BitTorrent protocol settings: privacy, encryption, queueing, share ratio.

import { computed } from 'vue';
import type { GlobalSettings, EncryptionSettings } from '@/types/settings';

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

function patchEncryption(key: keyof EncryptionSettings, value: string): void {
  emit('update:settings', {
    ...props.settings,
    protocol: {
      ...props.settings.protocol,
      encryption: { ...props.settings.protocol.encryption, [key]: value },
    },
  });
}

// ── Privacy ──
const enableDht = computed({
  get: () => props.settings.protocol.enableDht,
  set: (v: boolean) => patch('protocol', 'enableDht', v),
});
const enablePex = computed({
  get: () => props.settings.protocol.enablePex,
  set: (v: boolean) => patch('protocol', 'enablePex', v),
});
const enableLsd = computed({
  get: () => props.settings.protocol.enableLsd,
  set: (v: boolean) => patch('protocol', 'enableLsd', v),
});
const anonymousMode = computed({
  get: () => props.settings.privacy.anonymousMode,
  set: (v: boolean) => patch('privacy', 'anonymousMode', v),
});

// ── Encryption ──
const outPolicy = computed({
  get: () => props.settings.protocol.encryption.outPolicy,
  set: (v: string) => patchEncryption('outPolicy', v),
});
const inPolicy = computed({
  get: () => props.settings.protocol.encryption.inPolicy,
  set: (v: string) => patchEncryption('inPolicy', v),
});
const allowedLevel = computed({
  get: () => props.settings.protocol.encryption.allowedLevel,
  set: (v: string) => patchEncryption('allowedLevel', v),
});

// ── Queueing ──
const maxActiveDownloads = computed({
  get: () => props.settings.queue.maxActiveDownloads,
  set: (v: number) => patch('queue', 'maxActiveDownloads', v),
});
const maxActiveSeeds = computed({
  get: () => props.settings.queue.maxActiveSeeds,
  set: (v: number) => patch('queue', 'maxActiveSeeds', v),
});
const maxActiveTorrents = computed({
  get: () => props.settings.queue.maxActiveTorrents,
  set: (v: number) => patch('queue', 'maxActiveTorrents', v),
});
const dontCountSlowTorrents = computed({
  get: () => props.settings.queue.dontCountSlowTorrents,
  set: (v: boolean) => patch('queue', 'dontCountSlowTorrents', v),
});

// ── Share Ratio ──
const seedRatioLimit = computed({
  get: () => props.settings.behavior.seedRatioLimit,
  set: (v: number) => patch('behavior', 'seedRatioLimit', v),
});
const seedTimeLimit = computed({
  get: () => props.settings.behavior.seedTimeLimit,
  set: (v: number) => patch('behavior', 'seedTimeLimit', v),
});
const pauseOnSeedComplete = computed({
  get: () => props.settings.behavior.pauseOnSeedComplete,
  set: (v: boolean) => patch('behavior', 'pauseOnSeedComplete', v),
});
const removeOnSeedComplete = computed({
  get: () => props.settings.behavior.removeOnSeedComplete,
  set: (v: boolean) => patch('behavior', 'removeOnSeedComplete', v),
});
</script>

<template>
  <div class="bt-settings">
    <!-- ── Privacy ── -->
    <section class="bt-settings__section">
      <h3 class="bt-settings__section-title">Privacy</h3>
      <div class="bt-settings__checkboxes">
        <div class="bt-settings__checkbox-row">
          <input id="bt-dht" v-model="enableDht" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-dht" class="bt-settings__label-inline">Enable DHT</label>
        </div>
        <div class="bt-settings__checkbox-row">
          <input id="bt-pex" v-model="enablePex" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-pex" class="bt-settings__label-inline">Enable PEX</label>
        </div>
        <div class="bt-settings__checkbox-row">
          <input id="bt-lsd" v-model="enableLsd" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-lsd" class="bt-settings__label-inline">Enable LSD</label>
        </div>
        <div class="bt-settings__checkbox-row">
          <input id="bt-anon" v-model="anonymousMode" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-anon" class="bt-settings__label-inline">Anonymous Mode</label>
        </div>
      </div>
    </section>

    <!-- ── Encryption ── -->
    <section class="bt-settings__section">
      <h3 class="bt-settings__section-title">Encryption</h3>
      <div class="bt-settings__grid">
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-enc-out">Outgoing Policy</label>
          <select id="bt-enc-out" v-model="outPolicy" class="bt-settings__select">
            <option value="Forced">Forced</option>
            <option value="Enabled">Enabled</option>
            <option value="Disabled">Disabled</option>
          </select>
        </div>
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-enc-in">Incoming Policy</label>
          <select id="bt-enc-in" v-model="inPolicy" class="bt-settings__select">
            <option value="Forced">Forced</option>
            <option value="Enabled">Enabled</option>
            <option value="Disabled">Disabled</option>
          </select>
        </div>
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-enc-level">Allowed Level</label>
          <select id="bt-enc-level" v-model="allowedLevel" class="bt-settings__select">
            <option value="Plaintext">Plaintext</option>
            <option value="RC4">RC4</option>
            <option value="Both">Both</option>
          </select>
        </div>
      </div>
    </section>

    <!-- ── Queueing ── -->
    <section class="bt-settings__section">
      <h3 class="bt-settings__section-title">Queueing</h3>
      <div class="bt-settings__grid">
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-q-dl">Max Active Downloads</label>
          <input id="bt-q-dl" v-model.number="maxActiveDownloads" class="bt-settings__input" type="number" min="0" />
        </div>
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-q-seed">Max Active Seeds</label>
          <input id="bt-q-seed" v-model.number="maxActiveSeeds" class="bt-settings__input" type="number" min="0" />
        </div>
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-q-total">Max Active Torrents</label>
          <input id="bt-q-total" v-model.number="maxActiveTorrents" class="bt-settings__input" type="number" min="0" />
        </div>
      </div>
      <div class="bt-settings__checkboxes">
        <div class="bt-settings__checkbox-row">
          <input id="bt-q-slow" v-model="dontCountSlowTorrents" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-q-slow" class="bt-settings__label-inline">Don't Count Slow Torrents</label>
        </div>
      </div>
    </section>

    <!-- ── Share Ratio ── -->
    <section class="bt-settings__section">
      <h3 class="bt-settings__section-title">Share Ratio</h3>
      <div class="bt-settings__grid">
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-sr-ratio">Seed Ratio Limit</label>
          <input id="bt-sr-ratio" v-model.number="seedRatioLimit" class="bt-settings__input" type="number" min="0" step="0.1" />
          <p class="bt-settings__hint">0 = disabled</p>
        </div>
        <div class="bt-settings__field">
          <label class="bt-settings__label" for="bt-sr-time">Seed Time Limit</label>
          <input id="bt-sr-time" v-model.number="seedTimeLimit" class="bt-settings__input" type="number" min="0" />
          <p class="bt-settings__hint">Minutes, 0 = disabled</p>
        </div>
      </div>
      <div class="bt-settings__checkboxes">
        <div class="bt-settings__checkbox-row">
          <input id="bt-sr-pause" v-model="pauseOnSeedComplete" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-sr-pause" class="bt-settings__label-inline">Pause on Seed Complete</label>
        </div>
        <div class="bt-settings__checkbox-row">
          <input id="bt-sr-remove" v-model="removeOnSeedComplete" class="bt-settings__checkbox" type="checkbox" />
          <label for="bt-sr-remove" class="bt-settings__label-inline">Remove on Seed Complete</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.bt-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.bt-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.bt-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.bt-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.bt-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.bt-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.bt-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.bt-settings__input {
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

.bt-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.bt-settings__select {
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

.bt-settings__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.bt-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.bt-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.bt-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.bt-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .bt-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
