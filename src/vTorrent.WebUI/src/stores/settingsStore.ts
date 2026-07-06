// settingsStore.ts — Global settings store
// Loads and saves GlobalSettings via the REST API.

import { defineStore } from 'pinia';
import { ref } from 'vue';
import * as sessionApi from '../api/session';
import type { GlobalSettings, UpdateSettingsRequest } from '../types/settings';

export const useSettingsStore = defineStore('settings', () => {
  // ----------------------------------------------------------
  // State
  // ----------------------------------------------------------

  /** Full GlobalSettings object. Null until loadSettings() resolves. */
  const globalSettings = ref<GlobalSettings | null>(null);

  /** True while an API call is in-flight. */
  const isLoading = ref(false);

  // ----------------------------------------------------------
  // Actions
  // ----------------------------------------------------------

  /**
   * loadSettings — fetches GlobalSettings from GET /api/v1/session/settings.
   * Sensitive fields are redacted by the server (empty strings).
   */
  async function loadSettings(): Promise<void> {
    isLoading.value = true;
    try {
      globalSettings.value = await sessionApi.getSettings();
    } finally {
      isLoading.value = false;
    }
  }

  /**
   * saveSettings — sends a partial settings update via PUT /api/v1/session/settings,
   * then reloads the full settings to stay in sync with server-applied defaults.
   */
  async function saveSettings(partial: UpdateSettingsRequest): Promise<void> {
    isLoading.value = true;
    try {
      await sessionApi.updateSettings(partial);
      // Re-fetch so the store reflects any server-side normalization
      globalSettings.value = await sessionApi.getSettings();
    } finally {
      isLoading.value = false;
    }
  }

  return {
    globalSettings,
    isLoading,
    loadSettings,
    saveSettings,
  };
});
