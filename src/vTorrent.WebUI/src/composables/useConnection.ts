import { ref, computed, readonly } from 'vue';
import type { ServerProfile } from '@/types/connection';
import {
  LOCAL_PROFILE,
  PROFILES_STORAGE_KEY,
  ACTIVE_PROFILE_KEY,
} from '@/types/connection';

const _profiles = ref<ServerProfile[]>([]);
const _activeProfileId = ref<string>('local');

/** Load profiles from localStorage on module init */
function _loadFromStorage(): void {
  try {
    const raw = localStorage.getItem(PROFILES_STORAGE_KEY);
    const saved: ServerProfile[] = raw ? JSON.parse(raw) : [];
    const hasLocal = saved.some((p) => p.id === 'local');
    _profiles.value = hasLocal ? saved : [LOCAL_PROFILE, ...saved];
  } catch {
    _profiles.value = [LOCAL_PROFILE];
  }

  const activeId = localStorage.getItem(ACTIVE_PROFILE_KEY);
  _activeProfileId.value = activeId ?? 'local';
}

function _saveToStorage(): void {
  localStorage.setItem(PROFILES_STORAGE_KEY, JSON.stringify(_profiles.value));
}

// Initialize on module load
_loadFromStorage();

export function useConnection() {
  const profiles = readonly(_profiles);

  const activeProfile = computed<ServerProfile>(
    () =>
      _profiles.value.find((p) => p.id === _activeProfileId.value) ??
      LOCAL_PROFILE
  );

  const isRemote = computed(() => activeProfile.value.id !== 'local');

  function getBaseUrl(profile?: ServerProfile): string {
    const p = profile ?? activeProfile.value;
    if (p.id === 'local' || !p.host) return '';
    const scheme = p.https ? 'https' : 'http';
    return `${scheme}://${p.host}`;
  }

  function createProfile(profile: Omit<ServerProfile, 'id'>): ServerProfile {
    const newProfile: ServerProfile = {
      ...profile,
      id: crypto.randomUUID(),
    };
    _profiles.value = [..._profiles.value, newProfile];
    _saveToStorage();
    return newProfile;
  }

  function updateProfile(
    id: string,
    updates: Partial<Omit<ServerProfile, 'id'>>
  ): void {
    if (id === 'local') return;
    _profiles.value = _profiles.value.map((p) =>
      p.id === id ? { ...p, ...updates } : p
    );
    _saveToStorage();
  }

  function deleteProfile(id: string): void {
    if (id === 'local') return;
    _profiles.value = _profiles.value.filter((p) => p.id !== id);
    _saveToStorage();
    if (_activeProfileId.value === id) {
      switchProfile('local');
    }
  }

  function setDefault(id: string): void {
    _profiles.value = _profiles.value.map((p) => ({
      ...p,
      isDefault: p.id === id,
    }));
    _saveToStorage();
  }

  function switchProfile(id: string): void {
    _activeProfileId.value = id;
    localStorage.setItem(ACTIVE_PROFILE_KEY, id);
    window.location.reload();
  }

  return {
    profiles,
    activeProfile,
    isRemote,
    getBaseUrl,
    createProfile,
    updateProfile,
    deleteProfile,
    setDefault,
    switchProfile,
  };
}
