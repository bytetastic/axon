using System;

namespace Axon.Terminal;

[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
    DoubleUnderline = 256,
    CurlyUnderline = 512,
    Overline = 1024,
}

/// <summary>
/// Farbcodierung: 0 = Standardfarbe, 0x01000000 | Index = Palette (0–255), 0x02000000 | RGB = Truecolor.
/// </summary>
public struct CellAttr : IEquatable<CellAttr>
{
    public const uint PaletteTag = 0x01000000;
    public const uint RgbTag = 0x02000000;

    public uint Fg;
    public uint Bg;
    public CellFlags Flags;
    public ushort Link;

    public static uint Palette(int index) => PaletteTag | (uint)(index & 0xFF);
    public static uint Rgb(int r, int g, int b) => RgbTag | (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));

    public bool Equals(CellAttr o) => Fg == o.Fg && Bg == o.Bg && Flags == o.Flags && Link == o.Link;
    public override bool Equals(object? obj) => obj is CellAttr o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Fg, Bg, Flags, Link);
    public static bool operator ==(CellAttr a, CellAttr b) => a.Equals(b);
    public static bool operator !=(CellAttr a, CellAttr b) => !a.Equals(b);
}

public struct Cell
{
    /// <summary>Unicode-Codepoint; 0 = leer.</summary>
    public int Rune;
    /// <summary>0 = normal, 1 = linke Hälfte eines breiten Zeichens, 2 = rechte Hälfte.</summary>
    public byte Kind;
    public CellAttr Attr;

    public const byte Normal = 0, WideLead = 1, WideTail = 2;

    public Cell(int rune, byte kind, CellAttr attr) { Rune = rune; Kind = kind; Attr = attr; }
}

public sealed class TerminalLine
{
    public Cell[] Cells;
    /// <summary>Zeile wurde automatisch umgebrochen (für Kopieren ohne Zeilenumbruch).</summary>
    public bool Wrapped;
    /// <summary>Wird bei jeder Änderung erhöht – der Renderer zeichnet nur geänderte Zeilen neu.</summary>
    public int Version;

    public TerminalLine(int cols, CellAttr fill)
    {
        Cells = new Cell[cols];
        if (fill.Bg != 0 || fill.Flags != 0)
            for (int i = 0; i < cols; i++) Cells[i].Attr = fill;
    }

    public void Resize(int cols)
    {
        if (Cells.Length == cols) return;
        int old = Cells.Length;
        Array.Resize(ref Cells, cols);
        if (cols < old && cols > 0 && Cells[cols - 1].Kind == Cell.WideLead)
            Cells[cols - 1] = default;
        Version++;
    }
}

/// <summary>Ringpuffer für den Scrollback. <see cref="Dropped"/> zählt verworfene Zeilen für stabile absolute Indizes.</summary>
public sealed class LineRing
{
    TerminalLine[] _items;
    int _start;

    public int Count { get; private set; }
    public long Dropped { get; private set; }
    public int Capacity => _items.Length;

    public LineRing(int capacity) => _items = new TerminalLine[Math.Max(1, capacity)];

    public TerminalLine this[int i] => _items[(_start + i) % _items.Length];

    public void Add(TerminalLine line)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = line;
            Count++;
        }
        else
        {
            _items[_start] = line;
            _start = (_start + 1) % _items.Length;
            Dropped++;
        }
    }

    /// <summary>Entfernt die jüngste Zeile (für das Zurückholen beim Vergrößern).</summary>
    public TerminalLine? PopLast()
    {
        if (Count == 0) return null;
        int idx = (_start + Count - 1) % _items.Length;
        var l = _items[idx];
        _items[idx] = null!;
        Count--;
        return l;
    }

    public void Clear()
    {
        Dropped += Count;
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }
}
