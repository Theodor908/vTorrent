<script setup lang="ts">
import {
  PhCheckCircle,
  PhWarning,
  PhInfo,
  PhX,
  PhXCircle,
} from '@phosphor-icons/vue';
import { useToast } from '@/composables/useToast';

const { toasts, dismissToast } = useToast();

function iconForType(type: string) {
  switch (type) {
    case 'success': return PhCheckCircle;
    case 'error':   return PhXCircle;
    case 'warning': return PhWarning;
    default:        return PhInfo;
  }
}
</script>

<template>
  <Teleport to="body">
    <div class="toast-container" aria-live="polite" aria-atomic="false">
      <TransitionGroup name="toast">
        <div
          v-for="toast in toasts"
          :key="toast.id"
          class="toast"
          :class="[`toast--${toast.type}`, { 'toast--dismissing': toast.dismissing }]"
          role="alert"
        >
          <component :is="iconForType(toast.type)" class="toast__icon" :size="18" weight="bold" />
          <span class="toast__message">{{ toast.message }}</span>
          <button
            class="toast__close"
            :aria-label="`Dismiss notification`"
            @click="dismissToast(toast.id)"
          >
            <PhX :size="14" weight="bold" />
          </button>
        </div>
      </TransitionGroup>
    </div>
  </Teleport>
</template>

<style scoped>
.toast-container {
  position: fixed;
  top: calc(var(--header-height) + var(--spacing-lg));
  right: var(--spacing-xl);
  z-index: 9999;
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
  max-width: 380px;
  pointer-events: none;
}

.toast {
  display: flex;
  align-items: flex-start;
  gap: var(--spacing-sm);
  padding: var(--spacing-md) var(--spacing-lg);
  border-radius: var(--radius-lg);
  border: 1px solid transparent;
  background: var(--bg-card);
  box-shadow: var(--shadow-lg), 0 0 0 1px color-mix(in srgb, var(--text-primary) 4%, transparent);
  pointer-events: all;
  min-width: 280px;
  backdrop-filter: blur(12px);
  position: relative;
  overflow: hidden;
}

/* Colored left accent bar */
.toast::before {
  content: '';
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 3px;
}

.toast--success::before { background: var(--status-green); }
.toast--error::before   { background: var(--status-red); }
.toast--warning::before { background: var(--status-orange); }
.toast--info::before    { background: var(--accent-cyan); }

.toast--success { border-color: rgba(16, 185, 129, 0.2); }
.toast--error   { border-color: rgba(239, 68, 68, 0.2); }
.toast--warning { border-color: rgba(245, 158, 11, 0.2); }
.toast--info    { border-color: color-mix(in srgb, var(--accent-active) 20%, transparent); }

.toast__icon {
  flex-shrink: 0;
  margin-top: 1px;
}

.toast--success .toast__icon { color: var(--status-green); }
.toast--error   .toast__icon { color: var(--status-red); }
.toast--warning .toast__icon { color: var(--status-orange); }
.toast--info    .toast__icon { color: var(--accent-cyan); }

.toast__message {
  flex: 1;
  font-size: var(--font-sm);
  color: var(--text-primary);
  line-height: 1.5;
  word-break: break-word;
}

.toast__close {
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border-radius: var(--radius-sm);
  color: var(--text-tertiary);
  transition: color var(--transition-fast), background-color var(--transition-fast);
  cursor: pointer;
  margin-top: -1px;
}

.toast__close:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

/* TransitionGroup animations */
.toast-enter-active {
  transition: all 300ms cubic-bezier(0.34, 1.56, 0.64, 1);
}

.toast-leave-active {
  transition: all 300ms cubic-bezier(0.4, 0, 0.2, 1);
}

.toast-enter-from {
  opacity: 0;
  transform: translateX(100%) scale(0.9);
}

.toast-leave-to {
  opacity: 0;
  transform: translateX(100%) scale(0.9);
}

.toast-move {
  transition: transform 300ms cubic-bezier(0.4, 0, 0.2, 1);
}
</style>
