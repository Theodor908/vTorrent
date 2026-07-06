// torrentStore.test.ts — Unit tests for the torrent Pinia store

import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'

// Mock the API module so the store can be imported without network calls
vi.mock('@/api/torrents', () => ({
  getTorrents: vi.fn().mockResolvedValue([]),
}))

import { useTorrentStore } from '@/stores/torrentStore'
import type { TorrentSnapshot } from '@/types/torrent'

// ============================================================
// Helper — minimal TorrentSnapshot factory
// ============================================================

function makeSnapshot(overrides: Partial<TorrentSnapshot> & { infoHash: string; name: string }): TorrentSnapshot {
  return {
    infoHashV2: null,
    torrentVersionValue: 1,
    status: {
      phase: 'Downloading',
      fileOp: 'None',
      intent: 'Active',
      error: null,
      missingFiles: false,
      isAutoManaged: false,
      isFinished: false,
      isSeed: false,
      fileOpProgress: 0,
    },
    totalSize: 1024,
    totalWanted: 1024,
    totalWantedDone: 0,
    piecesCompleted: 0,
    totalPieces: 10,
    verifiedProgress: 0,
    pendingPieces: 0,
    payloadDownloadRate: 0,
    payloadUploadRate: 0,
    smoothedPayloadDownloadRate: 0,
    totalDownloadRate: 0,
    totalUploadRate: 0,
    sessionPayloadDownloaded: 0,
    sessionPayloadUploaded: 0,
    totalUploaded: 0,
    connectedPeers: 0,
    connectedSeeds: 0,
    totalPeers: 0,
    totalSeeds: 0,
    availability: 0,
    isEndgame: false,
    endgameWastedBytes: 0,
    endgameDuplicateBlocks: 0,
    isSeeding: false,
    isFinished: false,
    addedOn: '2024-01-01T00:00:00Z',
    completedOn: null,
    activeDuration: '00:00:00',
    seedingDuration: '00:00:00',
    savePath: '/downloads',
    queuePosition: 0,
    isForceResumed: false,
    categoryId: null,
    categoryName: null,
    tags: [],
    errorMessage: null,
    ...overrides,
  }
}

// ============================================================
// Tests
// ============================================================

