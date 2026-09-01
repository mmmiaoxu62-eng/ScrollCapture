using System.Runtime.InteropServices;
using System.Windows.Automation;
using ScrollCapture.Utils;

namespace ScrollCapture.Scrolling;

/// <summary>
/// A snapshot of the target window's scroll state. Two probing strategies are tried in
/// order (deterministic preference, mirroring how phones know the offset):
///   1. GetScrollInfo (standard Win32 scrollbar: nPos/nMax/nPage are in pixels for
///      standard bars — the most authoritative estimate available)
///   2. UIAutomation ScrollPattern (Chrome/Edge/Firefox expose VerticalScrollPercent/
///      VerticalViewSize through accessibility bridges)
/// When both fail, scroll control falls back to pure vision matching.
/// </summary>
public sealed record OffsetSnapshot(
    int? ScrollPos,        // GetScrollInfo nPos (≈ pixels for standard bars)
    int? ScrollMax,        // nMax (content extent in track units)
    int? ScrollPage,       // nPage (viewport in track units)
    long? ScrollTrackPos,  // nTrackPos (thumb position)
    double? UiaPercent,    // 0..100
    double? UiaViewSize,   // 0..100 (small = long page)
    double? ClientHeightPx = null) // viewport height for UIA percent<->px conversion
{
    public bool IsUsable => ScrollPos != null || UiaPercent != null;

    /// <summary>Estimated vertical movement in pixels between two snapshots (0..N).</summary>
    public double? EstimateDeltaPx(OffsetSnapshot other)
    {
        // Strategy 1: scrollbar positions (units ≈ pixels on standard scrollbars).
        if (ScrollPos != null && other.ScrollPos != null)
        {
            return Math.Abs(other.ScrollPos.Value - ScrollPos.Value);
        }
        // Strategy 2: UIA percent delta mapped through view size & viewport height:
        //   usable(scrollable) = clientH * (100 - viewSize) / viewSize
        //   deltaPx = deltaPercent / 100 * usable
        double? percent = other.UiaPercent;
        if (percent != null && UiaPercent != null && percent >= 0 && UiaPercent >= 0)
        {
            double deltaPercent = Math.Abs(percent.Value - UiaPercent.Value);
            if (UiaViewSize is double viewSize && viewSize > 0 && viewSize < 100
                && ClientHeightPx is double ch && ch > 0)
            {
                double usable = ch * (100.0 - viewSize) / viewSize;
                return deltaPercent / 100.0 * usable;
            }
            // last resort: percent * 10 (crude)
            return deltaPercent * 10.0;
        }
        return null;
    }
}

public static class ScrollOffsetProbe
{
    /// <summary>Probes the window (its root first, then standard child hwnds).</summary>
    public static OffsetSnapshot? Probe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }
        OffsetSnapshot? snapshot = ProbeScrollInfo(hwnd) ?? ProbeUia(hwnd);
        return snapshot;
    }

    private static OffsetSnapshot? ProbeScrollInfo(IntPtr hwnd)
    {
        try
        {
            var info = new NativeMethods.SCROLLINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.SCROLLINFO>() };
            if (NativeMethods.GetScrollInfo(hwnd, NativeMethods.SB_VERT, ref info))
            {
                bool hasRange = info.nMax > info.nMin;
                if (hasRange && info.nPage > 0)
                {
                    return new OffsetSnapshot(info.nPos, info.nMax, (int)info.nPage, info.nTrackPos, null, null);
                }
            }
        }
        catch
        {
            // ignore — fall through to UIA
        }
        return null;
    }

    private static OffsetSnapshot? ProbeUia(IntPtr hwnd)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            ScrollPattern? pattern = GetScrollPattern(root);
            if (pattern == null)
            {
                return null;
            }
            double vert = pattern.Current.VerticalScrollPercent;
            double view = pattern.Current.VerticalViewSize;
            if (double.IsNaN(vert) || double.IsNaN(view))
            {
                return null;
            }
            double? clientH = GetClientHeight(hwnd);
            return new OffsetSnapshot(null, null, null, null, vert, view, clientH);
        }
        catch
        {
            return null;
        }
    }

    private static double? GetClientHeight(IntPtr hwnd)
    {
        try
        {
            if (NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT rect))
            {
                return rect.Bottom - rect.Top;
            }
        }
        catch
        {
        }
        return null;
    }

    private static ScrollPattern? GetScrollPattern(AutomationElement root)
    {
        try
        {
            var condition = new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true);
            AutomationElement? scrollable = root.FindFirst(TreeScope.Descendants, condition);
            if (scrollable == null)
            {
                scrollable = root;
            }
            return (ScrollPattern)scrollable.GetCurrentPattern(ScrollPattern.Pattern);
        }
        catch
        {
            return null;
        }
    }
}
