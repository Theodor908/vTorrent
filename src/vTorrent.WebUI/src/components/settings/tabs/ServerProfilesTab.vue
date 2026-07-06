<script setup lang="ts">
// ServerProfilesTab.vue — Manage remote server profiles: create, edit, delete, connect.
// The built-in "Local" profile is read-only and cannot be edited or removed.

import { ref, computed } from 'vue';
import { useConnection } from '@/composables/useConnection';
import type { ServerProfile } from '@/types/connection';

const connection = useConnection();

const editingProfile = ref<ServerProfile | null>(null);
const isCreating = ref(false);

const formName = ref('');
const formHost = ref('');
const formHttps = ref(true);
const formUsername = ref('');

function startCreate(): void {
  isCreating.value = true;
  editingProfile.value = null;
  formName.value = '';
  formHost.value = '';
  formHttps.value = true;
  formUsername.value = 'admin';
}

function startEdit(profile: ServerProfile): void {
  if (profile.id === 'local') return;
  isCreating.value = false;
  editingProfile.value = profile;
  formName.value = profile.name;
  formHost.value = profile.host;
  formHttps.value = profile.https;
  formUsername.value = profile.username;
}

function cancelEdit(): void {
  editingProfile.value = null;
  isCreating.value = false;
}

function saveProfile(): void {
  if (isCreating.value) {
    connection.createProfile({
      name: formName.value.trim(),
      host: formHost.value.trim(),
      https: formHttps.value,
      username: formUsername.value.trim(),
      isDefault: false,
    });
  } else if (editingProfile.value) {
    connection.updateProfile(editingProfile.value.id, {
      name: formName.value.trim(),
      host: formHost.value.trim(),
      https: formHttps.value,
      username: formUsername.value.trim(),
    });
  }
  cancelEdit();
}

function handleDelete(id: string): void {
  if (id === 'local') return;
  connection.deleteProfile(id);
}

function handleConnect(id: string): void {
  if (id === connection.activeProfile.value.id) return;
  connection.switchProfile(id);
}

const isFormValid = computed(
  () => formName.value.trim() !== '' && formHost.value.trim() !== ''
);
</script>

<template>
  <div class="profiles-settings">
    <!-- ── Header ── -->
    <section class="profiles-settings__section">
      <h3 class="profiles-settings__section-title">Server Profiles</h3>
      <p class="profiles-settings__description">
        Manage connections to vTorrent instances. The Local profile represents
        this server and cannot be edited or removed.
      </p>
    </section>

    <!-- ── Profile list ── -->
    <section class="profiles-settings__section">
      <div class="profiles-settings__list">
        <div
          v-for="profile in connection.profiles.value"
          :key="profile.id"
          class="profiles-settings__card"
          :class="{
            'profiles-settings__card--active': profile.id === connection.activeProfile.value.id,
            'profiles-settings__card--editing': editingProfile?.id === profile.id,
          }"
        >
          <div class="profiles-settings__card-info">
            <div class="profiles-settings__card-header">
              <span class="profiles-settings__card-name">{{ profile.name }}</span>
              <span
                v-if="profile.id === connection.activeProfile.value.id"
                class="profiles-settings__badge"
              >
                Connected
              </span>
            </div>
            <span class="profiles-settings__card-host">
              {{ profile.host || 'localhost (this server)' }}
            </span>
          </div>
          <div v-if="profile.id !== 'local'" class="profiles-settings__card-actions">
            <button
              class="profiles-settings__button profiles-settings__button--sm profiles-settings__button--secondary"
              :disabled="profile.id === connection.activeProfile.value.id"
              @click="handleConnect(profile.id)"
            >
              Connect
            </button>
            <button
              class="profiles-settings__button profiles-settings__button--sm profiles-settings__button--secondary"
              @click="startEdit(profile)"
            >
              Edit
            </button>
            <button
              class="profiles-settings__button profiles-settings__button--sm profiles-settings__button--danger"
              @click="handleDelete(profile.id)"
            >
              Delete
            </button>
          </div>
        </div>
      </div>

      <button
        v-if="!isCreating && !editingProfile"
        class="profiles-settings__button profiles-settings__add-btn"
        @click="startCreate"
      >
        + Add Profile
      </button>
    </section>

    <!-- ── Inline create / edit form ── -->
    <section v-if="isCreating || editingProfile" class="profiles-settings__section">
      <h3 class="profiles-settings__section-title">
        {{ isCreating ? 'New Profile' : 'Edit Profile' }}
      </h3>
      <div class="profiles-settings__form">
        <div class="profiles-settings__grid">
          <div class="profiles-settings__field">
            <label class="profiles-settings__label" for="prof-name">Name</label>
            <input
              id="prof-name"
              v-model="formName"
              class="profiles-settings__input"
              type="text"
              placeholder="e.g., Home Server"
              spellcheck="false"
            />
          </div>
          <div class="profiles-settings__field">
            <label class="profiles-settings__label" for="prof-host">Host</label>
            <input
              id="prof-host"
              v-model="formHost"
              class="profiles-settings__input"
              type="text"
              placeholder="e.g., 192.168.1.100:8080"
              spellcheck="false"
            />
          </div>
          <div class="profiles-settings__field">
            <label class="profiles-settings__label" for="prof-user">Username</label>
            <input
              id="prof-user"
              v-model="formUsername"
              class="profiles-settings__input"
              type="text"
              placeholder="admin"
              spellcheck="false"
            />
          </div>
          <div class="profiles-settings__field profiles-settings__field--checkbox">
            <div class="profiles-settings__checkbox-row">
              <input
                id="prof-https"
                v-model="formHttps"
                class="profiles-settings__checkbox"
                type="checkbox"
              />
              <label for="prof-https" class="profiles-settings__label-inline">Use HTTPS</label>
            </div>
          </div>
        </div>
        <div class="profiles-settings__form-actions">
          <button
            class="profiles-settings__button profiles-settings__button--secondary"
            @click="cancelEdit"
          >
            Cancel
          </button>
          <button
            class="profiles-settings__button"
            :disabled="!isFormValid"
            @click="saveProfile"
          >
            {{ isCreating ? 'Create' : 'Save' }}
          </button>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.profiles-settings {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-2xl);
}

