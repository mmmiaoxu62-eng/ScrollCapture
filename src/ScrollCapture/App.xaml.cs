using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using ScrollCapture.Hotkeys;
using ScrollCapture.Settings;
using ScrollCapture.UI;
using ScrollCapture.Utils;

namespace ScrollCapture;

public partial class App : Application
{
    public const string AppName = "ScrollCapture";

    private Mutex? _singleInstanceMutex;
    private HotkeyManager? _hotkeyManager;
    private TaskbarIcon? _tray;
    private SettingsWindow? _settingsWindow;

    public static AppSettings Settings { get; private set; } = new();
    public bool CaptureHotkeyRegistered { get; private set; }
    public bool IsExiting { get; private set; }
    public MainWindow? CurrentMainWindow { get; private set; }
    public static App CurrentApp => (App)Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallExceptionHandlers();
        Logger.Info($"Application started. Version={GetType().Assembly.GetName().Version}");

        _singleInstanceMutex = new Mutex(true, "Global\\ScrollCapture-SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ScrollCapture 已经在运行中。", AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        Settings = SettingsService.Load();
        TempSessionCleaner.PruneOlderThan(14);

        _hotkeyManager = new HotkeyManager();
        CaptureHotkeyRegistered = _hotkeyManager.Register(Settings.CaptureHotkey);
        if (!CaptureHotkeyRegistered)
        {
            Logger.Warn($"Hotkey registration failed: {Settings.CaptureHotkey} (likely in use by another app)");
        }
        _hotkeyManager.HotkeyPressed += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => CurrentMainWindow?.HandleHotkey());
        };

        var mainWindow = new MainWindow(_hotkeyManager, Settings, CaptureHotkeyRegistered);
        MainWindow = mainWindow;
        CurrentMainWindow = mainWindow;
        mainWindow.Show();

        SetupTray();
    }

    private void SetupTray()
    {
        _tray = new TaskbarIcon
        {
            IconSource = CreateTrayGlyph(),
            ToolTipText = "ScrollCapture 截长屏",
        };
        _tray.TrayMouseDoubleClick += (_, _) => ShowMainWindow();

        var menu = new ContextMenu();
        MenuItem Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }
        Add("开始截长屏", () => CurrentMainWindow?.BeginCapture());
        Add("主窗口", ShowMainWindow);
        Add("设置", ShowSettings);
        menu.Items.Add(new Separator());
        Add("退出", ExitRequested);

        _tray.ContextMenu = menu;
    }

    private void ShowMainWindow()
    {
        if (CurrentMainWindow is { } main)
        {
            main.Show();
            main.Activate();
        }
    }

    public void ShowSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(Settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Re-registers the capture hotkey (returns false when the combo is taken).</summary>
    public bool ReapplyCaptureHotkey(string spec)
    {
        if (_hotkeyManager == null)
        {
            return false;
        }
        if (_hotkeyManager.Register(spec))
        {
            Settings.CaptureHotkey = spec;
            CaptureHotkeyRegistered = true;
            CurrentMainWindow?.UpdateHotkeyDisplay(spec);
            Logger.Info($"Hotkey changed to {spec}");
            return true;
        }
        CaptureHotkeyRegistered = false;
        return false;
    }

    private void ExitRequested()
    {
        IsExiting = true;
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkeyManager?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private ImageSource CreateTrayGlyph()
    {
        var group = new DrawingGroup();
        var rect = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(30, 144, 255)), null,
            new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(0, 0, 32, 32)));
        group.Children.Add(rect);
        var bars = new GeometryDrawing(Brushes.White, null, new GeometryGroup
        {
            Children =
            {
                new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(6, 8, 20, 4)),
                new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(6, 14, 20, 4)),
                new System.Windows.Media.RectangleGeometry(new System.Windows.Rect(6, 20, 14, 4)),
            }
        });
        group.Children.Add(bars);
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Unhandled AppDomain exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled dispatcher exception", e.Exception);
        MessageBox.Show($"发生未处理的异常：\n\n{e.Exception.Message}\n\n日志已写入 {Logger.CurrentLogFilePath}",
            AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
