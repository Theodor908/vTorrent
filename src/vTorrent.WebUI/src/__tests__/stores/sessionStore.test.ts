// sessionStore.test.ts — Unit tests for the session Pinia store

import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'

// Mock the API module so the store can be imported without network calls
vi.mock('@/api/session', () => ({
  getSessionStats: vi.fn().mockResolvedValue(null),
}))

import { useSessionStore } from '@/stores/sessionStore'
import type { SessionStatistics } from '@/types/session'
import type { DhtStatusSnapshot } from '@/stores/sessionStore'

// ============================================================
// Helpers — minimal object factories
// ============================================================

function makeSessionStatistics(overrides: Partial<SessionStatistics> = {}): SessionStatistics {
  return {
    totalBytesSent: 0,
    totalBytesReceived: 0,
    globalDownloadRate: 0,
    globalUploadRate: 0,
    downloadingTorrents: 0,
    seedingTorrents: 0,
    pausedTorrents: 0,
    checkingTorrents: 0,
    errorTorrents: 0,
    uploadOnlyTorrents: 0,
    totalTorrents: 0,
    activeTorrents: 0,
    totalPeersConnected: 0,
    totalConnectedSeeds: 0,
    halfOpenConnections: 0,
    uploadingPeers: 0,
    downloadingPeers: 0,
    unchokedPeers: 0,
    connectionAttempts: 0,
    connectionsRejected: 0,
    dhtNodes: 0,
    dhtNodeCache: 0,
    dhtTorrents: 0,
    dhtBytesSent: 0,
    dhtBytesReceived: 0,
    trackerRequestsSent: 0,
    trackerResponsesReceived: 0,
    trackerErrors: 0,
    diskReadQueue: 0,
    diskWriteQueue: 0,
    diskBytesRead: 0,
    diskBytesWritten: 0,
    diskReadCount: 0,
    diskWriteCount: 0,
    diskCacheSize: 0,
    diskCacheHits: 0,
    diskCacheMisses: 0,
    diskCacheHitRatio: 0,
    piecesPassed: 0,
    piecesFailed: 0,
    piecePassRate: 0,
    sessionStartTime: '2024-01-01T00:00:00Z',
    uptime: '00:00:00',
    isPaused: false,
    listenPort: 6881,
    externalIpAddress: null,
    ...overrides,
  }
}

function makeDhtStatus(overrides: Partial<DhtStatusSnapshot> = {}): DhtStatusSnapshot {
  return {
    isRunning: true,
    isEnabled: true,
    nodeCount: 0,
    ...overrides,
  }
}

// ============================================================
// Tests
// ============================================================

describe('sessionStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  // ----------------------------------------------------------
  // Initial state
  // ----------------------------------------------------------

  it('stats is null initially', () => {
    const store = useSessionStore()
    expect(store.stats).toBeNull()
  })

  it('dhtStatus is null initially', () => {
    const store = useSessionStore()
    expect(store.dhtStatus).toBeNull()
  })

  // ----------------------------------------------------------
  // updateStats
  // ----------------------------------------------------------

  it('updateStats sets stats correctly', () => {
    const store = useSessionStore()
    const incoming = makeSessionStatistics({
      globalDownloadRate: 1024,
      globalUploadRate: 512,
      totalTorrents: 5,
      downloadingTorrents: 3,
    })

    store.updateStats(incoming)

    expect(store.stats).toEqual(incoming)
    expect(store.stats!.globalDownloadRate).toBe(1024)
    expect(store.stats!.globalUploadRate).toBe(512)
    expect(store.stats!.totalTorrents).toBe(5)
    expect(store.stats!.downloadingTorrents).toBe(3)
  })

  it('updateStats replaces previous stats', () => {
    const store = useSessionStore()
    store.updateStats(makeSessionStatistics({ globalDownloadRate: 100 }))
    store.updateStats(makeSessionStatistics({ globalDownloadRate: 999 }))

    expect(store.stats!.globalDownloadRate).toBe(999)
  })

  // ----------------------------------------------------------
  // updateDhtStatus
  // ----------------------------------------------------------

  it('updateDhtStatus sets DHT status', () => {
    const store = useSessionStore()
    const incoming = makeDhtStatus({ isRunning: true, isEnabled: true, nodeCount: 42 })

    store.updateDhtStatus(incoming)

    expect(store.dhtStatus).toEqual(incoming)
    expect(store.dhtStatus!.nodeCount).toBe(42)
    expect(store.dhtStatus!.isRunning).toBe(true)
  })

  it('updateDhtStatus replaces previous DHT status', () => {
    const store = useSessionStore()
    store.updateDhtStatus(makeDhtStatus({ nodeCount: 10 }))
    store.updateDhtStatus(makeDhtStatus({ nodeCount: 200 }))

    expect(store.dhtStatus!.nodeCount).toBe(200)
  })

  it('updateDhtStatus correctly sets isEnabled = false', () => {
    const store = useSessionStore()
    store.updateDhtStatus(makeDhtStatus({ isEnabled: false, isRunning: false, nodeCount: 0 }))

    expect(store.dhtStatus!.isEnabled).toBe(false)
    expect(store.dhtStatus!.isRunning).toBe(false)
  })
})
