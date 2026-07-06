// client.ts — Axios instances with JWT interceptors
// Access token is stored in-memory only — never in localStorage or cookies.

import axios from 'axios';
import type { AxiosInstance, InternalAxiosRequestConfig, AxiosResponse } from 'axios';
import { ACTIVE_PROFILE_KEY, PROFILES_STORAGE_KEY } from '@/types/connection';

// ============================================================
// Dynamic base URL — reads active profile from localStorage on module load.
// On profile switch, switchProfile() calls window.location.reload(), so this
// re-executes and picks up the new profile automatically.
// ============================================================

function getActiveBaseUrl(): string {
  try {
    const activeId = localStorage.getItem(ACTIVE_PROFILE_KEY) ?? 'local';
    if (activeId === 'local') return '';
    const raw = localStorage.getItem(PROFILES_STORAGE_KEY);
    const profiles = raw ? JSON.parse(raw) : [];
    const profile = profiles.find((p: any) => p.id === activeId);
    if (!profile || !profile.host) return '';
    const scheme = profile.https ? 'https' : 'http';
    return `${scheme}://${profile.host}`;
  } catch {
    return '';
  }
}

const _baseUrl = getActiveBaseUrl();

// ============================================================
// Per-profile refresh token key — isolates sessions across servers
// ============================================================

function getRefreshTokenKey(): string {
  const activeId = localStorage.getItem(ACTIVE_PROFILE_KEY) ?? 'local';
  return `vt_rt_${activeId}`;
}

// ============================================================
// In-memory token store
// ============================================================

let _accessToken: string | null = null;

export function setAccessToken(token: string | null): void {
  _accessToken = token;
}

export function getAccessToken(): string | null {
  return _accessToken;
}

// ============================================================
// Axios instances
// ============================================================

/** Client for all /api/v1/* endpoints. Attaches JWT and handles 401 refresh. */
export const apiClient: AxiosInstance = axios.create({
  baseURL: `${_baseUrl}/api/v1`,
  headers: { 'Content-Type': 'application/json' },
});

/** Client for /auth/* endpoints. No auth interceptor — used for login/refresh itself. */
export const authClient: AxiosInstance = axios.create({
  baseURL: `${_baseUrl}/auth`,
  headers: { 'Content-Type': 'application/json' },
});

// ============================================================
// Refresh deduplication
// ============================================================

let _refreshPromise: Promise<string> | null = null;

async function performSilentRefresh(): Promise<string> {
  if (_refreshPromise) {
    return _refreshPromise;
  }

  const refreshToken = getStoredRefreshToken();
  if (!refreshToken) {
    throw new Error('No refresh token available');
  }

  _refreshPromise = authClient
    .post<{ accessToken: string; refreshToken: string; expiresIn: number }>('/refresh', {
      refreshToken,
    })
    .then((res) => {
      const { accessToken, refreshToken: newRefreshToken } = res.data;
      setAccessToken(accessToken);
      storeRefreshToken(newRefreshToken);
      return accessToken;
    })
    .finally(() => {
      _refreshPromise = null;
    });

  return _refreshPromise;
}

// ============================================================
// Refresh token persistence (sessionStorage — cleared on tab close)
// This is the only credential stored outside memory.
// Access token remains in-memory only.
// Key is profile-aware so each remote server has its own session.
// ============================================================

export function storeRefreshToken(token: string): void {
  sessionStorage.setItem(getRefreshTokenKey(), token);
}

export function getStoredRefreshToken(): string | null {
  return sessionStorage.getItem(getRefreshTokenKey());
}

export function clearStoredRefreshToken(): void {
  sessionStorage.removeItem(getRefreshTokenKey());
}

// ============================================================
// Request interceptor — attach Bearer token
// ============================================================

apiClient.interceptors.request.use((config: InternalAxiosRequestConfig) => {
  const token = getAccessToken();
  if (token) {
    config.headers = config.headers ?? {};
    config.headers['Authorization'] = `Bearer ${token}`;
  }
  return config;
});

// ============================================================
// Response interceptor — silent refresh on 401
// ============================================================

apiClient.interceptors.response.use(
  (response: AxiosResponse) => response,
  async (error) => {
    const originalRequest = error.config as InternalAxiosRequestConfig & { _retried?: boolean };

    if (error.response?.status === 401 && !originalRequest._retried) {
      originalRequest._retried = true;

      try {
        const newToken = await performSilentRefresh();
        originalRequest.headers = originalRequest.headers ?? {};
        originalRequest.headers['Authorization'] = `Bearer ${newToken}`;
        return apiClient(originalRequest);
      } catch {
        // Refresh failed — clear tokens and redirect to login
        setAccessToken(null);
        clearStoredRefreshToken();
        window.location.href = '/login';
        return Promise.reject(error);
      }
    }

    return Promise.reject(error);
  },
);
