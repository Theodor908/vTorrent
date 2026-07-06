/**
 * A saved server profile for connecting to a vTorrent instance.
 * Stored in localStorage — passwords are never persisted.
 */
export interface ServerProfile {
  /** Unique identifier (UUID) */
  id: string;
  /** Display name, e.g., "Home Server" */
  name: string;
  /** Host and port, e.g., "192.168.1.100:8080". Empty string for local. */
  host: string;
  /** Use HTTPS for this connection */
  https: boolean;
  /** Username for login */
  username: string;
  /** Whether to auto-connect on app load */
  isDefault: boolean;
}

/** The local profile representing the server hosting this WebUI. */
export const LOCAL_PROFILE: ServerProfile = {
  id: 'local',
  name: 'Local',
  host: '',
  https: false,
  username: '',
  isDefault: true,
};

/** localStorage key for saved profiles */
export const PROFILES_STORAGE_KEY = 'vt-profiles';

/** localStorage key for active profile ID */
export const ACTIVE_PROFILE_KEY = 'vt-active-profile';
