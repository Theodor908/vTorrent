// tags.ts — /api/v1/tags endpoints
// Mirrors TagsController.cs exactly.

import { apiClient } from './client';

// ============================================================
// Tag — mirrors vTorrent.Abstractions.Records.Tag
// ============================================================

export interface Tag {
  id: number;
  name: string;
  color: string | null;
  sortOrder: number;
  createdAt: number; // Unix timestamp
  updatedAt: number; // Unix timestamp
}

// ============================================================
// Request types — mirrors CreateTagRequest / UpdateTagRequest
// ============================================================

export interface CreateTagRequest {
  name: string;
  color?: string | null;
}

export interface UpdateTagRequest {
  name: string;
  color?: string | null;
}

// ============================================================
// GET /api/v1/tags
// ============================================================

export async function getTags(): Promise<Tag[]> {
  const res = await apiClient.get<Tag[]>('/tags');
  return res.data;
}

// ============================================================
// POST /api/v1/tags
// Returns the created Tag (201 Created).
// ============================================================

export async function createTag(request: CreateTagRequest): Promise<Tag> {
  const res = await apiClient.post<Tag>('/tags', request);
  return res.data;
}

// ============================================================
// PUT /api/v1/tags/{id}
// ============================================================

export async function updateTag(id: number, request: UpdateTagRequest): Promise<void> {
  await apiClient.put(`/tags/${id}`, request);
}

// ============================================================
// DELETE /api/v1/tags/{id}
// ============================================================

export async function deleteTag(id: number): Promise<void> {
  await apiClient.delete(`/tags/${id}`);
}
