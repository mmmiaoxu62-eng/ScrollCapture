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
    private bool _pendingAutoMode;

    private CancellationTokenSource? _sessionCts;
    private LongCaptureSession? _session;
    private ProgressToast? _toast;
    private ManualCaptureSession? _manualSession;
    private bool _pendingManual;

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

    private void OnAutoCaptureClick(object sender, RoutedEventArgs e) => BeginCapture(autoMode: true);

    private void OnSingleCaptureClick(object sender, RoutedEventArgs e) => BeginCapture(autoMode: false);

    private void OnManualCaptureClick(object sender, RoutedEventArgs e) => BeginCapture(autoMode: false, manual: true);

    private void OnSettingsClick(object sender, RoutedEventArgs e) => App.CurrentApp.ShowSettings();

    /// <summary>Hotkey entry point: manual session add-frame, else capture/stop toggle.</summary>
    public void HandleHotkey()
    {
        if (_manualSession != null)
        {
            ManualAddFrame();
            return;
        }
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

    public void BeginCapture(bool autoMode = true, bool manual = false)
    {
        if (_captureActive)
        {
            // Second hotkey press while a session runs = stop.
            CancelRunningSession();
            return;
        }
        if (_manualSession != null)
        {
            return;
        }
        _captureActive = true;
        _pendingAutoMode = autoMode;
        _pendingManual = manual;

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

        if (_pendingManual)
        {
            StartManualSession(region.Value);
        }
        else if (_pendingAutoMode)
        {
            StartAutoSession(region.Value);
        }
        else
        {
            CaptureSingleAndSave(region.Value);
        }
    }

    private void StartManualSession(Int32Rect region)
    {
        _manualSession = new ManualCaptureSession(region, _settings.MaxImageHeight);
        _manualSession.FrameAdded += (count, height) => Dispatcher.BeginInvoke(() =>
            _toast?.Update($"手动截取… 第 {count} 帧 · 高度 {height}px · Ctrl+Alt+S 加帧 · 点击完成"));

        _toast = new ProgressToast();
        _toast.StopRequested += () => Dispatcher.BeginInvoke(() => FinishManualSession());
        _toast.Update("手动模式：自己滚动 → 按 Ctrl+Alt+S 加一帧 · 点击这里完成");
        _toast.PositionAbove(region, DpiManager.GetVirtualScreenPhysical());
        StatusText.Text = "手动模式进行中：滚动后按 Ctrl+Alt+S 逐帧添加。";
    }

    private void ManualAddFrame()
    {
        if (_manualSession == null)
        {
            return;
        }
        _manualSession.AddFrame(out string? warning);
        if (warning != null)
        {
            FinishManualSession(warning);
        }
    }

    private void FinishManualSession(string? warning = null)
    {
        ManualCaptureSession? manual = _manualSession;
        _manualSession = null;
        _toast?.Close();
        _toast = null;
        Show();

        if (manual == null || manual.FrameCount == 0)
        {
            StatusText.Text = "手动模式：未添加任何帧。";
            return;
        }

        BitmapSource? stitched = manual.Finish();
        if (stitched == null)
        {
            StatusText.Text = "手动模式：拼接失败。";
            return;
        }
        string stats = string.Join(" · ", manual.Steps.Select(s =>
            s.Skipped ? "重复帧" : s.UsedFallback ? "估计" : $"重叠{s.OverlapHeight}px(置信{s.Confidence:F2})"));
        StatusText.Text = $"手动拼接完成：{stitched.PixelWidth} × {stitched.PixelHeight} px（{manual.FrameCount} 帧）" +
                          (warning != null ? $"\n{warning}" : "") +
                          (manual.Warnings.Count > 0 ? "\n⚠ " + string.Join("\n⚠ ", manual.Warnings) : "") +
                          $"\n({stats})";
        ShowPreview(stitched, "手动拼接 · 已复制到剪贴板");
    }

    private void StartAutoSession(Int32Rect region)
    {
        StatusText.Text = "正在准备自动截长屏…";

        _sessionCts = new CancellationTokenSource();
        var framesDir = Path.Combine(AppPaths.DataDir, "temp", $"session_{DateTime.Now:yyyyMMdd_HHmmss}");
        int maxFrames = _settings.MaxFrames;

        _session = new LongCaptureSession(
            region,
            options: new ScrollOptions(),
            maxFrames: maxFrames,
            framesDirectory: framesDir,
            token: _sessionCts.Token);

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
                    ShowPreview(stitched, "已自动复制到剪贴板");
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

    private void ShowPreview(BitmapSource bitmap, string? note = null)
    {
        try
        {
            Clipboard.SetImage(bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Auto clipboard copy failed: {ex.Message}");
        }
        var preview = new PreviewWindow(bitmap, _settings, note);
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

    private void CaptureSingleAndSave(Int32Rect region)
    {
        StatusText.Text = "正在截图并保存…";
        Task.Run(() =>
        {
            try
            {
                BitmapSource bitmap = ScreenCaptureService.Capture(region);
                string path = SavePng(bitmap);
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"已保存：{path}（{bitmap.PixelWidth} × {bitmap.PixelHeight} px，{FileSizeText(new FileInfo(path).Length)}）";
                    ShowPreview(bitmap, "已自动复制到剪贴板");
                    Show();
                });
            }
            catch (Exception ex)
            {
                Logger.Error("Capture failed", ex);
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = $"截图失败：{ex.Message}";
                    Show();
                });
            }
        });
    }

    private string SavePng(BitmapSource bitmap, string prefix = "capture")
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
