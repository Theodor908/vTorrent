// router.ts — Vue Router configuration with hash history and auth guards.
// Uses createWebHashHistory() for portability (no server-side routing required).

import { createRouter, createWebHashHistory } from 'vue-router';
import { useAuthStore } from './stores/authStore';

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/',
      name: 'dashboard',
      component: () => import('@/views/DashboardView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/torrent/:hash',
      name: 'torrent-details',
      component: () => import('@/views/TorrentDetailsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/settings',
      name: 'settings',
      component: () => import('@/views/SettingsView.vue'),
      meta: { requiresAuth: true },
    },
    {
      path: '/login',
      name: 'login',
      component: () => import('@/views/LoginView.vue'),
    },
  ],
});

// Navigation guard — enforce auth requirements.
router.beforeEach((to) => {
  const authStore = useAuthStore();

  // Redirect unauthenticated users away from protected routes.
  if (to.meta.requiresAuth && !authStore.isAuthenticated) {
    return { name: 'login' };
  }

  // Redirect already-authenticated users away from the login page.
  if (to.name === 'login' && authStore.isAuthenticated) {
    return { name: 'dashboard' };
  }

  // Allow navigation to proceed.
  return true;
});

export default router;
