using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScrollCapture.Capture;
using ScrollCapture.Hotkeys;
using ScrollCapture.Scrolling;
using ScrollCapture.Settings;
using ScrollCapture.Stitching;
using ScrollCapture.UI;
using ScrollCapture.Utils;

namespace ScrollCapture;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private bool _captureActive;


    private CancellationTokenSource? _sessionCts;
    private LongCaptureSession? _session;
    private ProgressToast? _toast;


    public bool HotkeyRegistered { get; }

    public MainWindow(HotkeyManager hotkeyManager, AppSettings settings, bool hotkeyRegistered)
    {
        InitializeComponent();
        _settings = settings;
        HotkeyRegistered = hotkeyRegistered;

        HotkeySpec parsed = HotkeySpec.Parse(settings.CaptureHotkey) ?? new HotkeySpec(ModifierKeys.None, Key.None);
        HotkeyText.Text = hotkeyRegistered
            ? parsed.ToDisplayString()
            : $"{parsed.ToDisplayString()}　(注册失败：可能被其他程序占用)";
        StatusText.Text = hotkeyRegistered
            ? $"就绪 · 快捷键 {parsed.ToDisplayString()} · 再次按下可停止"
            : "快捷键注册失败，可直接点击按钮开始截图。";
    }

    private void OnAutoCaptureClick(object sender, RoutedEventArgs e) => BeginCapture();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => App.CurrentApp.ShowSettings();

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        string? zip = DiagnosticsExporter.Export(_settings.SaveDirectory);
        if (zip == null)
        {
            StatusText.Text = "诊断包导出失败（详见日志）。";
            return;
        }
        StatusText.Text = $"诊断包已导出：{zip}";
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", "/select," + zip);
        }
        catch (Exception ex)
        {
            Logger.Warn("open explorer failed: " + ex.Message);
        }
    }

    /// <summary>Hotkey entry point: capture/stop toggle.</summary>
    public void HandleHotkey()
    {
        if (_captureActive)
        {
            CancelRunningSession();
            return;
        }
        BeginCapture();
    }

    public void UpdateHotkeyDisplay(string spec)
    {
        HotkeyText.Text = HotkeySpec.Parse(spec)?.ToDisplayString() ?? spec;
        StatusText.Text = $"就绪 · 快捷键 {HotkeyText.Text} · 再次按下可停止";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.CurrentApp.IsExiting)
        {
            // resident in tray — closing the window just hides it
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    public void BeginCapture()
    {
        if (_captureActive)
        {
            // Second hotkey press while a session runs = stop.
            CancelRunningSession();
            return;
        }
        _captureActive = true;

        Hide(); // keep our own window out of the screenshot
        var virtualScreen = DpiManager.GetVirtualScreenPhysical();
        var overlay = new CaptureOverlay(virtualScreen);
        overlay.Finished += OnOverlayFinished;
        overlay.Show();
    }

    private void OnOverlayFinished(Int32Rect? region)
    {
        _captureActive = false;

        if (region is null)
        {
            StatusText.Text = "已取消。";
            Show();
            return;
        }

        StartAutoSession(region.Value);
    }

    private void StartAutoSession(Int32Rect region)
    {
        StatusText.Text = "正在准备自动截长屏…";

        _sessionCts = new CancellationTokenSource();
        var framesDir = Path.Combine(AppPaths.DataDir, "temp", $"session_{DateTime.Now:yyyyMMdd_HHmmss}");
        int maxFrames = _settings.MaxFrames;

        bool scrollDown = _settings.ScrollDirection != "Up";
        _session = new LongCaptureSession(
            region,
            options: new ScrollOptions { ScrollDown = scrollDown },
            maxFrames: maxFrames,
            framesDirectory: framesDir,
            token: _sessionCts.Token,
            fixedRegionDebug: _settings.FixedRegionDebug);
        StatusText.Text = $"正在准备自动截长屏（{(scrollDown ? "向下" : "向上")}）…";

        _toast = new ProgressToast();
        _toast.StopRequested += () => _sessionCts.Cancel();
        _toast.Update($"正在截取… 0 / {maxFrames} · 点击或再按快捷键停止");
        _toast.PositionAbove(region, DpiManager.GetVirtualScreenPhysical());

        _ = Task.Run(async () =>
        {
            SessionResult result = await _session.RunAsync(count =>
                Dispatcher.BeginInvoke(() => _toast?.Update($"正在截取… {count} / {maxFrames} · 点击或再按快捷键停止")));
            _ = Dispatcher.BeginInvoke(() => FinishAutoSession(result));
        });
    }

    private void FinishAutoSession(SessionResult result)
    {
        DetailCleanupSession();
        int maxImageHeight = _settings.MaxImageHeight;

        switch (result.Reason)
        {
            case SessionStopReason.Error:
            default:
                StatusText.Text = $"截取失败：{result.Error?.Message ?? "未知错误"}";
                Show();
                return;
            case SessionStopReason.ReachedBottom:
            case SessionStopReason.LimitReached:
            case SessionStopReason.Cancelled:
            case SessionStopReason.Unstable:
                break;
        }

        if (result.StitchedImage == null)
        {
            StatusText.Text = $"未获得有效拼接图（{ReasonText(result.Reason)}，共 {result.FrameCount} 帧）。帧文件在：\n{result.FramesDirectory}";
            Show();
            return;
        }
        TempSessionCleaner.CleanupKeepLatest(1);

        _ = Task.Run(() =>
        {
            try
            {
                BitmapSource stitched = result.StitchedImage;
                string path = SavePng(stitched, "longshot");
                string stats = string.Join(" · ", (result.StitchSteps ?? Array.Empty<StitchStepReport>()).Select(s =>
                    s.Skipped ? "重复帧" : s.UsedFallback ? "估计" : $"重叠{s.OverlapHeight}px(置信{s.Confidence:F2})"));
                string warnings = ((result.StitchWarnings ?? Array.Empty<string>()).Count > 0
                    ? "\n⚠ " + string.Join("\n⚠ ", result.StitchWarnings)
                    : "");
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"拼接完成：{stitched.PixelWidth} × {stitched.PixelHeight} px（{result.FrameCount} 帧）\n已保存：{path}" +
                                      warnings + $"\n({stats})";
                    ShowPreview(stitched, "已自动复制到剪贴板", path);
                    Show();
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Saving stitched image failed", ex);
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"拼接失败：{ex.Message}\n原始帧保留在：{result.FramesDirectory}";
                    Show();
                });
            }
        });
    }

    private void ShowPreview(BitmapSource bitmap, string? note = null, string? savedFilePath = null)
    {
        try
        {
            Clipboard.SetImage(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Auto clipboard copy failed: {ex.Message}");
        }
        var preview = new PreviewWindow(bitmap, _settings, note, savedFilePath);
        preview.RetakeRequested += () => BeginCapture();
        preview.Show();
    }

    private static string ReasonText(SessionStopReason reason) => reason switch
    {
        SessionStopReason.ReachedBottom => "到底部",
        SessionStopReason.LimitReached => "达到帧数上限",
        SessionStopReason.Cancelled => "手动停止",
        SessionStopReason.Unstable => "滚动不稳定",
        _ => reason.ToString()
    };

    private void CancelRunningSession()
    {
        if (_sessionCts != null)
        {
            StatusText.Text = "正在停止…";
            try
            {
                _sessionCts.Cancel();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void DetailCleanupSession()
    {
        _toast?.Close();
        _toast = null;
        _session?.Dispose();
        _session = null;
        _sessionCts?.Dispose();
        _sessionCts = null;
    }

    private string SavePng(BitmapSource bitmap, string prefix = "longshot")
    {
        Directory.CreateDirectory(_settings.SaveDirectory);
        string path = Path.Combine(_settings.SaveDirectory, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        encoder.Save(stream);
        return path;
    }

    private static string FileSizeText(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}

