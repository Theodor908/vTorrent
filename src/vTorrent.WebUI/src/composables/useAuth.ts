// useAuth.ts — Thin composable wrapper around authStore for component use.
// Provides reactive auth state and the initialize() lifecycle helper.

import { storeToRefs } from 'pinia';
import { useAuthStore } from '../stores/authStore';
import type { LoginRequest } from '../types/auth';

export function useAuth() {
  const authStore = useAuthStore();

  // Destructure as reactive refs so templates can bind directly
  const { isAuthenticated, isLocalAccess } = storeToRefs(authStore);

  /**
   * login — wraps authStore.login for component call sites.
   */
  async function login(credentials: LoginRequest): Promise<void> {
    await authStore.login(credentials);
  }

  /**
   * logout — wraps authStore.logout.
   */
  async function logout(): Promise<void> {
    await authStore.logout();
  }

  /**
   * initialize — called once at app startup.
   * 1. Checks if the server allows unauthenticated localhost access.
   * 2. If not local, attempts a silent token refresh from sessionStorage.
   * Returns true if the user ends up authenticated by either path.
   */
  async function initialize(): Promise<boolean> {
    // Step 1: probe for local-access bypass
    await authStore.checkLocalAccess();
    if (isAuthenticated.value) {
      return true;
    }

    // Step 2: try to restore session via stored refresh token
    await authStore.refreshToken();
    return isAuthenticated.value;
  }

  return {
    isAuthenticated,
    isLocalAccess,
    login,
    logout,
    initialize,
  };
}
