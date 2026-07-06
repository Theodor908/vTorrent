using System;
using System.Runtime.InteropServices;

namespace vTorrent.Core;

/// <summary>
/// Lightweight Win32 tray icon that gives full control over left/right click behavior.
/// Replaces Avalonia's TrayIcon which doesn't expose right-click events.
/// </summary>
public sealed class Win32TrayIcon : IDisposable
{
    // Window messages
    private const uint WM_TRAYICON = 0x0400 + 100; // WM_USER + 100
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    // Shell_NotifyIcon commands
    private const int NIM_ADD = 0x00;
    private const int NIM_DELETE = 0x02;

    // NOTIFYICONDATA flags
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;

    // LoadImage constants
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x10;
    private const uint LR_DEFAULTSIZE = 0x40;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImageW(
        IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public event Action? LeftClicked;
    public event Action? RightClicked;

    private IntPtr _hwnd;
    private NOTIFYICONDATAW _nid;
    private readonly WndProcDelegate _wndProc; // prevent GC collection
    private bool _disposed;

    public Win32TrayIcon(string iconFilePath, string tooltip)
    {
        _wndProc = WndProcCallback;

        // Register a unique window class
        var className = "vTorrentTray_" + Guid.NewGuid().ToString("N")[..8];
        var wc = new WNDCLASSEXW
        {
            cbSize = Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            lpszClassName = className
        };
        RegisterClassExW(ref wc);

        // Create a message-only hidden window
        _hwnd = CreateWindowExW(0, className, "", 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // Load the icon from file
        var hIcon = LoadImageW(IntPtr.Zero, iconFilePath,
            IMAGE_ICON, 16, 16, LR_LOADFROMFILE);

        // Register the tray icon
        _nid = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = (int)WM_TRAYICON,
            hIcon = hIcon,
            szTip = tooltip
        };
        Shell_NotifyIconW(NIM_ADD, ref _nid);
    }

    private IntPtr WndProcCallback(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_TRAYICON)
        {
            // For NOTIFYICONDATA v0: lParam = the mouse message
            var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            switch (mouseMsg)
            {
                case WM_LBUTTONUP:
                    LeftClicked?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    // SetForegroundWindow required for popup to dismiss properly
                    SetForegroundWindow(hwnd);
                    RightClicked?.Invoke();
                    break;
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Shell_NotifyIconW(NIM_DELETE, ref _nid);
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
