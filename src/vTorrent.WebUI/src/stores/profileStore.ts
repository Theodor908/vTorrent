import { ref, readonly } from 'vue';
import { defineStore } from 'pinia';
import type { ProfileMeta, ScheduleGridCell } from '@/types/profile';
import * as api from '@/api/profiles';

export const useProfileStore = defineStore('profile', () => {
  const profiles = ref<ProfileMeta[]>([]);
  const activeProfileName = ref('Balanced');
  const activeProfileColor = ref('#2196F3');
  const scheduleEnabled = ref(false);
  const scheduleGrid = ref<ScheduleGridCell[][] | null>(null);

  async function loadProfiles(): Promise<void> {
    try {
      profiles.value = await api.getProfiles();
    } catch (err) {
      console.error('Failed to load profiles:', err);
    }
  }

  async function loadActiveState(): Promise<void> {
    try {
      const state = await api.getActiveProfile();
      activeProfileName.value = state.name;
      activeProfileColor.value = state.color;
      scheduleEnabled.value = state.scheduleEnabled;
    } catch (err) {
      console.error('Failed to load active profile state:', err);
    }
  }

  async function activateProfile(name: string): Promise<void> {
    await api.activateProfile(name);
    activeProfileName.value = name;
    const profile = profiles.value.find((p) => p.name === name);
    if (profile) activeProfileColor.value = profile.color;
  }

  async function loadSchedule(): Promise<void> {
    try {
      const data = await api.getSchedule();
      scheduleEnabled.value = data.enabled;
      scheduleGrid.value = data.grid;
    } catch (err) {
      console.error('Failed to load schedule:', err);
    }
  }

  async function toggleSchedule(enabled: boolean): Promise<void> {
    await api.toggleSchedule(enabled);
    scheduleEnabled.value = enabled;
  }

  async function refreshActiveState(): Promise<void> {
    await loadActiveState();
  }

  return {
    profiles: readonly(profiles),
    activeProfileName: readonly(activeProfileName),
    activeProfileColor: readonly(activeProfileColor),
    scheduleEnabled: readonly(scheduleEnabled),
    scheduleGrid: readonly(scheduleGrid),
    loadProfiles,
    loadActiveState,
    activateProfile,
    loadSchedule,
    toggleSchedule,
    refreshActiveState,
  };
});
