using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace vTorrent.Core.PieceIO;

/// <summary>
/// Utility class for detecting storage device characteristics.
/// Used to warn users when downloading to HDDs (where sequential writes perform better than random).
/// </summary>
public static class StorageDeviceHelper
{
    /// <summary>
    /// Determines whether the drive hosting <paramref name="filePath"/> is a solid-state drive.
    /// Returns <c>true</c> (assume SSD) if detection fails, because the SSD warning is informational only.
    /// </summary>
    /// <param name="filePath">Absolute path on the drive to test.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <returns><c>true</c> if the drive is an SSD or detection was inconclusive; <c>false</c> if definitely an HDD.</returns>
    public static bool IsSolidStateDrive(string filePath, ILogger? logger = null)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return DetectWindowsSsd(filePath, logger);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return DetectLinuxSsd(filePath, logger);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return DetectMacOsSsd(filePath, logger);

            // Unknown platform — assume SSD
            logger?.LogDebug("StorageDeviceHelper: Unknown platform, defaulting to SSD assumption.");
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "StorageDeviceHelper: Unhandled exception during SSD detection, defaulting to SSD assumption.");
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Windows — IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceSeekPenaltyProperty
    // ──────────────────────────────────────────────────────────────────────────

#if WINDOWS
    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_PROPERTY_QUERY
    {
        public uint PropertyId;
        public uint QueryType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public byte[] AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.Bool)]
        public bool IncursSeekPenalty;
    }

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint StorageDeviceSeekPenaltyProperty = 7;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint hDevice,
        uint dwIoControlCode,
        ref STORAGE_PROPERTY_QUERY lpInBuffer,
        uint nInBufferSize,
        out DEVICE_SEEK_PENALTY_DESCRIPTOR lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
#endif

    private static bool DetectWindowsSsd(string filePath, ILogger? logger)
    {
#if WINDOWS
        // Extract drive root (e.g. "C:\") and build device path (e.g. "\\.\C:")
        string? root = Path.GetPathRoot(filePath);
        if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
        {
            logger?.LogDebug("StorageDeviceHelper: Cannot extract drive letter from '{Path}', defaulting to SSD.", filePath);
            return true;
        }

        string drivePath = @"\\.\" + root[0] + ':';

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        nint handle = CreateFileW(
            drivePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            nint.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nint.Zero);

        if (handle == -1 || handle == nint.Zero)
        {
            logger?.LogDebug("StorageDeviceHelper: CreateFileW failed for '{Drive}' (error {Error}), defaulting to SSD.", drivePath, Marshal.GetLastWin32Error());
            return true;
        }

        try
        {
            var query = new STORAGE_PROPERTY_QUERY
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = 0, // PropertyStandardQuery
                AdditionalParameters = new byte[1]
            };

            bool ok = DeviceIoControl(
                handle,
                IOCTL_STORAGE_QUERY_PROPERTY,
                ref query,
                (uint)Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                out DEVICE_SEEK_PENALTY_DESCRIPTOR descriptor,
                (uint)Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                out _,
                nint.Zero);

            if (!ok)
            {
                logger?.LogDebug("StorageDeviceHelper: DeviceIoControl failed (error {Error}), defaulting to SSD.", Marshal.GetLastWin32Error());
                return true;
            }

            bool isSsd = !descriptor.IncursSeekPenalty;
            logger?.LogDebug("StorageDeviceHelper: Drive '{Drive}' IncursSeekPenalty={Penalty} → IsSSD={IsSsd}.",
                drivePath, descriptor.IncursSeekPenalty, isSsd);
            return isSsd;
        }
        finally
        {
            CloseHandle(handle);
        }
#else
        logger?.LogDebug("StorageDeviceHelper: Windows detection called on non-Windows build, defaulting to SSD.");
        return true;
