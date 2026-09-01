using System.Runtime.InteropServices;
using System.Windows;
using ScrollCapture.Utils;

namespace ScrollCapture.Capture;

/// <summary>
/// Real-screen queries: virtual desktop bounds (physical px), per-monitor DPI scales.
/// The process is PerMonitorV2 aware, so all GetSystemMetrics values are true physical pixels.
/// </summary>
public static class DpiManager
{
    public sealed record MonitorInfo(Int32Rect PhysicalBounds, bool IsPrimary, double Scale);

    public static Int32Rect GetVirtualScreenPhysical()
    {
        int x = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int y = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        return new Int32Rect(x, y, Math.Max(w, 1), Math.Max(h, 1));
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hDc, ref NativeMethods.RECT prcMonitor, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfoW(hMonitor, ref info))
            {
                return true;
            }

            double scale = 1.0;
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            {
                scale = dpiX / 96.0;
            }

            var bounds = new Int32Rect(
                info.rcMonitor.Left, info.rcMonitor.Top,
                info.rcMonitor.Right - info.rcMonitor.Left,
                info.rcMonitor.Bottom - info.rcMonitor.Top);
            monitors.Add(new MonitorInfo(bounds, (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0, scale));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    /// <summary>Scale of the monitor containing the physical point; falls back to nearest / primary.</summary>
    public static double GetScaleForPhysicalPoint(Point physicalPoint)
    {
        foreach (MonitorInfo monitor in GetMonitors())
        {
            if (PointIsInside(physicalPoint, monitor.PhysicalBounds))
            {
                return monitor.Scale;
            }
        }

        MonitorInfo? nearest = GetMonitors().MinBy(monitor =>
        {
            Int32Rect b = monitor.PhysicalBounds;
            var center = new Point(b.X + b.Width / 2.0, b.Y + b.Height / 2.0);
            return (center - physicalPoint).Length;
        });
        return nearest?.Scale ?? 1.0;
    }

    private static bool PointIsInside(Point p, Int32Rect r)
    {
        return p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height;
    }
}
