// ============================================================
// MonitorWorkArea.cs
// ------------------------------------------------------------
// The usable area (screen minus taskbar) of the monitor a window
// is on, in WPF units, so a window can be sized to fit the screen
// it is actually shown on.
//
// SystemParameters.WorkArea only describes the primary monitor.
// On a multi-monitor setup the window may be on another one, so
// the monitor is asked for by window handle (MonitorFromWindow /
// GetMonitorInfo) and its pixel rectangle converted to WPF units
// with the window's own DPI transform.
//
// Before the window has a handle (during construction) there is
// no monitor to ask yet; the primary monitor's work area is used,
// which is where a CenterScreen window first appears.
// ============================================================

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TranscodeTools;

internal static class MonitorWorkArea
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int        Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint       Flags;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public static Rect For(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || PresentationSource.FromVisual(window)?.CompositionTarget is not { } target)
            return SystemParameters.WorkArea;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, MonitorDefaultToNearest), ref info))
            return SystemParameters.WorkArea;

        var toWpf = target.TransformFromDevice;
        return new Rect(
            toWpf.Transform(new Point(info.Work.Left,  info.Work.Top)),
            toWpf.Transform(new Point(info.Work.Right, info.Work.Bottom)));
    }
}