describe('torrentStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    localStorage.clear()
  })

  // ----------------------------------------------------------
  // updateTorrent
  // ----------------------------------------------------------

  it('updateTorrent adds a snapshot to the map', () => {
    const store = useTorrentStore()
    const snapshot = makeSnapshot({ infoHash: 'abc123', name: 'My Torrent' })

    store.updateTorrent(snapshot)

    expect(store.torrents.get('abc123')).toEqual(snapshot)
    expect(store.torrents.size).toBe(1)
  })

  it('updateTorrent replaces an existing snapshot with the same hash', () => {
    const store = useTorrentStore()
    const original = makeSnapshot({ infoHash: 'abc123', name: 'Old Name' })
    const updated = makeSnapshot({ infoHash: 'abc123', name: 'New Name' })

    store.updateTorrent(original)
    store.updateTorrent(updated)

    expect(store.torrents.get('abc123')!.name).toBe('New Name')
    expect(store.torrents.size).toBe(1)
  })

  // ----------------------------------------------------------
  // updateTorrents
  // ----------------------------------------------------------

  it('updateTorrents merges multiple snapshots', () => {
    const store = useTorrentStore()
    const snapshots = [
      makeSnapshot({ infoHash: 'hash1', name: 'Alpha' }),
      makeSnapshot({ infoHash: 'hash2', name: 'Beta' }),
      makeSnapshot({ infoHash: 'hash3', name: 'Gamma' }),
    ]

    store.updateTorrents(snapshots)

    expect(store.torrents.size).toBe(3)
    expect(store.torrents.get('hash1')!.name).toBe('Alpha')
    expect(store.torrents.get('hash2')!.name).toBe('Beta')
    expect(store.torrents.get('hash3')!.name).toBe('Gamma')
  })

  it('updateTorrents upserts — existing entries are replaced', () => {
    const store = useTorrentStore()
    store.updateTorrent(makeSnapshot({ infoHash: 'hash1', name: 'Old' }))

    store.updateTorrents([makeSnapshot({ infoHash: 'hash1', name: 'Updated' })])

    expect(store.torrents.get('hash1')!.name).toBe('Updated')
    expect(store.torrents.size).toBe(1)
  })

  // ----------------------------------------------------------
  // removeTorrent
  // ----------------------------------------------------------

  it('removeTorrent removes by hash', () => {
    const store = useTorrentStore()
    store.updateTorrent(makeSnapshot({ infoHash: 'hash1', name: 'To Remove' }))
    store.updateTorrent(makeSnapshot({ infoHash: 'hash2', name: 'To Keep' }))

    store.removeTorrent('hash1')

    expect(store.torrents.has('hash1')).toBe(false)
    expect(store.torrents.size).toBe(1)
  })

  it('removeTorrent clears selectedHash if it matches', () => {
    const store = useTorrentStore()
    store.updateTorrent(makeSnapshot({ infoHash: 'hash1', name: 'Selected' }))
    store.selectedHash = 'hash1'

    store.removeTorrent('hash1')

    expect(store.selectedHash).toBeNull()
  })

  it('removeTorrent does nothing when hash is not present', () => {
    const store = useTorrentStore()
    store.updateTorrent(makeSnapshot({ infoHash: 'hash1', name: 'Present' }))

    store.removeTorrent('nonexistent')

    expect(store.torrents.size).toBe(1)
  })

  // ----------------------------------------------------------
  // filteredTorrents — search query
  // ----------------------------------------------------------

  it('filteredTorrents filters by search query (case-insensitive)', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'Ubuntu ISO' }),
      makeSnapshot({ infoHash: 'h2', name: 'Arch Linux' }),
      makeSnapshot({ infoHash: 'h3', name: 'Ubuntu Server' }),
    ])

    store.searchQuery = 'ubuntu'

    expect(store.filteredTorrents).toHaveLength(2)
    expect(store.filteredTorrents.map(t => t.name)).toContain('Ubuntu ISO')
    expect(store.filteredTorrents.map(t => t.name)).toContain('Ubuntu Server')
  })

  it('filteredTorrents returns all when search query is empty', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'A' }),
      makeSnapshot({ infoHash: 'h2', name: 'B' }),
    ])

    store.searchQuery = ''

    expect(store.filteredTorrents).toHaveLength(2)
  })

  // ----------------------------------------------------------
  // filteredTorrents — status filter
  // ----------------------------------------------------------

  it('filteredTorrents filters by status', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'Downloading One', status: { phase: 'Downloading', fileOp: 'None', intent: 'Active', error: null, missingFiles: false, isAutoManaged: true, isFinished: false, isSeed: false, fileOpProgress: 0 } }),
      makeSnapshot({ infoHash: 'h2', name: 'Seeding One', status: { phase: 'Seeding', fileOp: 'None', intent: 'Active', error: null, missingFiles: false, isAutoManaged: true, isFinished: true, isSeed: true, downloadRate: 0, uploadRate: 0, connectedPeers: 0, progress: 1, fileOpProgress: 0 } }),
      makeSnapshot({ infoHash: 'h3', name: 'Downloading Two', status: { phase: 'Downloading', fileOp: 'None', intent: 'Active', error: null, missingFiles: false, isAutoManaged: true, isFinished: false, isSeed: false, fileOpProgress: 0 } }),
    ])

    store.statusFilter = 'Seeding'

    expect(store.filteredTorrents).toHaveLength(1)
    expect(store.filteredTorrents[0].name).toBe('Seeding One')
  })

  it('filteredTorrents returns all when status filter is null', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'A', status: { phase: 'Downloading', fileOp: 'None', intent: 'Active', error: null, missingFiles: false, isAutoManaged: true, isFinished: false, isSeed: false, fileOpProgress: 0 } }),
      makeSnapshot({ infoHash: 'h2', name: 'B', status: { phase: 'Seeding', fileOp: 'None', intent: 'Active', error: null, missingFiles: false, isAutoManaged: true, isFinished: true, isSeed: true, downloadRate: 0, uploadRate: 0, connectedPeers: 0, progress: 1, fileOpProgress: 0 } }),
    ])

    store.statusFilter = null

    expect(store.filteredTorrents).toHaveLength(2)
  })

  // ----------------------------------------------------------
  // filteredTorrents — sorting
  // ----------------------------------------------------------

  it('filteredTorrents sorts by name ascending', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'Zeta' }),
      makeSnapshot({ infoHash: 'h2', name: 'Alpha' }),
      makeSnapshot({ infoHash: 'h3', name: 'Mango' }),
    ])

    store.sortColumn = 'Name'
    store.sortDirection = 'asc'

    const names = store.filteredTorrents.map(t => t.name)
    expect(names).toEqual(['Alpha', 'Mango', 'Zeta'])
  })

  it('filteredTorrents sorts by name descending', () => {
    const store = useTorrentStore()
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'Zeta' }),
      makeSnapshot({ infoHash: 'h2', name: 'Alpha' }),
      makeSnapshot({ infoHash: 'h3', name: 'Mango' }),
    ])

    store.sortColumn = 'Name'
    store.sortDirection = 'desc'

    const names = store.filteredTorrents.map(t => t.name)
    expect(names).toEqual(['Zeta', 'Mango', 'Alpha'])
  })

  // ----------------------------------------------------------
  // statusCounts
  // ----------------------------------------------------------

  it('statusCounts computes correct counts from the map', () => {
    const store = useTorrentStore()
    const dlStatus = { phase: 'Downloading' as const, fileOp: 'None' as const, intent: 'Active' as const, error: null, missingFiles: false, isAutoManaged: true, isFinished: false, isSeed: false, fileOpProgress: 0 };
    const seedStatus = { ...dlStatus, phase: 'Seeding' as const, isFinished: true, isSeed: true, progress: 1 };
    store.updateTorrents([
      makeSnapshot({ infoHash: 'h1', name: 'D1', status: dlStatus }),
      makeSnapshot({ infoHash: 'h2', name: 'D2', status: dlStatus }),
      makeSnapshot({ infoHash: 'h3', name: 'S1', status: seedStatus }),
      makeSnapshot({ infoHash: 'h4', name: 'P1', status: { ...dlStatus, intent: 'Paused' as const } }),
      makeSnapshot({ infoHash: 'h5', name: 'E1', status: { ...dlStatus, error: { message: 'test error', errorCode: null, filePath: null } } }),
      makeSnapshot({ infoHash: 'h6', name: 'M1', status: { ...dlStatus, phase: 'FetchingMetadata' as const } }),
      makeSnapshot({ infoHash: 'h7', name: 'F1', isFinished: true, status: seedStatus }),
    ])

    const counts = store.statusCounts

    expect(counts.downloading).toBe(3)  // D1, D2, M1 (MetadataDownloading derived from FetchingMetadata)
    expect(counts.seeding).toBe(2)      // S1, F1
    expect(counts.paused).toBe(1)       // P1
    expect(counts.errored).toBe(1)      // E1
    expect(counts.completed).toBe(1)    // F1 (isFinished)
  })

  it('statusCounts returns zeros when store is empty', () => {
    const store = useTorrentStore()

    const counts = store.statusCounts

    expect(counts.downloading).toBe(0)
    expect(counts.seeding).toBe(0)
    expect(counts.paused).toBe(0)
    expect(counts.errored).toBe(0)
    expect(counts.completed).toBe(0)
  })

  // ----------------------------------------------------------
  // toggleColumn
  // ----------------------------------------------------------

  it('toggleColumn flips column visibility from true to false', () => {
    const store = useTorrentStore()

    // 'Name' is visible by default (true)
    expect(store.columnVisibility['Name']).toBe(true)

    store.toggleColumn('Name')

    expect(store.columnVisibility['Name']).toBe(false)
  })

  it('toggleColumn flips column visibility from false to true', () => {
    const store = useTorrentStore()

    // 'Status' is hidden by default (false)
    expect(store.columnVisibility['Status']).toBe(false)

    store.toggleColumn('Status')

    expect(store.columnVisibility['Status']).toBe(true)
  })

  it('toggleColumn toggling twice restores original visibility', () => {
    const store = useTorrentStore()
    const original = store.columnVisibility['Name']

    store.toggleColumn('Name')
    store.toggleColumn('Name')

    expect(store.columnVisibility['Name']).toBe(original)
  })

  // ----------------------------------------------------------
  // Column visibility persists to localStorage
  // ----------------------------------------------------------

  it('toggleColumn persists visibility to localStorage', () => {
    const store = useTorrentStore()

    store.toggleColumn('Name')

    const raw = localStorage.getItem('vtorrent-columns')
    expect(raw).not.toBeNull()
    const stored = JSON.parse(raw!)
    expect(stored['Name']).toBe(false)
  })

  it('loadColumnVisibility restores from localStorage', () => {
    const store = useTorrentStore()
    // Manually write a custom state to localStorage
    localStorage.setItem('vtorrent-columns', JSON.stringify({ Name: false, Progress: false }))

    store.loadColumnVisibility()

    expect(store.columnVisibility['Name']).toBe(false)
    expect(store.columnVisibility['Progress']).toBe(false)
  })

  it('loadColumnVisibility fills missing keys with defaults', () => {
    const store = useTorrentStore()
    // Only override Name; all others should fall back to default
    localStorage.setItem('vtorrent-columns', JSON.stringify({ Name: false }))

    store.loadColumnVisibility()

    expect(store.columnVisibility['Name']).toBe(false)
    // 'Progress' was not in localStorage — should be true (default)
    expect(store.columnVisibility['Progress']).toBe(true)
  })
})
