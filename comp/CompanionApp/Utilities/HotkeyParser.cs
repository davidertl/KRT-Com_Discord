using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace CompanionApp.Utilities;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

public readonly struct HotkeyCombo
{
    public HotkeyCombo(Key key, HotkeyModifiers modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public Key Key { get; }
    public HotkeyModifiers Modifiers { get; }
}

public static class HotkeyParser
{
    public static bool TryParse(string input, out HotkeyCombo combo)
    {
        combo = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var parts = input.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var keyPart = parts.Last();

        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Win;
                    break;
                default:
                    return false;
            }
        }

        if (!Enum.TryParse<Key>(keyPart, true, out var key))
        {
            return false;
        }

        combo = new HotkeyCombo(key, modifiers);
        return true;
    }
}
