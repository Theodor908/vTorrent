// useBreakpoint.ts — Reactive window-size breakpoint detection.
// Breakpoints:
//   mobile  — width < 768px
//   tablet  — 768px ≤ width < 1280px
//   desktop — width ≥ 1280px

import { ref, onMounted, onUnmounted } from 'vue';

// ============================================================
// Type
// ============================================================

export type Breakpoint = 'mobile' | 'tablet' | 'desktop';

// ============================================================
// Helper
// ============================================================

function computeBreakpoint(width: number): Breakpoint {
  if (width < 768) return 'mobile';
  if (width < 1280) return 'tablet';
  return 'desktop';
}

// ============================================================
// Composable
// ============================================================

export function useBreakpoint() {
  const breakpoint = ref<Breakpoint>(
    typeof window !== 'undefined'
      ? computeBreakpoint(window.innerWidth)
      : 'desktop', // SSR-safe default
  );

  function onResize(): void {
    breakpoint.value = computeBreakpoint(window.innerWidth);
  }

  onMounted(() => {
    window.addEventListener('resize', onResize, { passive: true });
    // Sync immediately in case the window was resized before mount
    onResize();
  });

  onUnmounted(() => {
    window.removeEventListener('resize', onResize);
  });

  return {
    breakpoint,
    /** Convenience booleans for common template bindings */
    isMobile: { get value() { return breakpoint.value === 'mobile'; } },
    isTablet: { get value() { return breakpoint.value === 'tablet'; } },
    isDesktop: { get value() { return breakpoint.value === 'desktop'; } },
  };
}
