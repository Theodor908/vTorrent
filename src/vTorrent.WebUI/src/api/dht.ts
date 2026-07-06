// dht.ts — /api/v1/dht endpoints
// Mirrors DhtController.cs exactly.

import { apiClient } from './client';

// ============================================================
// DhtStatus — inline type matching the anonymous object
// returned by GET /api/v1/dht from DhtController.cs:
// { isRunning, isEnabled, nodeCount }
// ============================================================

export interface DhtStatus {
  isRunning: boolean;
  isEnabled: boolean;
  nodeCount: number;
}

// ============================================================
// GET /api/v1/dht
// ============================================================

export async function getDhtStatus(): Promise<DhtStatus> {
  const res = await apiClient.get<DhtStatus>('/dht');
  return res.data;
}

// ============================================================
// POST /api/v1/dht/toggle
// ============================================================

export async function toggleDht(): Promise<void> {
  await apiClient.post('/dht/toggle');
}
