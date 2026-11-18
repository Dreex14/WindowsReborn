using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WindowsReborn.Core.Configuration;

namespace WindowsReborn.App.Infrastructure;

/// <summary>
/// Recuerda el tamaño, la posición y si la ventana estaba maximizada, con la posición
/// que guarda Windows (píxeles reales), así funciona con varios monitores y escalas.
/// </summary>
public static class WindowPlacement
{
    private const int SwShowNormal = 1;
    private const int SwShowMaximized = 3;
    private const uint MonitorDefaultToNull = 0;

    public static void Restore(Window window, AppSettings s)
    {
        if (s.WindowLeft is not { } l || s.WindowTop is not { } t || s.WindowWidth is not { } w || s.WindowHeight is not { } h || w < 200 || h < 150)
            return;
        var rect = new RECT { Left = (int)l, Top = (int)t, Right = (int)(l + w), Bottom = (int)(t + h) };
        // Si ese monitor ya no está, se deja la posición por defecto (centrada).
        if (MonitorFromRect(ref rect, MonitorDefaultToNull) == IntPtr.Zero)
            return;
        // Se aplica al cargar: antes, WPF vuelve a colocar la ventana con sus propios valores.
        window.Loaded += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var placement = new WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                showCmd = s.WindowMaximized ? SwShowMaximized : SwShowNormal,
                rcNormalPosition = rect,
            };
            SetWindowPlacement(hwnd, ref placement);
        };
    }

    public static void Save(Window window, AppSettings s)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref placement)) return;
        var r = placement.rcNormalPosition;
        s.WindowLeft = r.Left;
        s.WindowTop = r.Top;
        s.WindowWidth = r.Right - r.Left;
        s.WindowHeight = r.Bottom - r.Top;
        s.WindowMaximized = window.WindowState == WindowState.Maximized;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
}
