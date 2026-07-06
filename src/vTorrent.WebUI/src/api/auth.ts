// auth.ts — /auth endpoints (NOT under /api/v1)
// AuthController.cs routes: POST /auth/login, POST /auth/refresh, POST /auth/logout,
//                            POST /auth/change-password

import { authClient, apiClient, setAccessToken, storeRefreshToken, clearStoredRefreshToken } from './client';
import type { LoginRequest, LoginResponse, ChangePasswordRequest, CreateApiKeyRequest, CreateApiKeyResponse, ApiKeyListItem } from '../types/auth';

// ============================================================
// login — POST /auth/login
// ============================================================

export async function login(credentials: LoginRequest): Promise<LoginResponse> {
  const res = await authClient.post<LoginResponse>('/login', credentials);
  const data = res.data;
  // Store tokens: access token in-memory, refresh token in sessionStorage
  setAccessToken(data.accessToken);
  storeRefreshToken(data.refreshToken);
  return data;
}

// ============================================================
// logout — POST /auth/logout
// Sends the current refresh token for revocation.
// ============================================================

export async function logout(): Promise<void> {
  const refreshToken = sessionStorage.getItem('vt_rt');
  if (refreshToken) {
    try {
      await authClient.post('/logout', { refreshToken });
    } catch {
      // Best-effort — clear local state regardless
    }
  }
  setAccessToken(null);
  clearStoredRefreshToken();
}

// ============================================================
// refreshToken — POST /auth/refresh
// Returns a new LoginResponse with rotated tokens.
// ============================================================

export async function refreshToken(): Promise<LoginResponse> {
  const rt = sessionStorage.getItem('vt_rt');
  if (!rt) throw new Error('No refresh token stored');
  const res = await authClient.post<LoginResponse>('/refresh', { refreshToken: rt });
  const data = res.data;
  setAccessToken(data.accessToken);
  storeRefreshToken(data.refreshToken);
  return data;
}

// ============================================================
// changePassword — POST /auth/change-password
// Requires valid access token (Authorize attribute on server).
// ============================================================

export async function changePassword(request: ChangePasswordRequest): Promise<void> {
  await apiClient.post('/auth/change-password', request);
}

// ============================================================
// probeLocalAccess — lightweight check for localhost bypass
// Tries a GET to /api/v1/session/counts without auth to detect
// if the server allows unauthenticated localhost access.
// ============================================================

export async function probeLocalAccess(): Promise<boolean> {
  try {
    const res = await fetch('/api/v1/session/counts', {
      method: 'GET',
      credentials: 'omit',
    });
    return res.ok;
  } catch {
    return false;
  }
}

// ============================================================
// listApiKeys — GET /auth/api-keys
// Requires valid access token (Authorize attribute on server).
// ============================================================

export async function listApiKeys(): Promise<ApiKeyListItem[]> {
  const response = await apiClient.get<ApiKeyListItem[]>('/auth/api-keys')
  return response.data
}

// ============================================================
// createApiKey — POST /auth/api-keys
// Requires valid access token (Authorize attribute on server).
// ============================================================

export async function createApiKey(label: string): Promise<CreateApiKeyResponse> {
  const response = await apiClient.post<CreateApiKeyResponse>('/auth/api-keys', { label } as CreateApiKeyRequest)
  return response.data
}

// ============================================================
// revokeApiKey — DELETE /auth/api-keys/{keyPrefix}
// Requires valid access token (Authorize attribute on server).
// ============================================================

export async function revokeApiKey(keyPrefix: string): Promise<void> {
  await apiClient.delete(`/auth/api-keys/${keyPrefix}`)
}
