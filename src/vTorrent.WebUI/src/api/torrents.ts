// torrents.ts — /api/v1/torrents endpoints
// Mirrors TorrentsController.cs exactly.

import { apiClient } from './client';
import type {
  TorrentSnapshot,
  ManagedTorrentView,
  TorrentListParams,
  AddTorrentOptions,
  AddMagnetRequest,
  DeleteTorrentParams,
  SetFilePrioritiesRequest,
  AddTorrentResponse,
} from '../types/torrent';
import type { TorrentSettings } from '../types/settings';

// ============================================================
// GET /api/v1/torrents
// ============================================================

export async function getTorrents(params?: TorrentListParams): Promise<TorrentSnapshot[]> {
  const res = await apiClient.get<TorrentSnapshot[]>('/torrents', { params });
  return res.data;
}

// ============================================================
// GET /api/v1/torrents/{hash}
// ============================================================

export async function getTorrent(hash: string): Promise<TorrentSnapshot> {
  const res = await apiClient.get<TorrentSnapshot>(`/torrents/${hash}`);
  return res.data;
}

// ============================================================
// GET /api/v1/torrents/{hash}/details
// ============================================================

export async function getTorrentDetails(hash: string): Promise<ManagedTorrentView> {
  const res = await apiClient.get<ManagedTorrentView>(`/torrents/${hash}/details`);
  return res.data;
}

// ============================================================
// GET /api/v1/torrents/{hash}/pieces
// Returns an array of booleans (true = complete piece).
// ============================================================

export async function getPieceStates(hash: string): Promise<boolean[]> {
  const res = await apiClient.get<boolean[]>(`/torrents/${hash}/pieces`);
  return res.data;
}

// ============================================================
// POST /api/v1/torrents — multipart/form-data upload
// Returns the info hash of the added torrent.
// ============================================================

export async function addTorrentFile(
  file: File,
  options?: AddTorrentOptions,
): Promise<string> {
  const form = new FormData();
  form.append('torrentFile', file);
  if (options) {
    form.append('options', JSON.stringify(options));
  }
  const res = await apiClient.post<AddTorrentResponse>('/torrents', form, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return res.data.infoHash;
}

// ============================================================
// POST /api/v1/torrents/magnet
// Returns the info hash of the added torrent.
// ============================================================

export async function addMagnet(
  uri: string,
  options?: Omit<AddMagnetRequest, 'magnetUri'>,
): Promise<string> {
  const body: AddMagnetRequest = { magnetUri: uri, ...options };
  const res = await apiClient.post<AddTorrentResponse>('/torrents/magnet', body);
  return res.data.infoHash;
}

// ============================================================
// DELETE /api/v1/torrents/{hash}
// ============================================================

export async function deleteTorrent(hash: string, params?: DeleteTorrentParams): Promise<void> {
  await apiClient.delete(`/torrents/${hash}`, { params });
}

// ============================================================
// Simple POST actions — all return void (204 No Content)
// ============================================================

export async function pauseTorrent(hash: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/pause`);
}

export async function resumeTorrent(hash: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/resume`);
}

export async function forceStartTorrent(hash: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/force-start`);
}

export async function recheckTorrent(hash: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/recheck`);
}

export async function toggleSuperSeed(hash: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/super-seed`);
}

export async function pauseAll(): Promise<void> {
  await apiClient.post('/torrents/pause-all');
}

export async function resumeAll(): Promise<void> {
  await apiClient.post('/torrents/resume-all');
}

// ============================================================
// POST /api/v1/torrents/{hash}/location — { savePath }
// ============================================================

export async function changeLocation(hash: string, savePath: string): Promise<void> {
  await apiClient.post(`/torrents/${hash}/location`, { savePath });
}

// ============================================================
// PUT /api/v1/torrents/{hash}/settings
// ============================================================

export async function applyTorrentSettings(hash: string, settings: TorrentSettings): Promise<void> {
  await apiClient.put(`/torrents/${hash}/settings`, settings);
}

// ============================================================
// PUT /api/v1/torrents/{hash}/files/priorities
// Body: { priorities: [{ fileIndex, priority }] }
// ============================================================

export async function setFilePriorities(
  hash: string,
  priorities: SetFilePrioritiesRequest,
): Promise<void> {
  await apiClient.put(`/torrents/${hash}/files/priorities`, priorities);
}

// ============================================================
// PUT /api/v1/torrents/{hash}/category — { categoryId: number | null }
// ============================================================

export async function setTorrentCategory(
  hash: string,
  categoryId: number | null,
): Promise<void> {
  await apiClient.put(`/torrents/${hash}/category`, { categoryId });
}

// ============================================================
// GET /api/v1/torrents/{hash}/tags — returns string[] (tag names)
// ============================================================

export async function getTorrentTags(hash: string): Promise<string[]> {
  const res = await apiClient.get<string[]>(`/torrents/${hash}/tags`);
  return res.data;
}

// ============================================================
// PUT /api/v1/torrents/{hash}/tags — { tagIds: number[] }
// ============================================================

export async function setTorrentTags(hash: string, tagIds: number[]): Promise<void> {
  await apiClient.put(`/torrents/${hash}/tags`, { tagIds });
}

// ============================================================
// Queue position — POST /api/v1/torrents/{hash}/queue/{position}
// position: 'top' | 'bottom' | 'up' | 'down'
// ============================================================

export type QueuePosition = 'top' | 'bottom' | 'up' | 'down';

export async function setQueuePosition(hash: string, position: QueuePosition): Promise<void> {
  await apiClient.post(`/torrents/${hash}/queue/${position}`);
}
