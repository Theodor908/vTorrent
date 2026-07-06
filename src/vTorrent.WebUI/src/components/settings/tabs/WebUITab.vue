<script setup lang="ts">
// WebUITab.vue — WebUI settings: authentication, allowed origins, advanced security, API keys.

import { computed, ref, watch } from 'vue';
import type { GlobalSettings } from '@/types/settings';
import { changePassword, listApiKeys, createApiKey, revokeApiKey } from '@/api/auth';
import type { ApiKeyListItem } from '@/types/auth';
import { useToast } from '@/composables/useToast';

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

const { showToast } = useToast();

// ── Authentication ──
const localUsername = computed({
  get: () => props.settings.server.localUsername,
  set: (v: string) => patch('server', 'localUsername', v),
});

const currentPassword = ref('');
const newPassword = ref('');
const passwordSaving = ref(false);

async function handlePasswordChange(): Promise<void> {
  if (!currentPassword.value || !newPassword.value) return;
  passwordSaving.value = true;
  try {
    await changePassword({ currentPassword: currentPassword.value, newPassword: newPassword.value });
    showToast('Password changed successfully.', 'success');
    currentPassword.value = '';
    newPassword.value = '';
  } catch {
    showToast('Failed to change password.', 'error');
  } finally {
    passwordSaving.value = false;
  }
}

// ── Access ──
const allowedOrigins = computed({
  get: () => props.settings.server.allowedOrigins,
  set: (v: string) => patch('server', 'allowedOrigins', v),
});

// ── Advanced Security (collapsible) ──
const securityExpanded = ref(false);

const enableCsrfProtection = computed({
  get: () => props.settings.server.enableCsrfProtection,
  set: (v: boolean) => patch('server', 'enableCsrfProtection', v),
});
const enableClickjackingProtection = computed({
  get: () => props.settings.server.enableClickjackingProtection,
  set: (v: boolean) => patch('server', 'enableClickjackingProtection', v),
});
const enableSecurityHeaders = computed({
  get: () => props.settings.server.enableSecurityHeaders,
  set: (v: boolean) => patch('server', 'enableSecurityHeaders', v),
});
const enableSecureCookie = computed({
  get: () => props.settings.server.enableSecureCookie,
  set: (v: boolean) => patch('server', 'enableSecureCookie', v),
});
const verboseSecurityErrors = computed({
  get: () => props.settings.server.verboseSecurityErrors,
  set: (v: boolean) => patch('server', 'verboseSecurityErrors', v),
});

// Host Validation
const enableHostHeaderValidation = computed({
  get: () => props.settings.server.enableHostHeaderValidation,
  set: (v: boolean) => patch('server', 'enableHostHeaderValidation', v),
});
const allowedHostnames = computed({
  get: () => props.settings.server.allowedHostnames,
  set: (v: string) => patch('server', 'allowedHostnames', v),
});

// Reverse Proxy
const enableReverseProxySupport = computed({
  get: () => props.settings.server.enableReverseProxySupport,
  set: (v: boolean) => patch('server', 'enableReverseProxySupport', v),
});
const trustedProxies = computed({
  get: () => props.settings.server.trustedProxies,
  set: (v: string) => patch('server', 'trustedProxies', v),
});

// Brute Force
const maxAuthFailCount = computed({
  get: () => props.settings.server.maxAuthFailCount,
  set: (v: number) => patch('server', 'maxAuthFailCount', v),
});
const authBanDurationSeconds = computed({
  get: () => props.settings.server.authBanDurationSeconds,
  set: (v: number) => patch('server', 'authBanDurationSeconds', v),
});

// Subnet Bypass
const enableSubnetAuthBypass = computed({
  get: () => props.settings.server.enableSubnetAuthBypass,
  set: (v: boolean) => patch('server', 'enableSubnetAuthBypass', v),
});
const authBypassSubnets = computed({
  get: () => props.settings.server.authBypassSubnets,
  set: (v: string) => patch('server', 'authBypassSubnets', v),
});

// API Keys
const apiKeysEnabled = computed({
  get: () => props.settings.server.apiKeysEnabled,
  set: (v: boolean) => patch('server', 'apiKeysEnabled', v),
});

const apiKeys = ref<ApiKeyListItem[]>([]);
const apiKeysLoading = ref(false);
const apiKeyCreating = ref(false);
const showCreateForm = ref(false);
const newKeyLabel = ref('');
const createdKeyFull = ref<string | null>(null);
const keyCopied = ref(false);

