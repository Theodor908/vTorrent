<script setup lang="ts">
// ConnectionTab.vue — Connection + Proxy settings.

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

// ── Connection ──
const maxGlobalConnections = computed({
  get: () => props.settings.connection.maxGlobalConnections,
  set: (v: number) => patch('connection', 'maxGlobalConnections', v),
});
const maxConnectionsPerTorrent = computed({
  get: () => props.settings.connection.maxConnectionsPerTorrent,
  set: (v: number) => patch('connection', 'maxConnectionsPerTorrent', v),
});
const maxHalfOpenConnections = computed({
  get: () => props.settings.connection.maxHalfOpenConnections,
  set: (v: number) => patch('connection', 'maxHalfOpenConnections', v),
});
const enableUpnp = computed({
  get: () => props.settings.connection.enableUpnp,
  set: (v: boolean) => patch('connection', 'enableUpnp', v),
});
const enableNatPmp = computed({
  get: () => props.settings.connection.enableNatPmp,
  set: (v: boolean) => patch('connection', 'enableNatPmp', v),
});
const allowMultipleConnectionsPerIp = computed({
  get: () => props.settings.connection.allowMultipleConnectionsPerIp,
  set: (v: boolean) => patch('connection', 'allowMultipleConnectionsPerIp', v),
});

// ── Proxy ──
const proxyEnabled = computed(() => props.settings.proxy.type !== 'None');
const proxyNeedsAuth = computed(() => ['Socks5Password', 'HttpPassword'].includes(props.settings.proxy.type));

const proxyType = computed({
  get: () => props.settings.proxy.type,
  set: (v: string) => patch('proxy', 'type', v as GlobalSettings['proxy']['type']),
});
const proxyHostname = computed({
  get: () => props.settings.proxy.hostname,
  set: (v: string) => patch('proxy', 'hostname', v),
});
const proxyPort = computed({
  get: () => props.settings.proxy.port,
  set: (v: number) => patch('proxy', 'port', v),
});
const proxyUsername = computed({
  get: () => props.settings.proxy.username,
  set: (v: string) => patch('proxy', 'username', v),
});
const proxyPassword = computed({
  get: () => props.settings.proxy.password,
  set: (v: string) => patch('proxy', 'password', v),
});
const proxyPeerConnections = computed({
  get: () => props.settings.proxy.proxyPeerConnections,
  set: (v: boolean) => patch('proxy', 'proxyPeerConnections', v),
});
const proxyTrackerConnections = computed({
  get: () => props.settings.proxy.proxyTrackerConnections,
  set: (v: boolean) => patch('proxy', 'proxyTrackerConnections', v),
});
</script>

