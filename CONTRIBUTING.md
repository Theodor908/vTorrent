# Contributing to vTorrent

Thanks for your interest in contributing! This document explains how to get a
development environment running and the conventions we follow.

By participating in this project you agree to abide by our
[Code of Conduct](CODE_OF_CONDUCT.md).

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) — the exact version is
  pinned in `global.json` (roll-forward is enabled, so any compatible 10.x SDK works).

### Build, Run, Test

```bash
git clone https://github.com/Theodor908/vTorrent.git
cd vTorrent

dotnet build vTorrent.sln -c Release          # build everything
dotnet run --project src/vTorrent.Desktop     # run the desktop app
dotnet test vTorrent.sln                       # run all tests
```

## Project Structure

See the **Project Layout** section in the [README](README.md#project-layout) for a
map of the solution. In short:

- `src/vTorrent.Abstractions` — interfaces, DTOs, settings (no project dependencies)
- `src/vTorrent.Core` — the torrent engine, orchestration, networking
- `src/vTorrent.Desktop` — Avalonia UI (MVVM)
- `tests/*` — one test project per subsystem

## Coding Guidelines

- **Match the surrounding code.** Follow existing naming, structure, and patterns
  in the file you're editing.
- **`async` correctness matters.** Use `.ConfigureAwait(false)` in core/service
  layers. Avoid sync-over-async (`.Result` / `.GetAwaiter().GetResult()`) on
  unbounded operations.
- **Note:** the main projects do **not** enable implicit usings — add the
  `using` directives you need explicitly.
- **Keep units focused.** Prefer small, well-bounded types with a single clear
  responsibility over large multi-purpose files.
- Run `dotnet format` before submitting to keep style consistent.

## Pull Request Process

1. **Fork** the repo and create a feature branch:
   `git checkout -b feature/short-description` (or `fix/...`).
2. Make your change. **Add or update tests** for any behavior change.
3. Ensure `dotnet build` and `dotnet test` both pass locally.
4. Write a clear, descriptive commit message and PR title.
5. Open a PR against `main`, fill out the template, and link any related issues.
6. Be responsive to review feedback — discussion is part of the process.

## Reporting Bugs & Requesting Features

Use the [issue templates](https://github.com/Theodor908/vTorrent/issues/new/choose).
Please search existing issues first to avoid duplicates.

## License of Contributions

vTorrent is licensed under the **GNU GPL v3.0**. By submitting a contribution,
you agree that it will be licensed under the same terms.