#endif
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Linux — /proc/mounts + /sys/block/{dev}/queue/rotational
    // ──────────────────────────────────────────────────────────────────────────

    private static bool DetectLinuxSsd(string filePath, ILogger? logger)
    {
        try
        {
            string? device = ResolveLinuxDevice(filePath, logger);
            if (device is null)
                return true;

            // Strip partition suffix: "sda1" → "sda", "nvme0n1p2" → "nvme0n1"
            string baseDevice = StripPartitionSuffix(device);

            string rotationalPath = $"/sys/block/{baseDevice}/queue/rotational";
            if (!File.Exists(rotationalPath))
            {
                logger?.LogDebug("StorageDeviceHelper: '{Path}' not found, defaulting to SSD.", rotationalPath);
                return true;
            }

            string content = File.ReadAllText(rotationalPath).Trim();
            bool isSsd = content == "0";
            logger?.LogDebug("StorageDeviceHelper: Device '{Device}' rotational={Value} → IsSSD={IsSsd}.",
                baseDevice, content, isSsd);
            return isSsd;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "StorageDeviceHelper: Linux detection failed, defaulting to SSD.");
            return true;
        }
    }

    private static string? ResolveLinuxDevice(string filePath, ILogger? logger)
    {
        // Resolve to absolute path
        string absPath = Path.GetFullPath(filePath);

        try
        {
            string[] mounts = File.ReadAllLines("/proc/mounts");

            string bestMountPoint = string.Empty;
            string? bestDevice = null;

            foreach (string line in mounts)
            {
                // Format: device mountpoint fstype options dump pass
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                    continue;

                string device = parts[0];
                string mountPoint = parts[1];

                // Unescape octal sequences in mount point (e.g. \040 → space)
                mountPoint = UnescapeOctal(mountPoint);

                if (!absPath.StartsWith(mountPoint, StringComparison.Ordinal))
                    continue;

                // Ensure it's a true prefix (directory boundary)
                if (mountPoint.Length > 1 && absPath.Length > mountPoint.Length &&
                    absPath[mountPoint.Length] != '/')
                    continue;

                if (mountPoint.Length > bestMountPoint.Length)
                {
                    bestMountPoint = mountPoint;
                    bestDevice = device;
                }
            }

            if (bestDevice is null)
            {
                logger?.LogDebug("StorageDeviceHelper: No mount point matched for '{Path}', defaulting to SSD.", absPath);
                return null;
            }

            // Extract just the device name (e.g. /dev/sda1 → sda1)
            return Path.GetFileName(bestDevice);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "StorageDeviceHelper: Failed to read /proc/mounts, defaulting to SSD.");
            return null;
        }
    }

    private static string StripPartitionSuffix(string deviceName)
    {
        // NVMe pattern: nvme0n1p2 → nvme0n1
        if (deviceName.Contains('p'))
        {
            int pIdx = deviceName.LastIndexOf('p');
            string afterP = deviceName[(pIdx + 1)..];
            if (afterP.Length > 0 && afterP.AsSpan().TrimStart("0123456789").Length == 0)
                return deviceName[..pIdx];
        }

        // SATA/SCSI pattern: sda1 → sda
        int i = deviceName.Length - 1;
        while (i >= 0 && char.IsAsciiDigit(deviceName[i]))
            i--;

        return i < deviceName.Length - 1 ? deviceName[..(i + 1)] : deviceName;
    }

    private static string UnescapeOctal(string input)
    {
        if (!input.Contains('\\'))
            return input;

        var sb = new System.Text.StringBuilder(input.Length);
        int idx = 0;
        while (idx < input.Length)
        {
            if (input[idx] == '\\' && idx + 3 < input.Length &&
                IsOctalDigit(input[idx + 1]) &&
                IsOctalDigit(input[idx + 2]) &&
                IsOctalDigit(input[idx + 3]))
            {
                int value = ((input[idx + 1] - '0') << 6) |
                            ((input[idx + 2] - '0') << 3) |
                             (input[idx + 3] - '0');
                sb.Append((char)value);
                idx += 4;
            }
            else
            {
                sb.Append(input[idx++]);
            }
        }
        return sb.ToString();
    }

    private static bool IsOctalDigit(char c) => c >= '0' && c <= '7';

    // ──────────────────────────────────────────────────────────────────────────
    // macOS — df + diskutil info
    // ──────────────────────────────────────────────────────────────────────────

    private static bool DetectMacOsSsd(string filePath, ILogger? logger)
    {
        try
        {
            // Step 1: resolve mount point via `df`
            string dfOutput = RunProcess("df", filePath, logger);
            if (string.IsNullOrWhiteSpace(dfOutput))
                return true;

            string[] lines = dfOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                logger?.LogDebug("StorageDeviceHelper: Unexpected df output, defaulting to SSD.");
                return true;
            }

            // Second line, last whitespace-delimited field is the mount point
            string[] fields = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0)
            {
                logger?.LogDebug("StorageDeviceHelper: Cannot parse df output, defaulting to SSD.");
                return true;
            }

            string mountPoint = fields[^1];

            // Step 2: query diskutil for drive type
            string diskutilOutput = RunProcess("diskutil", $"info {mountPoint}", logger);
            if (string.IsNullOrWhiteSpace(diskutilOutput))
                return true;

            bool isSsd = diskutilOutput.Contains("Solid State: Yes", StringComparison.OrdinalIgnoreCase);
            logger?.LogDebug("StorageDeviceHelper: diskutil info '{MountPoint}' → IsSSD={IsSsd}.", mountPoint, isSsd);
            return isSsd;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "StorageDeviceHelper: macOS detection failed, defaulting to SSD.");
            return true;
        }
    }

    private static string RunProcess(string executable, string arguments, ILogger? logger)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000); // 5-second timeout
            return output;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "StorageDeviceHelper: Failed to run '{Exe} {Args}'.", executable, arguments);
            return string.Empty;
        }
    }
}
