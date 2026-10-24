using System.Windows.Media;
using Axon.Visuals;

namespace Axon.Terminal;

/// <summary>Farben des Terminals – Grundfarben, Cursor und Auswahl kommen aus dem aktuellen Thema.</summary>
public static class TerminalTheme
{
    public static Color Foreground => ThemeManager.Current.TermForeground;
    /// <summary>Hintergrund für invertierte Zellen ohne eigene Farbe (die Fläche selbst bleibt transparent).</summary>
    public static Color Background => ThemeManager.Current.TermBackground;
    public static Color Cursor => ThemeManager.Current.TermCursor;
    public static Color Selection => ThemeManager.Current.TermSelection;
    public static Color Link => ThemeManager.Current.TermLink;

    /// <summary>Nach einem Themenwechsel: das Schwarz der Palette passt sich dem Grundton an.</summary>
    public static void Refresh() => Palette[0] = ThemeManager.Current.TermBlack;

    static readonly Color[] Base16 =
    {
        Color.FromRgb(0x10, 0x1C, 0x17), // schwarz (wird vom Thema ersetzt)
        Color.FromRgb(0xFF, 0x6B, 0x7A), // rot
        Color.FromRgb(0x4B, 0xE3, 0x9A), // grün
        Color.FromRgb(0xF5, 0xC6, 0x6B), // gelb
        Color.FromRgb(0x5C, 0xA8, 0xFF), // blau
        Color.FromRgb(0xC7, 0x92, 0xEA), // magenta
        Color.FromRgb(0x4F, 0xD6, 0xC8), // cyan
        Color.FromRgb(0xC9, 0xDE, 0xD3), // weiß
        Color.FromRgb(0x4A, 0x63, 0x58), // hellschwarz
        Color.FromRgb(0xFF, 0x8F, 0x9B),
        Color.FromRgb(0x7D, 0xF5, 0xB8),
        Color.FromRgb(0xFF, 0xD9, 0x8E),
        Color.FromRgb(0x8A, 0xC2, 0xFF),
        Color.FromRgb(0xDD, 0xB5, 0xF5),
        Color.FromRgb(0x83, 0xF0, 0xE2),
        Color.FromRgb(0xF0, 0xFA, 0xF5),
    };

    public static readonly Color[] Palette = BuildPalette();

    static Color[] BuildPalette()
    {
        var p = new Color[256];
        for (int i = 0; i < 16; i++) p[i] = Base16[i];
        byte[] levels = { 0, 95, 135, 175, 215, 255 };
        for (int i = 0; i < 216; i++)
            p[16 + i] = Color.FromRgb(levels[i / 36], levels[i / 6 % 6], levels[i % 6]);
        for (int i = 0; i < 24; i++)
        {
            byte v = (byte)(8 + i * 10);
            p[232 + i] = Color.FromRgb(v, v, v);
        }
        return p;
    }

    public static Color Resolve(uint c, Color fallback)
    {
        if (c == 0) return fallback;
        if ((c & CellAttr.RgbTag) != 0) return Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c);
        return Palette[c & 0xFF];
    }
}