async function loadApiKeys(): Promise<void> {
  apiKeysLoading.value = true;
  try {
    apiKeys.value = await listApiKeys();
  } catch {
    showToast('Failed to load API keys.', 'error');
  } finally {
    apiKeysLoading.value = false;
  }
}

// Auto-load keys when section is expanded and API keys are enabled
watch(
  [securityExpanded, () => props.settings.server.apiKeysEnabled],
  ([expanded, enabled]) => {
    if (expanded && enabled) {
      loadApiKeys();
    }
  },
);

async function handleCreateKey(): Promise<void> {
  if (!newKeyLabel.value.trim()) return;
  apiKeyCreating.value = true;
  try {
    const response = await createApiKey(newKeyLabel.value.trim());
    createdKeyFull.value = response.apiKey;
    keyCopied.value = false;
    showCreateForm.value = false;
    newKeyLabel.value = '';
    await loadApiKeys();
  } catch {
    showToast('Failed to create API key.', 'error');
  } finally {
    apiKeyCreating.value = false;
  }
}

async function handleRevokeKey(keyPrefix: string): Promise<void> {
  try {
    await revokeApiKey(keyPrefix);
    showToast('API key revoked.', 'success');
    await loadApiKeys();
  } catch {
    showToast('Failed to revoke API key.', 'error');
  }
}

async function copyCreatedKey(): Promise<void> {
  if (!createdKeyFull.value) return;
  try {
    await navigator.clipboard.writeText(createdKeyFull.value);
    keyCopied.value = true;
  } catch {
    showToast('Failed to copy to clipboard.', 'error');
  }
}

function dismissCreatedKey(): void {
  createdKeyFull.value = null;
  keyCopied.value = false;
}

function formatDate(epochSeconds: number | null): string {
  if (!epochSeconds) return 'Never';
  return new Date(epochSeconds * 1000).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  });
}
</script>

