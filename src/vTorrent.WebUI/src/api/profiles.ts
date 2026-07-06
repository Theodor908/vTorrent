import { apiClient } from './client';
import type { ProfileMeta, ActiveProfileState, ScheduleData, ScheduleImportResult } from '@/types/profile';

export async function getProfiles(): Promise<ProfileMeta[]> {
  const res = await apiClient.get<ProfileMeta[]>('/profiles');
  return res.data;
}

export async function getActiveProfile(): Promise<ActiveProfileState> {
  const res = await apiClient.get<ActiveProfileState>('/profiles/active');
  return res.data;
}

export async function activateProfile(name: string): Promise<void> {
  await apiClient.put('/profiles/active', { name });
}

export async function getSchedule(): Promise<ScheduleData> {
  const res = await apiClient.get<ScheduleData>('/profiles/schedule');
  return res.data;
}

export async function toggleSchedule(enabled: boolean): Promise<void> {
  await apiClient.put('/profiles/schedule/toggle', { enabled });
}

/** GET /api/v1/profiles/schedule/export — download .vtschedule.json file. */
export async function exportSchedule(): Promise<void> {
  const res = await apiClient.get('/profiles/schedule/export', { responseType: 'blob' });
  const blob = new Blob([res.data], { type: 'application/json' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'schedule.vtschedule.json';
  a.click();
  URL.revokeObjectURL(url);
}

/** POST /api/v1/profiles/schedule/import — upload .vtschedule.json file. */
export async function importSchedule(file: File): Promise<ScheduleImportResult> {
  const formData = new FormData();
  formData.append('file', file);
  const res = await apiClient.post<ScheduleImportResult>('/profiles/schedule/import', formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res.data;
}
