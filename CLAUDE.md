# CLAUDE.md

## Project Overview
vTorrent is a full-featured BitTorrent client built with .NET 10, Avalonia 11.3.0, and MVVM architecture. Architecturally inspired by libtorrent 2.0.11 design patterns. Single-solution C# project (~62K LOC).

## Skills
Always use available skills (brainstorming, writing-plans, executing-plans, TDD, debugging, code-review, etc.) unless the user explicitly says not to. Skills enforce discipline and prevent shortcuts.

## Critical Design Rules
1. **HandlePieceAsync must NOT dispatch requests** -- only the download loop dispatches. Inline dispatch causes concurrent lock races and stalls.
2. **Bitfield is MSB-first** (piece 0 = bit 7 of byte 0) per BitTorrent protocol.
3. **PeerReplacer must skip evaluation during endgame** -- duplicate blocks suppress payload rate.
4. **CompletePieceAsync requires atomic entry gate** via `_inProgressPieces.TryRemove`.
5. **All Bitfield methods require locking** -- `SetPiece` + `_completedPieces++` is NOT atomic.
6. **Use VerifiedProgress for UI**, not byte-based progress which includes unverified data.

## Key Directories
- `src/vTorrent.Abstractions/` - Interfaces, DTOs, settings, enums (no dependencies)
- `src/vTorrent.Core/` - Engine, orchestration, DHT, download/upload, peer/tracker communication
- `src/vTorrent.Desktop/` - Avalonia UI, ViewModels, services
- `src/vTorrent.Core/Engine/` - TorrentEngine, EngineFactory, EnginePhaseInitializer
- `src/vTorrent.Core/Download/` - DownloadCoordinator, PieceSelectionCoordinator
- `src/vTorrent.Core/Upload/` - ChokingManager, UploadCoordinator, PeerReplacer
- `src/vTorrent.Core/PieceIO/` - Disk backends (Posix/Mmap/Adaptive), FileHandleCache
- `src/vTorrent.Core/Settings/` - SettingsManager

## Settings Pattern
Settings use `IOptionsMonitor<T>` via `SettingsMonitor<T>` bridge. Per-torrent overrides resolved by `SettingsResolver` (sentinel: -1 for ints, null for nullable types).

## Build & Run
- Framework: .NET 10.0
- UI: Avalonia 11.3.0
- MVVM: CommunityToolkit.Mvvm 8.2.1
- DB: SQLite + Dapper
- Targets: win-x64, osx-x64/arm64, linux-x64/arm64

## No Implicit Usings
The main project (vTorrent.csproj) does NOT have implicit usings enabled. Test project does.

## DLL Lock Errors
If build fails with file lock errors, kill stale dotnet processes: `taskkill /f /im dotnet.exe`
