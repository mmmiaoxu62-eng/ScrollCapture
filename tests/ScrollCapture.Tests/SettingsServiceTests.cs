using System.IO;
using ScrollCapture.Settings;

namespace ScrollCapture.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"scrollcapture_settings_{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var settings = new AppSettings
        {
            CaptureHotkey = "Alt+Shift+X",
            SaveDirectory = Path.Combine(Path.GetTempPath(), "shots"),
            MaxImageHeight = 50000,
            MaxFrames = 50,
        };

        Assert.True(SettingsService.Save(settings, _tempFile));
        AppSettings loaded = SettingsService.Load(_tempFile);

        Assert.Equal(settings.CaptureHotkey, loaded.CaptureHotkey);
        Assert.Equal(settings.SaveDirectory, loaded.SaveDirectory);
        Assert.Equal(settings.MaxImageHeight, loaded.MaxImageHeight);
        Assert.Equal(settings.MaxFrames, loaded.MaxFrames);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        AppSettings loaded = SettingsService.Load(Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.json"));
        Assert.Equal("Ctrl+Alt+S", loaded.CaptureHotkey);
        Assert.Equal(30000, loaded.MaxImageHeight);
        Assert.Equal(100, loaded.MaxFrames);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaultsWithoutThrowing()
    {
        File.WriteAllText(_tempFile, "{ this is not valid json ]");
        AppSettings loaded = SettingsService.Load(_tempFile);
        Assert.NotNull(loaded);
        Assert.Equal("Ctrl+Alt+S", loaded.CaptureHotkey);
    }

    [Fact]
    public void Load_EmptyJson_ReturnsDefaults()
    {
        File.WriteAllText(_tempFile, string.Empty);
        AppSettings loaded = SettingsService.Load(_tempFile);
        Assert.Equal("Ctrl+Alt+S", loaded.CaptureHotkey);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }
}
