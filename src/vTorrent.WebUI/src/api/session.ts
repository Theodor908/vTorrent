// session.ts — /api/v1/session endpoints
// Mirrors SessionController.cs exactly.

import { apiClient } from './client';
import type { SessionStatistics } from '../types/session';
import type { GlobalSettings, UpdateSettingsRequest } from '../types/settings';

// ============================================================
// SessionCounts — inline type matching the anonymous object
// returned by GET /api/v1/session/counts from SessionController.cs:
// { downloading, seeding, paused, completed }
// ============================================================

export interface SessionCounts {
  downloading: number;
  seeding: number;
  paused: number;
  completed: number;
}

// ============================================================
// GET /api/v1/session/stats
// Returns full SessionStatistics object.
// ============================================================

export async function getSessionStats(): Promise<SessionStatistics> {
  const res = await apiClient.get<SessionStatistics>('/session/stats');
  return res.data;
}

// ============================================================
// GET /api/v1/session/counts
// Returns lightweight torrent count summary.
// ============================================================

export async function getSessionCounts(): Promise<SessionCounts> {
  const res = await apiClient.get<SessionCounts>('/session/counts');
  return res.data;
}

// ============================================================
// GET /api/v1/session/settings
// Returns redacted GlobalSettings (sensitive fields are empty strings).
// ============================================================

export async function getSettings(): Promise<GlobalSettings> {
  const res = await apiClient.get<GlobalSettings>('/session/settings');
  return res.data;
}

// ============================================================
// PUT /api/v1/session/settings
// Body: { settings: Partial<GlobalSettings> }
// The server wraps the partial settings in UpdateSettingsRequest.
// ============================================================

export async function updateSettings(settings: UpdateSettingsRequest): Promise<void> {
  await apiClient.put('/session/settings', { settings });
}
