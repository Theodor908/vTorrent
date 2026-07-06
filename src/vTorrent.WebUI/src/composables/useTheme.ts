// useTheme.ts — Dark/light theme toggle with localStorage persistence.
// Module-level singleton: isDark ref is shared across all useTheme() callers.

import { ref } from 'vue';

const THEME_KEY = 'vtorrent-theme';

// Module-level singleton state
const isDark = ref(localStorage.getItem(THEME_KEY) !== 'light');

function applyTheme(dark: boolean): void {
  document.documentElement.setAttribute('data-theme', dark ? 'dark' : 'light');
}

// Apply on module load
applyTheme(isDark.value);

export function useTheme() {
  // Re-sync from localStorage on each call so external changes (other tabs,
  // or test harness resetting localStorage) are picked up.
  const stored = localStorage.getItem(THEME_KEY);
  const shouldBeDark = stored !== 'light';
  if (isDark.value !== shouldBeDark) {
    isDark.value = shouldBeDark;
  }
  applyTheme(isDark.value);

  function toggle(): void {
    isDark.value = !isDark.value;
    applyTheme(isDark.value);
    localStorage.setItem(THEME_KEY, isDark.value ? 'dark' : 'light');
  }

  function setDark(value: boolean): void {
    isDark.value = value;
    applyTheme(value);
    localStorage.setItem(THEME_KEY, value ? 'dark' : 'light');
  }

  return { isDark, toggle, setDark };
}