<template>
  <div class="webui-settings">
    <!-- ── Authentication ── -->
    <section class="webui-settings__section">
      <h3 class="webui-settings__section-title">Authentication</h3>
      <div class="webui-settings__grid">
        <div class="webui-settings__field">
          <label class="webui-settings__label" for="webui-user">Username</label>
          <input id="webui-user" v-model="localUsername" class="webui-settings__input" type="text" spellcheck="false" />
        </div>
        <div class="webui-settings__field">
          <label class="webui-settings__label" for="webui-cur-pw">Current Password</label>
          <input id="webui-cur-pw" v-model="currentPassword" class="webui-settings__input" type="password" />
        </div>
        <div class="webui-settings__field">
          <label class="webui-settings__label" for="webui-new-pw">New Password</label>
          <input id="webui-new-pw" v-model="newPassword" class="webui-settings__input" type="password" />
        </div>
        <div class="webui-settings__field webui-settings__field--action">
          <button
            class="webui-settings__button"
            :disabled="passwordSaving || !currentPassword || !newPassword"
            @click="handlePasswordChange"
          >
            {{ passwordSaving ? 'Saving...' : 'Change Password' }}
          </button>
        </div>
      </div>
    </section>

    <!-- ── Access ── -->
    <section class="webui-settings__section">
      <h3 class="webui-settings__section-title">Access</h3>
      <div class="webui-settings__grid">
        <div class="webui-settings__field webui-settings__field--full">
          <label class="webui-settings__label" for="webui-origins">Allowed Origins</label>
          <input id="webui-origins" v-model="allowedOrigins" class="webui-settings__input" type="text" spellcheck="false" />
          <p class="webui-settings__hint">Comma-separated origins, * for all</p>
        </div>
      </div>
    </section>

    <!-- ── Advanced Security (collapsible) ── -->
    <section class="webui-settings__section">
      <button
        class="webui-settings__collapse-header"
        :aria-expanded="securityExpanded"
        @click="securityExpanded = !securityExpanded"
      >
        <span class="webui-settings__collapse-arrow" :class="{ 'webui-settings__collapse-arrow--open': securityExpanded }">&#9654;</span>
        <h3 class="webui-settings__section-title webui-settings__section-title--inline">Advanced Security</h3>
      </button>

      <div v-if="securityExpanded" class="webui-settings__collapse-body">
        <!-- General toggles -->
        <div class="webui-settings__checkboxes">
          <div class="webui-settings__checkbox-row">
            <input id="sec-csrf" v-model="enableCsrfProtection" class="webui-settings__checkbox" type="checkbox" />
            <label for="sec-csrf" class="webui-settings__label-inline">Enable CSRF Protection</label>
          </div>
          <div class="webui-settings__checkbox-row">
            <input id="sec-clickjack" v-model="enableClickjackingProtection" class="webui-settings__checkbox" type="checkbox" />
            <label for="sec-clickjack" class="webui-settings__label-inline">Enable Clickjacking Protection</label>
          </div>
          <div class="webui-settings__checkbox-row">
            <input id="sec-headers" v-model="enableSecurityHeaders" class="webui-settings__checkbox" type="checkbox" />
            <label for="sec-headers" class="webui-settings__label-inline">Enable Security Headers</label>
          </div>
          <div class="webui-settings__checkbox-row">
            <input id="sec-cookie" v-model="enableSecureCookie" class="webui-settings__checkbox" type="checkbox" />
            <label for="sec-cookie" class="webui-settings__label-inline">Enable Secure Cookie</label>
          </div>
          <div class="webui-settings__checkbox-row">
            <input id="sec-verbose" v-model="verboseSecurityErrors" class="webui-settings__checkbox" type="checkbox" />
            <label for="sec-verbose" class="webui-settings__label-inline">Verbose Security Errors</label>
          </div>
        </div>

        <!-- Host Validation -->
        <div class="webui-settings__subsection">
          <h4 class="webui-settings__subsection-title">Host Validation</h4>
          <div class="webui-settings__checkboxes">
            <div class="webui-settings__checkbox-row">
              <input id="sec-host-val" v-model="enableHostHeaderValidation" class="webui-settings__checkbox" type="checkbox" />
              <label for="sec-host-val" class="webui-settings__label-inline">Enable Host Header Validation</label>
            </div>
          </div>
          <div v-if="enableHostHeaderValidation" class="webui-settings__grid">
            <div class="webui-settings__field webui-settings__field--full">
              <label class="webui-settings__label" for="sec-hostnames">Allowed Hostnames</label>
              <input id="sec-hostnames" v-model="allowedHostnames" class="webui-settings__input" type="text" spellcheck="false" />
              <p class="webui-settings__hint">Semicolon-separated hostnames (e.g., myserver.example.com;*.example.com)</p>
            </div>
          </div>
        </div>

        <!-- Reverse Proxy -->
        <div class="webui-settings__subsection">
          <h4 class="webui-settings__subsection-title">Reverse Proxy</h4>
          <div class="webui-settings__checkboxes">
            <div class="webui-settings__checkbox-row">
              <input id="sec-revproxy" v-model="enableReverseProxySupport" class="webui-settings__checkbox" type="checkbox" />
              <label for="sec-revproxy" class="webui-settings__label-inline">Enable Reverse Proxy Support</label>
            </div>
          </div>
          <div v-if="enableReverseProxySupport" class="webui-settings__grid">
            <div class="webui-settings__field webui-settings__field--full">
              <label class="webui-settings__label" for="sec-proxies">Trusted Proxies</label>
              <input id="sec-proxies" v-model="trustedProxies" class="webui-settings__input" type="text" spellcheck="false" />
              <p class="webui-settings__hint">Semicolon-separated IPs or CIDR ranges (e.g., 10.0.0.1;172.16.0.0/12)</p>
            </div>
          </div>
        </div>

        <!-- Brute Force Protection -->
        <div class="webui-settings__subsection">
          <h4 class="webui-settings__subsection-title">Brute Force Protection</h4>
          <div class="webui-settings__grid">
            <div class="webui-settings__field">
              <label class="webui-settings__label" for="sec-max-fail">Max Auth Failures</label>
              <input id="sec-max-fail" v-model.number="maxAuthFailCount" class="webui-settings__input" type="number" min="1" />
            </div>
            <div class="webui-settings__field">
              <label class="webui-settings__label" for="sec-ban-dur">Ban Duration (seconds)</label>
              <input id="sec-ban-dur" v-model.number="authBanDurationSeconds" class="webui-settings__input" type="number" min="0" />
            </div>
          </div>
        </div>

        <!-- Subnet Auth Bypass -->
        <div class="webui-settings__subsection">
          <h4 class="webui-settings__subsection-title">Subnet Auth Bypass</h4>
          <div class="webui-settings__checkboxes">
            <div class="webui-settings__checkbox-row">
              <input id="sec-subnet" v-model="enableSubnetAuthBypass" class="webui-settings__checkbox" type="checkbox" />
              <label for="sec-subnet" class="webui-settings__label-inline">Enable Subnet Auth Bypass</label>
            </div>
          </div>
          <div v-if="enableSubnetAuthBypass" class="webui-settings__grid">
            <div class="webui-settings__field webui-settings__field--full">
              <label class="webui-settings__label" for="sec-subnets">Bypass Subnets</label>
              <input id="sec-subnets" v-model="authBypassSubnets" class="webui-settings__input" type="text" spellcheck="false" />
              <p class="webui-settings__hint">Semicolon-separated CIDR ranges (e.g., 192.168.1.0/24;10.0.0.0/8)</p>
            </div>
            <div class="webui-settings__field webui-settings__field--full">
              <p class="webui-settings__warning">Clients from these subnets will bypass authentication entirely. Only use on trusted private networks.</p>
            </div>
          </div>
        </div>

        <!-- API Keys -->
        <div class="webui-settings__subsection">
          <h4 class="webui-settings__subsection-title">API Keys</h4>
          <div class="webui-settings__checkboxes">
            <div class="webui-settings__checkbox-row">
              <input id="sec-apikeys" v-model="apiKeysEnabled" class="webui-settings__checkbox" type="checkbox" />
              <label for="sec-apikeys" class="webui-settings__label-inline">Enable API Keys</label>
            </div>
          </div>

          <div v-if="apiKeysEnabled" class="webui-settings__apikeys">
            <!-- Created key banner (shown once after creation) -->
            <div v-if="createdKeyFull" class="webui-settings__key-banner">
              <p class="webui-settings__key-banner-warn">Copy this key now. It will not be shown again.</p>
              <div class="webui-settings__key-banner-row">
                <code class="webui-settings__key-banner-code">{{ createdKeyFull }}</code>
                <button class="webui-settings__button webui-settings__button--sm" @click="copyCreatedKey">
                  {{ keyCopied ? 'Copied' : 'Copy' }}
                </button>
                <button class="webui-settings__button webui-settings__button--sm webui-settings__button--secondary" @click="dismissCreatedKey">
                  Dismiss
                </button>
              </div>
            </div>

            <!-- Key list -->
            <div v-if="apiKeysLoading" class="webui-settings__hint">Loading keys...</div>
            <table v-else-if="apiKeys.length > 0" class="webui-settings__table">
              <thead>
                <tr>
                  <th>Prefix</th>
                  <th>Label</th>
                  <th>Created</th>
                  <th>Last Used</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="key in apiKeys" :key="key.keyPrefix" :class="{ 'webui-settings__table-row--revoked': key.isRevoked }">
                  <td><code>{{ key.keyPrefix }}...</code></td>
                  <td>{{ key.label }}</td>
                  <td>{{ formatDate(key.createdAt) }}</td>
                  <td>{{ formatDate(key.lastUsed) }}</td>
                  <td>
                    <button
                      v-if="!key.isRevoked"
                      class="webui-settings__button webui-settings__button--sm webui-settings__button--danger"
                      @click="handleRevokeKey(key.keyPrefix)"
                    >
                      Revoke
                    </button>
                    <span v-else class="webui-settings__hint">Revoked</span>
                  </td>
                </tr>
              </tbody>
            </table>
            <p v-else class="webui-settings__hint">No API keys created yet.</p>

            <!-- Create key form -->
            <div v-if="showCreateForm" class="webui-settings__create-key">
              <div class="webui-settings__field">
                <label class="webui-settings__label" for="sec-key-label">Key Label</label>
                <input
                  id="sec-key-label"
                  v-model="newKeyLabel"
                  class="webui-settings__input"
                  type="text"
                  spellcheck="false"
                  placeholder="e.g., Automation script"
                  @keyup.enter="handleCreateKey"
                />
              </div>
              <div class="webui-settings__create-key-actions">
                <button
                  class="webui-settings__button"
                  :disabled="apiKeyCreating || !newKeyLabel.trim()"
                  @click="handleCreateKey"
                >
                  {{ apiKeyCreating ? 'Creating...' : 'Create' }}
                </button>
                <button
                  class="webui-settings__button webui-settings__button--secondary"
                  @click="showCreateForm = false; newKeyLabel = ''"
                >
                  Cancel
                </button>
              </div>
            </div>
            <button
              v-else
              class="webui-settings__button"
              @click="showCreateForm = true"
            >
              Generate Key
            </button>
          </div>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.webui-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

