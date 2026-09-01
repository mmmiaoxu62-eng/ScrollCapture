using System.Windows;
using System.Windows.Interop;
using ScrollCapture.Utils;

namespace ScrollCapture.Hotkeys;

/// <summary>
/// Registers a single global hotkey (Win32 RegisterHotKey) on a hidden message-only window.
/// Must be created and used on the WPF UI thread.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int HotkeyId = 0x5C01;

    private HwndSource? _messageSource;
    private bool _registered;
    private HotkeySpec? _current;

    public event EventHandler? HotkeyPressed;

    public bool IsRegistered => _registered;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("ScrollCapture Hotkey Host")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);
    }

    public bool Register(string? hotkeyText)
    {
        Unregister();
        HotkeySpec? spec = HotkeySpec.Parse(hotkeyText);
        if (spec == null || _messageSource == null)
        {
            return false;
        }

        bool ok = NativeMethods.RegisterHotKey(_messageSource.Handle, HotkeyId, spec.ToNativeModifiers(), spec.ToVirtualKey());
        if (ok)
        {
            _registered = true;
            _current = spec;
        }
        return ok;
    }

    public void Unregister()
    {
        if (_registered && _messageSource != null)
        {
            NativeMethods.UnregisterHotKey(_messageSource.Handle, HotkeyId);
            _registered = false;
            _current = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt64() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _messageSource?.RemoveHook(WndProc);
        _messageSource?.Dispose();
        _messageSource = null;
    }
}
