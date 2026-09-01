using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Capture;

namespace ScrollCapture.Tests;

public class Diagnostics3Tests
{
    [Fact]
    public void ComparePwAndBltLiveness()
    {
        if (!DiagnosticGate.Enabled) return;

        IntPtr hwnd = IntPtr.Zero;
        foreach (var p in Process.GetProcessesByName("msedge"))
        {
            if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Contains("Wikipedia"))
            {
                hwnd = p.MainWindowHandle;
                break;
            }
        }
        if (hwnd == IntPtr.Zero) throw new Xunit.Sdk.XunitException("no wikipedia edge window");

        ShowWindow(hwnd, 3);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
        {
            throw new Xunit.Sdk.XunitException("could not foreground edge (SetForegroundWindow refused)");
        }
        System.Threading.Thread.Sleep(600);

        var region = new Int32Rect(500, 300, 600, 400);
        Scroll(hwnd); System.Threading.Thread.Sleep(900);
        var pw1 = CaptureEngine.CaptureViaPrintWindow(hwnd, region);
        Scroll(hwnd); System.Threading.Thread.Sleep(900);
        var pw2 = CaptureEngine.CaptureViaPrintWindow(hwnd, region);
        int pwDiff = CountDiff(pw1, pw2);

        var blt1 = ScreenCaptureService.Capture(region);
        System.Threading.Thread.Sleep(600);
        var blt2 = ScreenCaptureService.Capture(region);
        int bltDiff = CountDiff(blt1, blt2);

        throw new Xunit.Sdk.XunitException(
            $"pw: {pw1.PixelWidth}x{pw1.PixelHeight} diff={pwDiff}  |  blt: {blt1.PixelWidth}x{blt1.PixelHeight} diff={bltDiff}");
    }

    private static void Scroll(IntPtr hwnd)
    {
        SetCursorPos(640, 540);
        SendInput(new INPUT
        {
            type = 0,
            mi = new MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)-480) }
        });
    }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(INPUT i);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    private static int CountDiff(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return -1;
        int w = a.PixelWidth, h = a.PixelHeight;
        var xa = new byte[w * h * 4];
        var xb = new byte[w * h * 4];
        a.CopyPixels(xa, w * 4, 0);
        b.CopyPixels(xb, w * 4, 0);
        int diff = 0, n = 0;
        for (int i = 0; i < xa.Length; i += 64)
        {
            if (xa[i] != xb[i]) diff++;
            n++;
        }
        return diff;
    }
}
