<script setup lang="ts">
// SettingsTab.vue — Generic data-driven settings tab renderer.
// Accepts an array of SettingsField descriptors and renders the appropriate
// input for each one. Emits update:field(path, value) on every change.

// ============================================================
// Types
// ============================================================

export interface SettingsField {
  label: string;
  /** Dot-notation path into GlobalSettings, e.g. "connection.listenPort" */
  path: string;
  type: 'text' | 'number' | 'checkbox' | 'select' | 'password';
  options?: { label: string; value: string | number }[];
  hint?: string;
  min?: number;
  max?: number;
}

// ============================================================
// Props & Emits
// ============================================================

defineProps<{
  fields: SettingsField[];
}>();

const emit = defineEmits<{
  (e: 'update:field', path: string, value: string | number | boolean): void;
}>();

// ============================================================
// Helpers
// ============================================================

function onInput(field: SettingsField, event: Event): void {
  const target = event.target as HTMLInputElement | HTMLSelectElement;

  let value: string | number | boolean;
  if (field.type === 'checkbox') {
    value = (target as HTMLInputElement).checked;
  } else if (field.type === 'number') {
    const n = parseFloat(target.value);
    value = isNaN(n) ? 0 : n;
  } else {
    value = target.value;
  }

  emit('update:field', field.path, value);
}

function inputId(path: string): string {
  return `settings-field-${path.replace(/\./g, '-')}`;
}
</script>

<template>
  <div class="settings-tab">
    <div
      v-for="field in fields"
      :key="field.path"
      class="settings-tab__field"
      :class="{ 'settings-tab__field--checkbox': field.type === 'checkbox' }"
    >
      <!-- Checkbox: label goes after the control -->
      <template v-if="field.type === 'checkbox'">
        <div class="settings-tab__checkbox-row">
          <input
            :id="inputId(field.path)"
            class="settings-tab__checkbox"
            type="checkbox"
            @change="onInput(field, $event)"
          />
          <label :for="inputId(field.path)" class="settings-tab__label settings-tab__label--inline">
            {{ field.label }}
          </label>
        </div>
      </template>

      <!-- All other types: label above, input below -->
      <template v-else>
        <label :for="inputId(field.path)" class="settings-tab__label">
          {{ field.label }}
        </label>

        <!-- Select -->
        <select
          v-if="field.type === 'select'"
          :id="inputId(field.path)"
          class="settings-tab__select"
          @change="onInput(field, $event)"
        >
          <option
            v-for="opt in field.options"
            :key="String(opt.value)"
            :value="opt.value"
          >
            {{ opt.label }}
          </option>
        </select>

        <!-- Text / number / password -->
        <input
          v-else
          :id="inputId(field.path)"
          class="settings-tab__input"
          :type="field.type"
          :min="field.min"
          :max="field.max"
          @input="onInput(field, $event)"
        />
      </template>

      <!-- Hint text -->
      <p v-if="field.hint" class="settings-tab__hint">{{ field.hint }}</p>
    </div>
  </div>
</template>

<style scoped>
.settings-tab {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--spacing-lg) var(--spacing-xl);
  align-items: start;
}

/* Checkboxes span both columns so they don't appear squished */
.settings-tab__field--checkbox {
  grid-column: 1 / -1;
}

.settings-tab__label {
  display: block;
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.03em;
  text-transform: uppercase;
  margin-bottom: var(--spacing-xs);
}

.settings-tab__label--inline {
  font-size: var(--font-md);
  font-weight: 400;
  text-transform: none;
  letter-spacing: 0;
  color: var(--text-primary);
  margin-bottom: 0;
  cursor: pointer;
}

.settings-tab__input,
.settings-tab__select {
  width: 100%;
  height: 36px;
  padding: 0 var(--spacing-md);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: var(--font-md);
  outline: none;
  transition:
    border-color var(--transition-fast),
    box-shadow var(--transition-fast);
}

.settings-tab__input:focus,
.settings-tab__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.settings-tab__select {
  appearance: none;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8'%3E%3Cpath d='M1 1l5 5 5-5' stroke='%2364748b' stroke-width='1.5' fill='none' stroke-linecap='round'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right var(--spacing-md) center;
  padding-right: var(--spacing-2xl);
  cursor: pointer;
}

.settings-tab__checkbox-row {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
}

.settings-tab__checkbox {
  width: 16px;
  height: 16px;
  accent-color: var(--accent-cyan);
  cursor: pointer;
  flex-shrink: 0;
}

.settings-tab__hint {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
  margin-top: var(--spacing-xs);
  line-height: 1.4;
}

/* Single column on narrow screens */
@media (max-width: 640px) {
  .settings-tab {
    grid-template-columns: 1fr;
  }

  .settings-tab__field--checkbox {
    grid-column: 1;
  }
}
</style>
