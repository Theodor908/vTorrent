<script setup lang="ts">
import { PhSun, PhMoon } from '@phosphor-icons/vue';
import { useTheme } from '@/composables/useTheme';

const { isDark, toggle } = useTheme();
</script>

<template>
  <button
    class="theme-toggle"
    :aria-label="`Switch to ${isDark ? 'light' : 'dark'} theme`"
    :title="`Switch to ${isDark ? 'light' : 'dark'} theme`"
    @click="toggle"
  >
    <span class="theme-toggle__icon">
      <PhSun v-if="!isDark" :size="14" weight="bold" />
      <PhMoon v-else :size="14" weight="bold" />
    </span>

    <span class="theme-toggle__track" :class="{ 'theme-toggle__track--dark': isDark }">
      <span class="theme-toggle__thumb" />
    </span>

    <span class="theme-toggle__label">{{ isDark ? 'Dark' : 'Light' }}</span>
  </button>
</template>

<style scoped>
.theme-toggle {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  padding: var(--spacing-xs) var(--spacing-sm);
  border-radius: var(--radius-md);
  color: var(--text-secondary);
  transition: color var(--transition-fast), background-color var(--transition-fast);
  cursor: pointer;
  width: 100%;
}

.theme-toggle:hover {
  color: var(--text-primary);
  background: var(--bg-hover);
}

.theme-toggle__icon {
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  color: var(--accent-active);
}

.theme-toggle__track {
  position: relative;
  display: inline-flex;
  align-items: center;
  width: 32px;
  height: 18px;
  border-radius: 9px;
  background: var(--bg-input);
  border: 1px solid var(--border);
  transition: background-color var(--transition-fast), border-color var(--transition-fast);
  flex-shrink: 0;
}

.theme-toggle__track--dark {
  background: var(--accent-active);
  border-color: var(--accent-active);
}

.theme-toggle__thumb {
  position: absolute;
  left: 2px;
  width: 12px;
  height: 12px;
  border-radius: 50%;
  background: var(--text-secondary);
  transition: transform var(--transition-fast), background-color var(--transition-fast);
}

.theme-toggle__track--dark .theme-toggle__thumb {
  transform: translateX(14px);
  background: var(--bg-primary);
}

.theme-toggle__label {
  font-size: var(--font-xs);
  font-weight: 500;
  letter-spacing: 0.03em;
  flex: 1;
}
</style>
