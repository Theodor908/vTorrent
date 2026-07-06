// useToast.ts — Application-wide toast notification system.
// Provides a composable that surfaces toast messages from anywhere in the app.
// The Toast.vue component reads from this shared reactive queue and renders them.

import { ref } from 'vue';

// ============================================================
// Types
// ============================================================

export type ToastType = 'success' | 'error' | 'info' | 'warning';

export interface Toast {
  id: number;
  message: string;
  type: ToastType;
  duration: number;
  /** Set to true by the dismiss logic to trigger the fade-out CSS transition. */
  dismissing: boolean;
}

// ============================================================
// Shared singleton state — one queue, shared across all callers.
// ============================================================

let _nextId = 1;

const toasts = ref<Toast[]>([]);

// ============================================================
// Composable
// ============================================================

export function useToast() {
  /**
   * showToast — push a new toast into the queue.
   * @param message  Human-readable notification text.
   * @param type     Visual variant: 'success' | 'error' | 'info' | 'warning'.
   * @param duration Auto-dismiss delay in ms. Defaults to 5000.
   */
  function showToast(message: string, type: ToastType = 'info', duration = 5000): void {
    const id = _nextId++;
    const toast: Toast = { id, message, type, duration, dismissing: false };
    toasts.value.push(toast);

    // Schedule auto-dismiss: first trigger the fade-out class, then remove.
    setTimeout(() => {
      dismissToast(id);
    }, duration);
  }

  /**
   * dismissToast — mark a toast as dismissing (triggers CSS fade-out),
   * then remove it from the queue after the transition completes.
   */
  function dismissToast(id: number): void {
    const toast = toasts.value.find((t) => t.id === id);
    if (!toast || toast.dismissing) return;
    toast.dismissing = true;
    // Remove after fade transition (300ms matches CSS animation duration).
    setTimeout(() => {
      toasts.value = toasts.value.filter((t) => t.id !== id);
    }, 350);
  }

  return {
    toasts,
    showToast,
    dismissToast,
  };
}
