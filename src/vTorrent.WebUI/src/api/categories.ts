// categories.ts — /api/v1/categories endpoints
// Mirrors CategoriesController.cs exactly.

import { apiClient } from './client';

// ============================================================
// Category — mirrors vTorrent.Abstractions.Records.Category
// ============================================================

export interface Category {
  id: number;
  name: string;
  color: string | null;
  savePath: string | null;
  sortOrder: number;
  createdAt: number; // Unix timestamp
  updatedAt: number; // Unix timestamp
}

// ============================================================
// Request types — mirrors CreateCategoryRequest / UpdateCategoryRequest
// ============================================================

export interface CreateCategoryRequest {
  name: string;
  color?: string | null;
  savePath?: string | null;
}

export interface UpdateCategoryRequest {
  name: string;
  color?: string | null;
  savePath?: string | null;
}

// ============================================================
// GET /api/v1/categories
// ============================================================

export async function getCategories(): Promise<Category[]> {
  const res = await apiClient.get<Category[]>('/categories');
  return res.data;
}

// ============================================================
// POST /api/v1/categories
// Returns the created Category (201 Created).
// ============================================================

export async function createCategory(request: CreateCategoryRequest): Promise<Category> {
  const res = await apiClient.post<Category>('/categories', request);
  return res.data;
}

// ============================================================
// PUT /api/v1/categories/{id}
// ============================================================

export async function updateCategory(id: number, request: UpdateCategoryRequest): Promise<void> {
  await apiClient.put(`/categories/${id}`, request);
}

// ============================================================
// DELETE /api/v1/categories/{id}
// ============================================================

export async function deleteCategory(id: number): Promise<void> {
  await apiClient.delete(`/categories/${id}`);
}
