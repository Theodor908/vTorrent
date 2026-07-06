// useBreakpoint.test.ts — Unit tests for the useBreakpoint composable

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { defineComponent } from 'vue'
import { useBreakpoint } from '@/composables/useBreakpoint'
import type { Breakpoint } from '@/composables/useBreakpoint'

// ============================================================
// Helper — mount the composable inside a component so that
// onMounted / onUnmounted lifecycle hooks are triggered.
// ============================================================

function mountBreakpoint(initialWidth: number) {
  // Set window width before mounting so the composable reads the correct value
  Object.defineProperty(window, 'innerWidth', {
    writable: true,
    configurable: true,
    value: initialWidth,
  })

  let result: ReturnType<typeof useBreakpoint> | undefined

  const TestComponent = defineComponent({
    setup() {
      result = useBreakpoint()
      return {}
    },
    template: '<div />',
  })

  const wrapper = mount(TestComponent)

  return { wrapper, get result() { return result! } }
}

// ============================================================
// Tests
// ============================================================

describe('useBreakpoint', () => {
  afterEach(() => {
    // Reset to a safe desktop width after each test
    Object.defineProperty(window, 'innerWidth', {
      writable: true,
      configurable: true,
      value: 1920,
    })
  })

  // ----------------------------------------------------------
  // desktop (width >= 1280)
  // ----------------------------------------------------------

  it('returns "desktop" for window width exactly 1280', () => {
    const { result } = mountBreakpoint(1280)
    expect(result.breakpoint.value).toBe<Breakpoint>('desktop')
  })

  it('returns "desktop" for window width 1920', () => {
    const { result } = mountBreakpoint(1920)
    expect(result.breakpoint.value).toBe<Breakpoint>('desktop')
  })

  it('returns "desktop" for window width 2560', () => {
    const { result } = mountBreakpoint(2560)
    expect(result.breakpoint.value).toBe<Breakpoint>('desktop')
  })

  // ----------------------------------------------------------
  // tablet (768 <= width < 1280)
  // ----------------------------------------------------------

  it('returns "tablet" for window width exactly 768', () => {
    const { result } = mountBreakpoint(768)
    expect(result.breakpoint.value).toBe<Breakpoint>('tablet')
  })

  it('returns "tablet" for window width 1024', () => {
    const { result } = mountBreakpoint(1024)
    expect(result.breakpoint.value).toBe<Breakpoint>('tablet')
  })

  it('returns "tablet" for window width 1279 (just below desktop)', () => {
    const { result } = mountBreakpoint(1279)
    expect(result.breakpoint.value).toBe<Breakpoint>('tablet')
  })

  // ----------------------------------------------------------
  // mobile (width < 768)
  // ----------------------------------------------------------

  it('returns "mobile" for window width 0', () => {
    const { result } = mountBreakpoint(0)
    expect(result.breakpoint.value).toBe<Breakpoint>('mobile')
  })

  it('returns "mobile" for window width 375', () => {
    const { result } = mountBreakpoint(375)
    expect(result.breakpoint.value).toBe<Breakpoint>('mobile')
  })

  it('returns "mobile" for window width 767 (just below tablet)', () => {
    const { result } = mountBreakpoint(767)
    expect(result.breakpoint.value).toBe<Breakpoint>('mobile')
  })

  // ----------------------------------------------------------
  // Resize events
  // ----------------------------------------------------------

  it('updates breakpoint when window resize event fires', async () => {
    const { result } = mountBreakpoint(1920)
    expect(result.breakpoint.value).toBe<Breakpoint>('desktop')

    // Simulate resize to tablet
    Object.defineProperty(window, 'innerWidth', {
      writable: true,
      configurable: true,
      value: 800,
    })
    window.dispatchEvent(new Event('resize'))

    // Allow Vue reactivity to propagate
    await new Promise(resolve => setTimeout(resolve, 0))

    expect(result.breakpoint.value).toBe<Breakpoint>('tablet')
  })

  it('updates breakpoint from tablet to mobile on resize', async () => {
    const { result } = mountBreakpoint(1024)
    expect(result.breakpoint.value).toBe<Breakpoint>('tablet')

    Object.defineProperty(window, 'innerWidth', {
      writable: true,
      configurable: true,
      value: 400,
    })
    window.dispatchEvent(new Event('resize'))

    await new Promise(resolve => setTimeout(resolve, 0))

    expect(result.breakpoint.value).toBe<Breakpoint>('mobile')
  })
})
