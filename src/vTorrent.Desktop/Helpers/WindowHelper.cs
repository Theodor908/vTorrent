using System;
using Avalonia;
using Avalonia.Controls;

namespace vTorrent.Desktop.Helpers;

/// <summary>
/// Wires platform-agnostic window behavior for chromeless windows.
/// With SystemDecorations="None", the OS provides no frame management —
/// this helper adds edge-based resize and constrains maximized bounds.
/// </summary>
internal static class WindowHelper
{
    /// <summary>
    /// Call from every window constructor after InitializeComponent().
    /// Wires edge resize (if resizable) and maximized-state work area constraint.
    /// </summary>
    public static void ApplyPlatformWindowStyle(Window window)
    {
        if (window.CanResize)
            WindowResizeHelper.EnableEdgeResize(window);

        // Constrain maximized windows to screen work area (excludes taskbar).
        // Without system decorations, WindowState.Maximized would cover the full screen.
        window.GetObservable(Window.WindowStateProperty)
            .Subscribe(new MaximizeConstrainer(window));
    }

    private sealed class MaximizeConstrainer : IObserver<WindowState>
    {
        private readonly Window _window;

        public MaximizeConstrainer(Window window) => _window = window;

        public void OnNext(WindowState state)
        {
            if (state != WindowState.Maximized)
                return;

            var screen = _window.Screens.ScreenFromWindow(_window);
            if (screen == null)
                return;

            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;

            // WorkingArea is in physical pixels, Position is in physical pixels,
            // Width/Height are in DIPs — convert work area to DIPs.
            _window.WindowState = WindowState.Normal;
            _window.Position = workArea.Position;
            _window.Width = workArea.Width / scaling;
            _window.Height = workArea.Height / scaling;
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
