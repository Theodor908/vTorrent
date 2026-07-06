// useTheme.test.ts — Unit tests for the useTheme composable

import { describe, it, expect, beforeEach } from 'vitest'
import { useTheme } from '@/composables/useTheme'

// ============================================================
// Tests
// ============================================================

describe('useTheme', () => {
  beforeEach(() => {
    // Clean slate for each test: clear localStorage and reset data-theme attribute
    localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
  })

  // ----------------------------------------------------------
  // Default state
  // ----------------------------------------------------------

  it('defaults to dark when localStorage has no stored preference', () => {
    const { isDark } = useTheme()
    expect(isDark.value).toBe(true)
  })

  it('applies dark data-theme attribute on first use', () => {
    useTheme()
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
  })

  it('defaults to light when localStorage stores "light"', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    const { isDark } = useTheme()
    expect(isDark.value).toBe(false)
  })

  it('applies light data-theme attribute when stored preference is "light"', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    useTheme()
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  })

  // ----------------------------------------------------------
  // Toggle: dark -> light
  // ----------------------------------------------------------

  it('toggle switches from dark to light', () => {
    const { isDark, toggle } = useTheme()
    expect(isDark.value).toBe(true)

    toggle()

    expect(isDark.value).toBe(false)
  })

  it('toggle updates data-theme attribute to light', () => {
    const { toggle } = useTheme()
    toggle()
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
  })

  it('toggle persists "light" to localStorage', () => {
    const { toggle } = useTheme()
    toggle()
    expect(localStorage.getItem('vtorrent-theme')).toBe('light')
  })

  // ----------------------------------------------------------
  // Toggle: light -> dark
  // ----------------------------------------------------------

  it('toggle switches from light back to dark', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    const { isDark, toggle } = useTheme()
    expect(isDark.value).toBe(false)

    toggle()

    expect(isDark.value).toBe(true)
  })

  it('toggle updates data-theme attribute back to dark', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    const { toggle } = useTheme()
    toggle()
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
  })

  it('toggle persists "dark" to localStorage when switching back', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    const { toggle } = useTheme()
    toggle()
    expect(localStorage.getItem('vtorrent-theme')).toBe('dark')
  })

  // ----------------------------------------------------------
  // setDark
  // ----------------------------------------------------------

  it('setDark(false) switches to light', () => {
    const { isDark, setDark } = useTheme()
    setDark(false)
    expect(isDark.value).toBe(false)
    expect(document.documentElement.getAttribute('data-theme')).toBe('light')
    expect(localStorage.getItem('vtorrent-theme')).toBe('light')
  })

  it('setDark(true) switches to dark', () => {
    localStorage.setItem('vtorrent-theme', 'light')
    const { isDark, setDark } = useTheme()
    setDark(true)
    expect(isDark.value).toBe(true)
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark')
    expect(localStorage.getItem('vtorrent-theme')).toBe('dark')
  })
})
