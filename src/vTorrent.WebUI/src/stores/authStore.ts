// authStore.ts — Authentication state store
// Manages JWT auth state, local-access bypass detection, and token lifecycle.

import { defineStore } from 'pinia';
import { ref } from 'vue';
import * as authApi from '../api/auth';
import type { LoginRequest } from '../types/auth';

export const useAuthStore = defineStore('auth', () => {
  // ============================================================
  // State
  // ============================================================

  /** True when a valid access token is in memory (post-login or post-refresh). */
  const isAuthenticated = ref(false);

  /**
   * True when the server is reachable without credentials (localhost bypass).
   * Checked once at startup by checkLocalAccess().
   */
  const isLocalAccess = ref(false);

  // ============================================================
  // Actions
  // ============================================================

  /**
   * login — POST /auth/login
   * On success the API layer stores tokens; we flip isAuthenticated.
   */
  async function login(credentials: LoginRequest): Promise<void> {
    await authApi.login(credentials);
    isAuthenticated.value = true;
  }

  /**
   * logout — POST /auth/logout
   * Revokes the refresh token server-side and clears all local state.
   */
  async function logout(): Promise<void> {
    await authApi.logout();
    isAuthenticated.value = false;
    isLocalAccess.value = false;
  }

  /**
   * checkLocalAccess — probes whether the server allows unauthenticated localhost access.
   * If yes, we skip the login screen entirely.
   */
  async function checkLocalAccess(): Promise<void> {
    const allowed = await authApi.probeLocalAccess();
    isLocalAccess.value = allowed;
    if (allowed) {
      isAuthenticated.value = true;
    }
  }

  /**
   * refreshToken — silently refreshes the access token using the stored refresh token.
   * On success flips isAuthenticated to true.
   * On failure (no refresh token or server rejection) leaves state unchanged.
   */
  async function refreshToken(): Promise<void> {
    try {
      await authApi.refreshToken();
      isAuthenticated.value = true;
    } catch {
      isAuthenticated.value = false;
    }
  }

  return {
    isAuthenticated,
    isLocalAccess,
    login,
    logout,
    checkLocalAccess,
    refreshToken,
  };
});
