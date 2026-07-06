// format.ts — Display formatting helpers for speeds, sizes, durations, and ratios.

// ============================================================
// formatBytes — human-readable byte count
// ============================================================

/**
 * formatBytes — formats a raw byte count into a compact human-readable string.
 * Examples: 0 → "0 B", 1234 → "1.23 KB", 45_000_000 → "45.0 MB"
 */
export function formatBytes(bytes: number): string {
  if (bytes <= 0) return '0 B';

  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);

  if (i === 0) return `${bytes} B`;

  const value = bytes / Math.pow(1024, i);
  return `${value < 10 ? value.toFixed(2) : value < 100 ? value.toFixed(1) : value.toFixed(0)} ${units[i]}`;
}

// ============================================================
// formatSpeed — bytes per second → human-readable speed
// ============================================================

/**
 * formatSpeed — formats a byte-per-second rate into a compact human-readable string.
 * Examples: 0 → "0 B/s", 1234 → "1.23 KB/s", 1_500_000 → "1.43 MB/s"
 */
export function formatSpeed(bytesPerSec: number): string {
  if (bytesPerSec <= 0) return '0 B/s';

  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  const i = Math.min(Math.floor(Math.log(bytesPerSec) / Math.log(1024)), units.length - 1);

  if (i === 0) return `${bytesPerSec} B/s`;

  const value = bytesPerSec / Math.pow(1024, i);
  return `${value < 10 ? value.toFixed(2) : value < 100 ? value.toFixed(1) : value.toFixed(0)} ${units[i]}`;
}

// ============================================================
// formatDuration — seconds → human-readable time span
// ============================================================

/**
 * formatDuration — formats a duration in seconds into a compact human-readable string.
 * Examples: 150 → "2m 30s", 4500 → "1h 15m", 262800 → "3d 1h"
 */
export function formatDuration(seconds: number): string {
  if (seconds <= 0) return '0s';

  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = Math.floor(seconds % 60);

  if (days > 0) return `${days}d ${hours}h`;
  if (hours > 0) return `${hours}h ${minutes}m`;
  if (minutes > 0) return `${minutes}m ${secs}s`;
  return `${secs}s`;
}

// ============================================================
// formatPercent — 0-1 ratio → percentage string
// ============================================================

/**
 * formatPercent — formats a 0.0–1.0 ratio as a percentage string with one decimal.
 * Examples: 0 → "0.0%", 0.456 → "45.6%", 1 → "100.0%"
 */
export function formatPercent(ratio: number): string {
  return `${(ratio * 100).toFixed(1)}%`;
}