.webui-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.webui-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.webui-settings__section-title--inline {
  border-bottom: none;
  padding-bottom: 0;
}

.webui-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.webui-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.webui-settings__field--full {
  grid-column: 1 / -1;
}

.webui-settings__field--action {
  display: flex;
  justify-content: flex-start;
  align-items: flex-end;
}

.webui-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.webui-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

.webui-settings__input {
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

.webui-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.webui-settings__button {
  height: 36px;
  padding: 0 var(--spacing-lg);
  background: var(--accent-active);
  color: var(--bg-primary);
  border: none;
  border-radius: var(--radius-md);
  font-size: var(--font-md);
  font-weight: 600;
  cursor: pointer;
  transition: opacity var(--transition-fast);
  white-space: nowrap;
}

.webui-settings__button:hover:not(:disabled) {
  opacity: 0.85;
}

.webui-settings__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.webui-settings__button--sm {
  height: 28px;
  padding: 0 var(--spacing-md);
  font-size: var(--font-sm);
}

.webui-settings__button--secondary {
  background: var(--bg-hover);
  color: var(--text-primary);
  border: 1px solid var(--border);
}

.webui-settings__button--danger {
  background: var(--status-red);
  color: #fff;
}

.webui-settings__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  line-height: 1.4;
}

.webui-settings__warning {
  font-size: var(--font-sm);
  color: var(--status-yellow, #e2a308);
  line-height: 1.5;
  padding: var(--spacing-sm) var(--spacing-md);
  background: color-mix(in srgb, var(--status-yellow, #e2a308) 8%, transparent);
  border-radius: var(--radius-md);
  border: 1px solid color-mix(in srgb, var(--status-yellow, #e2a308) 20%, transparent);
}

/* ── Collapsible section ── */
.webui-settings__collapse-header {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  background: none;
  border: none;
  padding: 0;
  cursor: pointer;
  width: 100%;
  text-align: left;
}

.webui-settings__collapse-arrow {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  transition: transform var(--transition-fast);
  flex-shrink: 0;
}

.webui-settings__collapse-arrow--open {
  transform: rotate(90deg);
}

.webui-settings__collapse-body {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xl);
  padding-left: var(--spacing-md);
}

/* ── Checkboxes ── */
.webui-settings__checkboxes {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.webui-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.webui-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

/* ── Subsection ── */
.webui-settings__subsection {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
}

.webui-settings__subsection-title {
  font-size: var(--font-sm);
  font-weight: 700;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

/* ── API Keys ── */
.webui-settings__apikeys {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.webui-settings__table {
  width: 100%;
  border-collapse: collapse;
  font-size: var(--font-sm);
}

.webui-settings__table th {
  text-align: left;
  font-weight: 600;
  color: var(--text-secondary);
  padding: var(--spacing-xs) var(--spacing-sm);
  border-bottom: 1px solid var(--border);
  text-transform: uppercase;
  font-size: var(--font-xs);
  letter-spacing: 0.03em;
}

.webui-settings__table td {
  padding: var(--spacing-sm);
  color: var(--text-primary);
  border-bottom: 1px solid color-mix(in srgb, var(--border) 50%, transparent);
}

.webui-settings__table code {
  font-family: var(--font-mono, monospace);
  font-size: var(--font-xs);
  color: var(--text-secondary);
}

.webui-settings__table-row--revoked td {
  opacity: 0.5;
}

.webui-settings__key-banner {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
  padding: var(--spacing-md);
  background: color-mix(in srgb, var(--accent-active) 6%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-active) 20%, transparent);
  border-radius: var(--radius-md);
}

.webui-settings__key-banner-warn {
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--status-yellow, #e2a308);
}

.webui-settings__key-banner-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  flex-wrap: wrap;
}

.webui-settings__key-banner-code {
  font-family: var(--font-mono, monospace);
  font-size: var(--font-sm);
  color: var(--text-primary);
  background: var(--bg-input);
  padding: var(--spacing-xs) var(--spacing-sm);
  border-radius: var(--radius-sm);
  word-break: break-all;
  flex: 1;
  min-width: 200px;
}

.webui-settings__create-key {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-md);
  max-width: 400px;
}

.webui-settings__create-key-actions {
  display: flex;
  gap: var(--spacing-sm);
}

@media (max-width: 640px) {
  .webui-settings__grid {
    grid-template-columns: 1fr;
  }

  .webui-settings__field--full {
    grid-column: 1;
  }
}
</style>
