/** Profile metadata — name, color, scope only (no settings values). */
export interface ProfileMeta {
  name: string;
  color: string;
  scope: string;
}

/** Active profile state from GET /api/v1/profiles/active. */
export interface ActiveProfileState {
  name: string;
  color: string;
  scheduleEnabled: boolean;
}

/** A single cell in the 7x24 schedule grid. */
export interface ScheduleGridCell {
  mode: 'Profile' | 'SeedOnly' | 'Paused';
  profileName: string | null;
  color: string;
}

/** Full schedule from GET /api/v1/profiles/schedule. */
export interface ScheduleData {
  enabled: boolean;
  grid: ScheduleGridCell[][];
}

/** Result of importing a schedule package. */
export interface ScheduleImportResult {
  success: boolean;
  importedProfiles: string[];
  renamedProfiles: Record<string, string>;
  skippedProfiles: string[];
  warnings: string[];
}