/* ── Section ── */
.profiles-settings__section {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.profiles-settings__section-title {
  font-size: var(--font-md);
  font-weight: 700;
  color: var(--text-primary);
  letter-spacing: 0.02em;
  padding-bottom: var(--spacing-sm);
  border-bottom: 1px solid var(--border);
}

.profiles-settings__description {
  font-size: var(--font-sm);
  color: var(--text-secondary);
  line-height: 1.5;
}

/* ── Profile list ── */
.profiles-settings__list {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
}

/* ── Profile card ── */
.profiles-settings__card {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--spacing-lg);
  padding: var(--spacing-md) var(--spacing-lg);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  transition: border-color var(--transition-fast);
}

.profiles-settings__card--active {
  border-color: var(--accent-active);
  background: color-mix(in srgb, var(--accent-active) 5%, var(--bg-input));
}

.profiles-settings__card--editing {
  border-color: var(--accent-cyan);
}

.profiles-settings__card-info {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.profiles-settings__card-header {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.profiles-settings__card-name {
  font-size: var(--font-md);
  font-weight: 600;
  color: var(--text-primary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.profiles-settings__card-host {
  font-size: var(--font-sm);
  color: var(--text-tertiary);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* ── Connected badge ── */
.profiles-settings__badge {
  display: inline-block;
  padding: 2px var(--spacing-sm);
  background: color-mix(in srgb, var(--accent-active) 15%, transparent);
  border: 1px solid color-mix(in srgb, var(--accent-active) 35%, transparent);
  border-radius: 9999px;
  font-size: var(--font-xs);
  font-weight: 600;
  color: var(--accent-active);
  letter-spacing: 0.02em;
  white-space: nowrap;
}

/* ── Card action buttons ── */
.profiles-settings__card-actions {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  flex-shrink: 0;
}

/* ── Add profile button ── */
.profiles-settings__add-btn {
  align-self: flex-start;
}

/* ── Edit form ── */
.profiles-settings__form {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xl);
}

.profiles-settings__grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

.profiles-settings__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.profiles-settings__field--checkbox {
  justify-content: flex-end;
}

/* ── Form labels ── */
.profiles-settings__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.profiles-settings__label-inline {
  font-size: var(--font-md);
  font-weight: 400;
  color: var(--text-primary);
  cursor: pointer;
  user-select: none;
}

/* ── Inputs ── */
.profiles-settings__input {
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

.profiles-settings__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

/* ── Checkbox ── */
.profiles-settings__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  height: 36px;
}

.profiles-settings__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

/* ── Form action buttons ── */
.profiles-settings__form-actions {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

/* ── Buttons ── */
.profiles-settings__button {
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

.profiles-settings__button:hover:not(:disabled) {
  opacity: 0.85;
}

.profiles-settings__button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.profiles-settings__button--sm {
  height: 28px;
  padding: 0 var(--spacing-md);
  font-size: var(--font-sm);
}

.profiles-settings__button--secondary {
  background: var(--bg-hover);
  color: var(--text-primary);
  border: 1px solid var(--border);
}

.profiles-settings__button--danger {
  background: var(--status-red);
  color: #fff;
}

/* ── Responsive ── */
@media (max-width: 640px) {
  .profiles-settings__card {
    flex-direction: column;
    align-items: flex-start;
  }

  .profiles-settings__card-actions {
    width: 100%;
    justify-content: flex-end;
  }

  .profiles-settings__grid {
    grid-template-columns: 1fr;
  }
}
</style>
