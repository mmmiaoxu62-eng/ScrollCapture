using System.Runtime.InteropServices;
using System.Windows;
using ScrollCapture.Utils;

namespace ScrollCapture.Scrolling;

/// <summary>
/// Simulates mouse wheel scrolling via SendInput (real wheel events — works with
/// browsers, Electron apps, WeChat/QQ, explorer, etc. — the window under the cursor receives it).
/// Saves/restores the original cursor position and optionally foregrounds the target window.
/// </summary>
public sealed class ScrollController : IDisposable
{
    public const int WheelTick = 120;

    private int _wheelStep;
    private bool _cursorSaved;
    private bool _anchorSet;
    private NativeMethods.POINT _originalCursor;
    private NativeMethods.POINT _anchor;

    public IntPtr TargetRootHwnd { get; private set; } = IntPtr.Zero;

    /// <summary>Wheel deltas per scroll (adaptive: 4 -> 2 -> 1 on suspicion).</summary>
    public int WheelStep
    {
        get => _wheelStep;
        set => _wheelStep = Math.Max(1, value);
    }

    public ScrollController(int wheelStep = 2)
    {
        _wheelStep = Math.Max(1, wheelStep);
    }

    /// <summary>
    /// Prepares for an auto-scroll session: saves cursor pos, moves cursor to the
    /// center of the capture region, tries to foreground the window under that point.
    /// </summary>
    public void Prepare(Int32Rect regionPhysical)
    {
        Dispose();

        if (!NativeMethods.GetCursorPos(out _originalCursor))
        {
            _originalCursor = default;
        }
        _cursorSaved = true;

        int anchorX = regionPhysical.X + Math.Max(1, regionPhysical.Width / 2);
        int anchorY = regionPhysical.Y + Math.Max(1, regionPhysical.Height / 2);
        _anchor = new NativeMethods.POINT { X = anchorX, Y = anchorY };
        _anchorSet = true;
        NativeMethods.SetCursorPos(anchorX, anchorY);

        try
        {
            IntPtr pointWindow = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = anchorX, Y = anchorY });
            if (pointWindow != IntPtr.Zero)
            {
                IntPtr root = NativeMethods.GetAncestor(pointWindow, NativeMethods.GA_ROOT);
                if (root != IntPtr.Zero)
                {
                    TargetRootHwnd = root;
                    NativeMethods.SetForegroundWindow(root);
                }
            }
        }
        catch
        {
            // Best effort only — scrolling works even without foreground focus.
        }
    }

    /// <summary>Scrolls down one step (negative wheel delta).</summary>
    public void ScrollOnce()
    {
        int delta = -WheelTick * _wheelStep;
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT
            {
                dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                mouseData = unchecked((uint)delta),
            }
        };

        uint sent = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        if (sent != 1)
        {
            throw new InvalidOperationException($"SendInput wheel failed (sent={sent}, error={Marshal.GetLastWin32Error()}).");
        }
    }

    /// <summary>Scrolls up (used to reset / for manual sessions).</summary>
    public void ScrollUpOnce()
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            mi = new NativeMethods.MOUSEINPUT
            {
                dwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                mouseData = (uint)(WheelTick * _wheelStep),
            }
        };
        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>Moves the cursor back to the scroll anchor (just before each wheel event).</summary>
    public void MoveCursorToAnchor()
    {
        if (_anchorSet)
        {
            NativeMethods.SetCursorPos(_anchor.X, _anchor.Y);
        }
    }

    /// <summary>
    /// Moves the cursor OUT of the capture region after scrolling, so hover-rendered
    /// floating widgets (WeChat "jump to latest", chat toolbars) vanish before the frame
    /// is taken — and the cursor itself never appears in any frame.
    /// </summary>
    public void MoveCursorOut(Int32Rect region)
    {
        try
        {
            // first choice: just above the region (window title area)
            int x = region.X + Math.Max(1, region.Width / 2);
            int y = region.Y - 24;
            if (y < -64)
            {
                // no room above: left margin of the region
                x = region.X - 40;
                y = region.Y + Math.Max(1, region.Height / 2);
                if (x < -64)
                {
                    // fully covering screen: into the taskbar strip at the bottom
                    x = region.X + Math.Max(1, region.Width / 2);
                    y = region.Y + region.Height - 12;
                }
            }
            NativeMethods.SetCursorPos(x, y);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>Restores the original cursor position.</summary>
    public void Restore()
    {
        if (_cursorSaved)
        {
            _cursorSaved = false;
            _anchorSet = false;
            NativeMethods.SetCursorPos(_originalCursor.X, _originalCursor.Y);
        }
    }

    public void Dispose() => Restore();
}