<template>
  <div class="connection-settings">
    <!-- ── Connection ── -->
    <section class="connection-settings__section">
      <h3 class="connection-settings__section-title">Connection</h3>
      <div class="connection-settings__grid">
        <div class="connection-settings__field">
          <label class="connection-settings__label" for="conn-listen-port">Listen Port</label>
          <input id="conn-listen-port" :value="props.settings.connection.listenPort" class="connection-settings__input connection-settings__input--disabled" type="number" disabled />
          <p class="connection-settings__hint">Requires engine restart — change in desktop app.</p>
        </div>
        <div class="connection-settings__field">
          <label class="connection-settings__label" for="conn-max-global">Max Global Connections</label>
          <input id="conn-max-global" v-model.number="maxGlobalConnections" class="connection-settings__input" type="number" min="0" />
        </div>
        <div class="connection-settings__field">
          <label class="connection-settings__label" for="conn-max-torrent">Max Connections Per Torrent</label>
          <input id="conn-max-torrent" v-model.number="maxConnectionsPerTorrent" class="connection-settings__input" type="number" min="0" />
        </div>
        <div class="connection-settings__field">
          <label class="connection-settings__label" for="conn-max-half">Max Half-Open Connections</label>
          <input id="conn-max-half" v-model.number="maxHalfOpenConnections" class="connection-settings__input" type="number" min="0" />
        </div>
      </div>
      <div class="connection-settings__checkboxes">
        <div class="connection-settings__checkbox-row">
          <input id="conn-upnp" v-model="enableUpnp" class="connection-settings__checkbox" type="checkbox" />
          <label for="conn-upnp" class="connection-settings__label-inline">Enable UPnP</label>
        </div>
        <div class="connection-settings__checkbox-row">
          <input id="conn-natpmp" v-model="enableNatPmp" class="connection-settings__checkbox" type="checkbox" />
          <label for="conn-natpmp" class="connection-settings__label-inline">Enable NAT-PMP</label>
        </div>
        <div class="connection-settings__checkbox-row">
          <input id="conn-multi-ip" v-model="allowMultipleConnectionsPerIp" class="connection-settings__checkbox" type="checkbox" />
          <label for="conn-multi-ip" class="connection-settings__label-inline">Allow Multiple Connections Per IP</label>
        </div>
      </div>
    </section>

    <!-- ── Proxy ── -->
    <section class="connection-settings__section">
      <h3 class="connection-settings__section-title">Proxy</h3>
      <div class="connection-settings__grid">
        <div class="connection-settings__field">
          <label class="connection-settings__label" for="proxy-type">Proxy Type</label>
          <select id="proxy-type" v-model="proxyType" class="connection-settings__select">
            <option value="None">None</option>
            <option value="Socks4">SOCKS4</option>
            <option value="Socks5">SOCKS5</option>
            <option value="Socks5Password">SOCKS5 (with auth)</option>
            <option value="Http">HTTP</option>
            <option value="HttpPassword">HTTP (with auth)</option>
          </select>
        </div>
        <div v-if="proxyEnabled" class="connection-settings__field">
          <label class="connection-settings__label" for="proxy-host">Hostname</label>
          <input id="proxy-host" v-model="proxyHostname" class="connection-settings__input" type="text" spellcheck="false" />
        </div>
        <div v-if="proxyEnabled" class="connection-settings__field">
          <label class="connection-settings__label" for="proxy-port">Port</label>
          <input id="proxy-port" v-model.number="proxyPort" class="connection-settings__input" type="number" min="1" max="65535" />
        </div>
        <div v-if="proxyNeedsAuth" class="connection-settings__field">
          <label class="connection-settings__label" for="proxy-user">Username</label>
          <input id="proxy-user" v-model="proxyUsername" class="connection-settings__input" type="text" spellcheck="false" />
        </div>
        <div v-if="proxyNeedsAuth" class="connection-settings__field">
          <label class="connection-settings__label" for="proxy-pass">Password</label>
          <input id="proxy-pass" v-model="proxyPassword" class="connection-settings__input" type="password" />
        </div>
      </div>
      <div v-if="proxyEnabled" class="connection-settings__checkboxes">
        <div class="connection-settings__checkbox-row">
          <input id="proxy-peers" v-model="proxyPeerConnections" class="connection-settings__checkbox" type="checkbox" />
          <label for="proxy-peers" class="connection-settings__label-inline">Proxy Peer Connections</label>
        </div>
        <div class="connection-settings__checkbox-row">
          <input id="proxy-trackers" v-model="proxyTrackerConnections" class="connection-settings__checkbox" type="checkbox" />
          <label for="proxy-trackers" class="connection-settings__label-inline">Proxy Tracker Connections</label>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.connection-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.connection-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.connection-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.connection-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.connection-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.connection-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.connection-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.connection-settings__input {
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

.connection-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.connection-settings__input--disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.connection-settings__input::placeholder {
  color: var(--text-tertiary);
}

.connection-settings__select {
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

.connection-settings__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.connection-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.connection-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.connection-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.connection-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

@media (max-width: 640px) {
  .connection-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
