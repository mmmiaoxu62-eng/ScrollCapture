using System.Windows.Input;
using ScrollCapture.Hotkeys;

namespace ScrollCapture.Tests;

public class HotkeySpecTests
{
    [Fact]
    public void Parse_DefaultCtrlShiftS()
    {
        HotkeySpec? spec = HotkeySpec.Parse("Ctrl+Shift+S");
        Assert.NotNull(spec);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, spec!.Modifiers);
        Assert.Equal(Key.S, spec.Key);
    }

    [Fact]
    public void Parse_ValidatesAltF12()
    {
        HotkeySpec? spec = HotkeySpec.Parse("Alt+F12");
        Assert.NotNull(spec);
        Assert.Equal(ModifierKeys.Alt, spec!.Modifiers);
        Assert.Equal(Key.F12, spec.Key);
    }

    [Fact]
    public void Parse_AcceptsMixedCaseAndSpaces()
    {
        HotkeySpec? spec = HotkeySpec.Parse("ctrl + shift + s");
        Assert.NotNull(spec);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Shift, spec!.Modifiers);
        Assert.Equal(Key.S, spec.Key);
    }

    [Fact]
    public void Parse_ReturnsNullForEmptyWhitespaceAndIncomplete()
    {
        Assert.Null(HotkeySpec.Parse(null));
        Assert.Null(HotkeySpec.Parse(""));
        Assert.Null(HotkeySpec.Parse("   "));
        Assert.Null(HotkeySpec.Parse("Ctrl+Shift"));   // no key part
        Assert.Null(HotkeySpec.Parse("Ctrl+Shift+")); // trailing empty part
        Assert.Null(HotkeySpec.Parse("NotAKey"));
    }

    [Fact]
    public void Parse_RejectsNumericKeyTokens()
    {
        // "1" would otherwise parse as Key.Cancel via numeric enum parsing.
        Assert.Null(HotkeySpec.Parse("Win+1"));
    }

    [Fact]
    public void ToDisplayString_FormatsDefaultHotkey()
    {
        HotkeySpec spec = HotkeySpec.Parse("Ctrl+Shift+S")!;
        Assert.Equal("Ctrl + Shift + S", spec.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_AllModifiers()
    {
        HotkeySpec spec = HotkeySpec.Parse("Win+Alt+Shift+Ctrl+P")!;
        Assert.Equal("Ctrl + Shift + Alt + Win + P", spec.ToDisplayString());
    }

    [Fact]
    public void ToNative_ProducesModifierFlags()
    {
        HotkeySpec spec = HotkeySpec.Parse("Ctrl+Shift+S")!;
        uint native = spec.ToNativeModifiers();
        Assert.Equal(0x0002 | 0x0004 | 0x4000u, native); // MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT
    }
}
