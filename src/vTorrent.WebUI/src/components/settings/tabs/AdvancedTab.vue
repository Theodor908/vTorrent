<script setup lang="ts">
// AdvancedTab.vue — Advanced settings: disk, seeding behavior, tracker, logging.

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

// ── Disk ──
const cacheSize = computed({
  get: () => props.settings.disk.cacheSize,
  set: (v: number) => patch('disk', 'cacheSize', v),
});
const hashThreads = computed({
  get: () => props.settings.disk.hashThreads,
  set: (v: number) => patch('disk', 'hashThreads', v),
});

// ── Seeding Behavior ──
const chokingAlgorithm = computed({
  get: () => props.settings.behavior.chokingAlgorithm,
  set: (v: string) => patch('behavior', 'chokingAlgorithm', v as GlobalSettings['behavior']['chokingAlgorithm']),
});
const seedChokingAlgorithm = computed({
  get: () => props.settings.behavior.seedChokingAlgorithm,
  set: (v: string) => patch('behavior', 'seedChokingAlgorithm', v as GlobalSettings['behavior']['seedChokingAlgorithm']),
});
const closeRedundantConnections = computed({
  get: () => props.settings.behavior.closeRedundantConnections,
  set: (v: boolean) => patch('behavior', 'closeRedundantConnections', v),
});
const seedingOutgoingConnections = computed({
  get: () => props.settings.behavior.seedingOutgoingConnections,
  set: (v: boolean) => patch('behavior', 'seedingOutgoingConnections', v),
});

// ── Tracker ──
const announceToAllTrackers = computed({
  get: () => props.settings.tracker.announceToAllTrackers,
  set: (v: boolean) => patch('tracker', 'announceToAllTrackers', v),
});
const announceToAllTiers = computed({
  get: () => props.settings.tracker.announceToAllTiers,
  set: (v: boolean) => patch('tracker', 'announceToAllTiers', v),
});
const preferUdpTrackers = computed({
  get: () => props.settings.tracker.preferUdpTrackers,
  set: (v: boolean) => patch('tracker', 'preferUdpTrackers', v),
});

// ── Logging ──
const logLevel = computed({
  get: () => props.settings.logging.level,
  set: (v: string) => patch('logging', 'level', v),
});
const logToFile = computed({
  get: () => props.settings.logging.logToFile,
  set: (v: boolean) => patch('logging', 'logToFile', v),
});
</script>

<template>
  <div class="advanced-settings">
    <!-- ── Disk ── -->
    <section class="advanced-settings__section">
      <h3 class="advanced-settings__section-title">Disk</h3>
      <div class="advanced-settings__grid">
        <div class="advanced-settings__field">
          <label class="advanced-settings__label" for="adv-cache">Cache Size</label>
          <input id="adv-cache" v-model.number="cacheSize" class="advanced-settings__input" type="number" min="0" />
          <p class="advanced-settings__hint">Bytes</p>
        </div>
        <div class="advanced-settings__field">
          <label class="advanced-settings__label" for="adv-hash">Hash Threads</label>
          <input id="adv-hash" v-model.number="hashThreads" class="advanced-settings__input" type="number" min="1" />
        </div>
      </div>
    </section>

    <!-- ── Seeding Behavior ── -->
    <section class="advanced-settings__section">
      <h3 class="advanced-settings__section-title">Seeding Behavior</h3>
      <div class="advanced-settings__grid">
        <div class="advanced-settings__field">
          <label class="advanced-settings__label" for="adv-choke">Choking Algorithm</label>
          <select id="adv-choke" v-model="chokingAlgorithm" class="advanced-settings__select">
            <option value="FixedSlots">Fixed Slots</option>
            <option value="RateBased">Rate Based</option>
            <option value="BitTyrant">BitTyrant</option>
            <option value="Adaptive">Adaptive</option>
          </select>
        </div>
        <div class="advanced-settings__field">
          <label class="advanced-settings__label" for="adv-seed-choke">Seed Choking Algorithm</label>
          <select id="adv-seed-choke" v-model="seedChokingAlgorithm" class="advanced-settings__select">
            <option value="FastestUpload">Fastest Upload</option>
            <option value="RoundRobin">Round Robin</option>
            <option value="AntiLeech">Anti-Leech</option>
          </select>
        </div>
      </div>
      <div class="advanced-settings__checkboxes">
        <div class="advanced-settings__checkbox-row">
          <input id="adv-close-redundant" v-model="closeRedundantConnections" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-close-redundant" class="advanced-settings__label-inline">Close Redundant Connections</label>
        </div>
        <div class="advanced-settings__checkbox-row">
          <input id="adv-seed-outgoing" v-model="seedingOutgoingConnections" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-seed-outgoing" class="advanced-settings__label-inline">Seeding Outgoing Connections</label>
        </div>
      </div>
    </section>

    <!-- ── Tracker ── -->
    <section class="advanced-settings__section">
      <h3 class="advanced-settings__section-title">Tracker</h3>
      <div class="advanced-settings__checkboxes">
        <div class="advanced-settings__checkbox-row">
          <input id="adv-tr-all" v-model="announceToAllTrackers" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-tr-all" class="advanced-settings__label-inline">Announce to All Trackers</label>
        </div>
        <div class="advanced-settings__checkbox-row">
          <input id="adv-tr-tiers" v-model="announceToAllTiers" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-tr-tiers" class="advanced-settings__label-inline">Announce to All Tiers</label>
        </div>
        <div class="advanced-settings__checkbox-row">
          <input id="adv-tr-udp" v-model="preferUdpTrackers" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-tr-udp" class="advanced-settings__label-inline">Prefer UDP Trackers</label>
        </div>
      </div>
    </section>

    <!-- ── Logging ── -->
    <section class="advanced-settings__section">
      <h3 class="advanced-settings__section-title">Logging</h3>
      <div class="advanced-settings__grid">
        <div class="advanced-settings__field">
          <label class="advanced-settings__label" for="adv-log-level">Log Level</label>
          <select id="adv-log-level" v-model="logLevel" class="advanced-settings__select">
            <option value="Trace">Trace</option>
            <option value="Debug">Debug</option>
            <option value="Information">Information</option>
            <option value="Warning">Warning</option>
            <option value="Error">Error</option>
            <option value="Critical">Critical</option>
          </select>
        </div>
      </div>
      <div class="advanced-settings__checkboxes">
        <div class="advanced-settings__checkbox-row">
          <input id="adv-log-file" v-model="logToFile" class="advanced-settings__checkbox" type="checkbox" />
          <label for="adv-log-file" class="advanced-settings__label-inline">Log to File</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.advanced-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.advanced-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.advanced-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.advanced-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.advanced-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.advanced-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.advanced-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.advanced-settings__input {
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

.advanced-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.advanced-settings__select {
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

.advanced-settings__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.advanced-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.advanced-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.advanced-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.advanced-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .advanced-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
