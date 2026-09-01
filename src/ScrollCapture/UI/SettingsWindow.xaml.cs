using System.Windows;
using System.Windows.Input;
using ScrollCapture.Hotkeys;
using ScrollCapture.Settings;
using ScrollCapture.Utils;

namespace ScrollCapture.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private HotkeySpec? _recordedHotkey;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        HotkeyBox.Text = HotkeySpec.Parse(settings.CaptureHotkey)?.ToDisplayString() ?? settings.CaptureHotkey;
        SaveDirBox.Text = settings.SaveDirectory;
        MaxHeightBox.Text = settings.MaxImageHeight.ToString();
        MaxFramesBox.Text = settings.MaxFrames.ToString();
    }

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Back || e.Key == Key.Escape)
        {
            _recordedHotkey = null;
            HotkeyBox.Clear();
            return;
        }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return; // waiting for the main key
        }

        ModifierKeys mods = Keyboard.Modifiers;
        var spec = new HotkeySpec(mods, e.Key);
        _recordedHotkey = spec;
        HotkeyBox.Text = spec.ToDisplayString();
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存目录" };
        if (dialog.ShowDialog(this) == true)
        {
            SaveDirBox.Text = dialog.FolderName;
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxHeightBox.Text.Trim(), out int maxHeight) || maxHeight < 100 || maxHeight > 200000)
        {
            MessageBox.Show(this, "最大高度需为 100~200000 之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(MaxFramesBox.Text.Trim(), out int maxFrames) || maxFrames < 1 || maxFrames > 2000)
        {
            MessageBox.Show(this, "最大帧数需为 1~2000 之间的整数。", "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_recordedHotkey != null)
        {
            string newSpec = _recordedHotkey.ToDisplayString();
            if (!App.CurrentApp.ReapplyCaptureHotkey(newSpec))
            {
                MessageBox.Show(this, $"快捷键 {newSpec} 注册失败（可能被其它程序占用），已保持原设置。",
                    "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                _settings.CaptureHotkey = newSpec;
            }
        }

        _settings.SaveDirectory = SaveDirBox.Text.Trim();
        _settings.MaxImageHeight = maxHeight;
        _settings.MaxFrames = maxFrames;
        SettingsService.Save(_settings);

        Close(); // shown modeless — no DialogResult
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
