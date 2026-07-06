using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace vTorrent.Desktop.Helpers;

/// <summary>
/// Provides edge-based resize for chromeless windows (SystemDecorations="None").
/// Attaches tunnel-routed pointer handlers to detect edge proximity and initiate
/// BeginResizeDrag with the correct WindowEdge.
/// </summary>
internal static class WindowResizeHelper
{
    private const int DefaultGripSize = 6;

    // Cached cursors to avoid allocations on every PointerMoved
    private static readonly Cursor CursorNS = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor CursorWE = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor CursorNWSE = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor CursorNESW = new(StandardCursorType.TopRightCorner);

    /// <summary>
    /// Enables edge resizing on a chromeless window.
    /// Call once from the window constructor, after InitializeComponent().
    /// </summary>
    public static void EnableEdgeResize(Window window, int gripSize = DefaultGripSize)
    {
        window.AddHandler(InputElement.PointerMovedEvent, (s, e) => OnPointerMoved(window, e, gripSize), RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.PointerPressedEvent, (s, e) => OnPointerPressed(window, e, gripSize), RoutingStrategies.Tunnel);
    }

    private static void OnPointerMoved(Window window, PointerEventArgs e, int grip)
    {
        if (window.WindowState != WindowState.Normal)
        {
            window.Cursor = Cursor.Default;
            return;
        }

        var edge = GetEdge(window, e, grip);
        window.Cursor = edge switch
        {
            WindowEdge.North or WindowEdge.South => CursorNS,
            WindowEdge.West or WindowEdge.East => CursorWE,
            WindowEdge.NorthWest or WindowEdge.SouthEast => CursorNWSE,
            WindowEdge.NorthEast or WindowEdge.SouthWest => CursorNESW,
            _ => Cursor.Default
        };
    }

    private static void OnPointerPressed(Window window, PointerPressedEventArgs e, int grip)
    {
        if (window.WindowState != WindowState.Normal)
            return;

        if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
            return;

        var edge = GetEdge(window, e, grip);
        if (edge == null)
            return;

        e.Handled = true;
        window.BeginResizeDrag(edge.Value, e);
    }

    private static WindowEdge? GetEdge(Window window, PointerEventArgs e, int grip)
    {
        var pos = e.GetPosition(window);
        var w = window.ClientSize.Width;
        var h = window.ClientSize.Height;

        var top = pos.Y < grip;
        var bottom = pos.Y > h - grip;
        var left = pos.X < grip;
        var right = pos.X > w - grip;

        if (top && left) return WindowEdge.NorthWest;
        if (top && right) return WindowEdge.NorthEast;
        if (bottom && left) return WindowEdge.SouthWest;
        if (bottom && right) return WindowEdge.SouthEast;
        if (top) return WindowEdge.North;
        if (bottom) return WindowEdge.South;
        if (left) return WindowEdge.West;
        if (right) return WindowEdge.East;

        return null;
    }
}
