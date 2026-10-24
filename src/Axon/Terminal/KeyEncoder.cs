using System.Windows.Input;
using Axon.Native;

namespace Axon.Terminal;

/// <summary>
/// Übersetzt Tasten in Eingabesequenzen. Normale Zeichen kommen über TextInput (Tottasten, AltGr, IME);
/// hier landen nur Sondertasten und Strg/Alt-Kombinationen.
/// Im Win32-Eingabemodus (ConPTY, ?9001h) werden diese als vollständige Tastenereignisse kodiert.
/// </summary>
public static class KeyEncoder
{
    public static string? Encode(Key key, ModifierKeys mods, bool appCursor, bool win32)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (IsSpecial(key))
        {
            if (win32) return Win32(key, mods, SpecialChar(key, ctrl));
            return Vt(key, ctrl, alt, shift, appCursor);
        }

        // AltGr (= Strg+Alt) erzeugt Zeichen wie @ € { } – das übernimmt TextInput.
        if (ctrl && alt) return null;

        if (ctrl)
        {
            int cc = CtrlChar(key, shift);
            if (cc < 0) return null;
            if (win32) return Win32(key, mods, cc);
            return ((char)cc).ToString();
        }

        if (alt)
        {
            int vk = KeyInterop.VirtualKeyFromKey(key);
            uint ch = Win32Api.MapVirtualKeyW((uint)vk, 2) & 0x7FFF; // MAPVK_VK_TO_CHAR
            if (ch == 0) return null;
            char c = (char)ch;
            c = shift ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c);
            if (win32) return Win32(key, mods, c);
            return "\x1b" + c;
        }

        return null;
    }

    /// <summary>Tastenereignis Alt+V – Claude Code liest damit ein Bild aus der Zwischenablage.</summary>
    public static string AltV(bool win32) => win32 ? Win32(Key.V, ModifierKeys.Alt, 'v') : "\x1bv";

    static bool IsSpecial(Key k) => k switch
    {
        Key.Enter or Key.Tab or Key.Back or Key.Escape or Key.Up or Key.Down or Key.Left or Key.Right or
        Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete => true,
        >= Key.F1 and <= Key.F12 => true,
        _ => false,
    };

    static int SpecialChar(Key k, bool ctrl) => k switch
    {
        Key.Enter => ctrl ? 10 : 13,
        Key.Tab => 9,
        Key.Back => ctrl ? 0x7F : 8,
        Key.Escape => 27,
        _ => 0,
    };

    static int CtrlChar(Key k, bool shift)
    {
        if (k >= Key.A && k <= Key.Z) return k - Key.A + 1;
        return k switch
        {
            Key.Space => 0,
            Key.D2 => 0,
            Key.OemOpenBrackets or Key.D3 => 27,
            Key.Oem5 or Key.OemBackslash or Key.D4 => 28,
            Key.OemCloseBrackets or Key.D5 => 29,
            Key.D6 => 30,
            Key.OemMinus or Key.D7 or Key.OemQuestion => 31,
            Key.D8 => 127,
            _ => -1,
        };
    }

    static int Mod(bool ctrl, bool alt, bool shift) => 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

    static string Vt(Key k, bool ctrl, bool alt, bool shift, bool appCursor)
    {
        int m = Mod(ctrl, alt, shift);
        string esc = alt ? "\x1b" : "";
        switch (k)
        {
            case Key.Enter: return esc + "\r";
            case Key.Tab: return shift ? "\x1b[Z" : esc + "\t";
            case Key.Back: return esc + (ctrl ? "\x08" : "\x7f");
            case Key.Escape: return "\x1b";
        }
        char? letter = k switch
        {
            Key.Up => 'A', Key.Down => 'B', Key.Right => 'C', Key.Left => 'D', Key.Home => 'H', Key.End => 'F',
            Key.F1 => 'P', Key.F2 => 'Q', Key.F3 => 'R', Key.F4 => 'S',
            _ => null,
        };
        if (letter != null)
        {
            bool isF = k >= Key.F1 && k <= Key.F4;
            if (m == 1) return (isF || appCursor ? "\x1bO" : "\x1b[") + letter;
            return $"\x1b[1;{m}{letter}";
        }
        int num = k switch
        {
            Key.Insert => 2, Key.Delete => 3, Key.PageUp => 5, Key.PageDown => 6,
            Key.F5 => 15, Key.F6 => 17, Key.F7 => 18, Key.F8 => 19, Key.F9 => 20, Key.F10 => 21, Key.F11 => 23, Key.F12 => 24,
            _ => 0,
        };
        if (num == 0) return "";
        return m == 1 ? $"\x1b[{num}~" : $"\x1b[{num};{m}~";
    }

    static string Win32(Key key, ModifierKeys mods, int uc)
    {
        int vk = KeyInterop.VirtualKeyFromKey(key);
        uint sc = Win32Api.MapVirtualKeyW((uint)vk, 0);
        int cs = 0;
        if ((mods & ModifierKeys.Shift) != 0) cs |= 0x10;
        if ((mods & ModifierKeys.Control) != 0) cs |= Keyboard.IsKeyDown(Key.RightCtrl) ? 0x04 : 0x08;
        if ((mods & ModifierKeys.Alt) != 0) cs |= Keyboard.IsKeyDown(Key.RightAlt) ? 0x01 : 0x02;
        if (Keyboard.IsKeyToggled(Key.NumLock)) cs |= 0x20;
        if (Keyboard.IsKeyToggled(Key.CapsLock)) cs |= 0x80;
        if (key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown or Key.Insert or Key.Delete)
            cs |= 0x100;
        return $"\x1b[{vk};{sc};{uc};1;{cs};1_";
    }
}

static class Win32Api
{
    public static uint MapVirtualKeyW(uint code, uint type) => Win32.MapVirtualKeyW(code, type);
}
