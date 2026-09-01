using System.Text;
using System.Windows.Input;

namespace ScrollCapture.Hotkeys;

/// <summary>
/// A parsed hotkey like "Ctrl+Shift+S". Serialized as a plain string in settings.
/// </summary>
public sealed record HotkeySpec(ModifierKeys Modifiers, Key Key)
{
    public const string DefaultHotkey = "Ctrl+Alt+S";

    public static HotkeySpec? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        ModifierKeys modifiers = ModifierKeys.None;
        Key key = Key.None;

        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    // Numeric tokens like "1" would parse as enum values (e.g. Key.Cancel); reject them.
                    if (part.Length > 0 && char.IsLetter(part[0])
                        && Enum.TryParse(part, ignoreCase: true, out Key parsed) && parsed is not Key.None)
                    {
                        key = parsed;
                    }
                    else
                    {
                        return null;
                    }
                    break;
            }
        }

        if (key == Key.None)
        {
            return null;
        }

        return new HotkeySpec(modifiers, key);
    }

    public uint ToNativeModifiers()
    {
        uint modifiers = NativeUtils.ModifiersToNative(Modifiers);
        return modifiers | NativeUtils.MOD_NOREPEAT;
    }

    public uint ToVirtualKey()
    {
        return (uint)KeyInterop.VirtualKeyFromKey(Key);
    }

    public string ToDisplayString()
    {
        var sb = new StringBuilder();
        AppendModifierName(sb, ModifierKeys.Control, "Ctrl");
        AppendModifierName(sb, ModifierKeys.Shift, "Shift");
        AppendModifierName(sb, ModifierKeys.Alt, "Alt");
        AppendModifierName(sb, ModifierKeys.Windows, "Win");
        if (sb.Length > 0)
        {
            sb.Append(" + ");
        }
        sb.Append(Key);
        return sb.ToString();
    }

    private void AppendModifierName(StringBuilder sb, ModifierKeys modifier, string name)
    {
        if ((Modifiers & modifier) == modifier)
        {
            if (sb.Length > 0)
            {
                sb.Append(" + ");
            }
            sb.Append(name);
        }
    }

    private static class NativeUtils
    {
        public const uint MOD_NOREPEAT = 0x4000;

        public static uint ModifiersToNative(ModifierKeys modifiers)
        {
            uint native = 0;
            if ((modifiers & ModifierKeys.Alt) == ModifierKeys.Alt) native |= 0x0001;
            if ((modifiers & ModifierKeys.Control) == ModifierKeys.Control) native |= 0x0002;
            if ((modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) native |= 0x0004;
            if ((modifiers & ModifierKeys.Windows) == ModifierKeys.Windows) native |= 0x0008;
            return native;
        }
    }
}
