using System;
using System.Windows;
using System.Windows.Media;
using Axon.Visuals;

namespace Axon.Terminal;

/// <summary>
/// Claudes Antworten im Verlauf: erkennen, passend zum Thema hervorheben und per Klick anspringen.
/// <para>
/// Claude Code beginnt jeden Block mit einem Punkt in Spalte 0 (Windows „●“, macOS „⏺“). Bei Textantworten hat er die
/// Themenfarbe „text“ (dunkle Themen Weiß, helle Schwarz), bei Werkzeugaufrufen Grün/Rot/gedimmt, bei Warnungen Gelb.
/// Der Text darunter ist um zwei Spalten eingerückt; alles mit Inhalt in Spalte 0 oder 1 beginnt etwas Neues
/// (nächster Punkt, Eingabe „&gt;“, Rahmen des Eingabefelds, Spinner). Geprüft an Claude Code 2.1.316.
/// </para>
/// </summary>
public sealed partial class TerminalView
{
    // Markierung einer Bildschirmzeile (im RowVisual gemerkt, damit nur geänderte Zeilen neu gerastert werden)
    const byte MarkNone = 0, MarkStart = 1, MarkBody = 2, MarkLast = 4;

    /// <summary>Hervorhebung der Antworten an/aus (Einstellung).</summary>
    public static bool HighlightAnswers { get; set; } = true;
    /// <summary>Kurzes Aufleuchten nach einem Sprung (aus, wenn Animationen abgeschaltet sind).</summary>
    public static bool FlashAnswers { get; set; } = true;

    byte[] _marks = Array.Empty<byte>();
    /// <summary>Ansicht oder Inhalt hat sich geändert – die Pfeile beim nächsten Bild neu bewerten.</summary>
    bool _navDirty;

    static bool IsBullet(int r) => r == 0x25CF || r == 0x23FA;
    static bool IsBlank(int r) => r == 0 || r == ' ';

    static readonly uint[] TextColors =
    {
        0, CellAttr.Rgb(255, 255, 255), CellAttr.Rgb(0, 0, 0),
        CellAttr.Palette(15), CellAttr.Palette(7), CellAttr.Palette(0), CellAttr.Palette(231), CellAttr.Palette(16),
    };

    /// <summary>Erste Zeile einer Textantwort: Punkt in Themenfarbe „text“, Leerzeichen, dann Text.</summary>
    static bool IsAnswerStart(TerminalLine? l)
    {
        if (l == null || l.Cells.Length < 3) return false;
        var c = l.Cells[0];
        if (!IsBullet(c.Rune) || c.Kind != Cell.Normal || !IsBlank(l.Cells[1].Rune) || IsBlank(l.Cells[2].Rune)) return false;
        if ((c.Attr.Flags & (CellFlags.Dim | CellFlags.Hidden | CellFlags.Inverse)) != 0) return false;
        return Array.IndexOf(TextColors, c.Attr.Fg) >= 0;
    }

    /// <summary>Inhalt in Spalte 0 oder 1 – hier beginnt etwas Neues, eine laufende Antwort ist zu Ende.</summary>
    static bool BreaksBlock(TerminalLine l) => l.Cells.Length < 2 || !IsBlank(l.Cells[0].Rune) || !IsBlank(l.Cells[1].Rune);

    static bool IsBlankLine(TerminalLine l)
    {
        foreach (var c in l.Cells) if (!IsBlank(c.Rune)) return false;
        return true;
    }

    /// <summary>Gehört die Zeile (nicht leer, eingerückt) zum Text einer Antwort, die weiter oben begann?</summary>
    bool InsideAnswer(TerminalEmulator emu, long abs)
    {
        for (long a = abs - 1, lim = Math.Max(emu.FirstAbs, abs - 4000); a >= lim; a--)
        {
            var l = emu.GetLineAbs(a);
            if (l == null) return false;
            if (BreaksBlock(l)) return IsAnswerStart(l);
        }
        return false;
    }

    /// <summary>Markierungen für die Zeilen ab <paramref name="top"/> berechnen. Nur unter emu.Sync aufrufen.</summary>
    void ComputeMarks(TerminalEmulator emu, long top, int count)
    {
        if (_marks.Length < count + 1) _marks = new byte[count + 1];
        Array.Clear(_marks);
        if (!HighlightAnswers || emu.AltActive) return;

        // Zustand oberhalb des Bildes, dann vorwärts (eine Zeile mehr, um das Blockende zu kennen)
        bool inside = InsideAnswer(emu, top);
        for (int r = 0; r <= count; r++)
        {
            var l = emu.GetLineAbs(top + r);
            byte m = MarkNone;
            if (l == null) inside = false;
            else if (IsAnswerStart(l)) { inside = true; m = MarkStart; }
            else if (BreaksBlock(l)) inside = false;
            else if (inside) m = MarkBody;
            _marks[r] = m;
        }

        // Leerzeilen gehören nur dazu, wenn danach noch Text derselben Antwort folgt. Dafür hinter dem Bild weiterschauen.
        bool nextInside = false;
        {
            bool state = inside;
            for (long a = top + count + 1, lim = top + count + 200; a < lim; a++)
            {
                var l = emu.GetLineAbs(a);
                if (l == null) break;
                if (IsBlankLine(l)) continue;
                nextInside = state && !BreaksBlock(l);
                break;
            }
        }
        for (int r = count; r >= 0; r--)
        {
            byte m = _marks[r];
            var l = emu.GetLineAbs(top + r);
            if (m == MarkBody && l != null && IsBlankLine(l))
            {
                if (!nextInside) _marks[r] = MarkNone;
                continue;
            }
            nextInside = m == MarkBody;
        }
        for (int r = 0; r < count; r++)
            if (_marks[r] != MarkNone && _marks[r + 1] != MarkBody) _marks[r] |= MarkLast;
        // letzte berechnete Zeile: Ende, wenn die nächste nicht mehr dazugehört
        if (_marks[count] != MarkNone && !nextInside) _marks[count] |= MarkLast;
    }

    // ───────────────────────────── Zeichnen ─────────────────────────────

    string? _markTheme;
    double _markLayout = -1;
    Brush? _washBrush, _washStartBrush, _nodeGlowBrush, _lineBrush, _nodeBrush, _nodeCoreBrush;
    Pen? _linePen;

    void EnsureMarkBrushes()
    {
        var t = ThemeManager.Current;
        if (_markTheme == t.Id && _markLayout == _layout) return;
        _markTheme = t.Id;
        _markLayout = _layout;
        Color accent = t.Mint, second = t.Teal, apex = t.LogoApex;
        // Faden in reiner Akzentfarbe – gemischt mit der Zweitfarbe wurde er im Halloween-Thema trüb rosa
        Color line = accent;

        // Schleier hinter der Antwort: links ein Hauch Akzent, nach rechts ausblendend
        _washBrush = Freeze(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x13, accent.R, accent.G, accent.B), 0),
            new(Color.FromArgb(0x07, second.R, second.G, second.B), 0.35),
            new(Color.FromArgb(0x00, second.R, second.G, second.B), 0.8),
        }, new Point(0, 0), new Point(1, 0)));
        _washStartBrush = Freeze(new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x1E, accent.R, accent.G, accent.B), 0),
            new(Color.FromArgb(0x0A, second.R, second.G, second.B), 0.35),
            new(Color.FromArgb(0x00, second.R, second.G, second.B), 0.8),
        }, new Point(0, 0), new Point(1, 0)));
        _nodeGlowBrush = Freeze(new RadialGradientBrush(Color.FromArgb(0x70, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)));
        _nodeBrush = Freeze(new RadialGradientBrush(MoodPalette.Mix(accent, apex, 0.35), accent) { GradientOrigin = new Point(0.35, 0.35) });
        _nodeCoreBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, apex.R, apex.G, apex.B)));
        _lineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x78, line.R, line.G, line.B)));
        _linePen = new Pen(_lineBrush, Math.Max(1 / _ppd, Snap(1.5))) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Round };
        _linePen.Freeze();
    }

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Hintergrund einer Antwortzeile: Schleier, Faden links unter dem Punkt, beim Start ein leuchtender Knoten.</summary>
    void DrawAnswerMark(DrawingContext dc, byte mark)
    {
        EnsureMarkBrushes();
        bool start = (mark & MarkStart) != 0, last = (mark & MarkLast) != 0;
        double width = Cols * _cw;
        double washLeft = -Snap(_pad.Left * 0.6);
        dc.DrawRectangle(start ? _washStartBrush : _washBrush, null, new Rect(washLeft, 0, width - washLeft, _lh));

        // Faden: senkrecht durch die Mitte von Spalte 0, vom Knoten bis zur letzten Zeile der Antwort
        double x = Snap(_cw / 2);
        double nodeR = Math.Max(2.5, _cw * 0.34);
        double y0 = start ? _lh / 2 + nodeR + 1.5 : 0;
        double y1 = last ? _lh / 2 : _lh;
        if (y1 > y0) dc.DrawLine(_linePen!, new Point(x, y0), new Point(x, y1));
        if (last && !start) dc.DrawEllipse(_lineBrush, null, new Point(x, _lh / 2), 1.6, 1.6);

        if (start)
        {
            var c = new Point(Snap(_cw / 2), Snap(_lh / 2));
            dc.DrawEllipse(_nodeGlowBrush, null, c, _lh * 0.75, _lh * 0.75);
            dc.DrawEllipse(_nodeBrush, null, c, nodeR, nodeR);
            dc.DrawEllipse(_nodeCoreBrush, null, c, nodeR * 0.38, nodeR * 0.38);
        }
    }

    // ───────────────────────────── Springen ─────────────────────────────

    /// <summary>Was die Pfeile gerade können: Antworten vorhanden, eine weiter oben, nach unten möglich.</summary>
    public (bool any, bool up, bool down) AnswerNav { get; private set; }
    public event Action? AnswerNavChanged;

    /// <summary>Berechnet <see cref="AnswerNav"/> neu und meldet Änderungen.</summary>
    public void RefreshAnswerNav()
    {
        var nav = (false, false, false);
        if (_emu != null)
        {
            lock (_emu.Sync)
            {
                if (!_emu.AltActive)
                {
                    double pos = _scrollAnimating ? _scrollTarget : ViewPosUnsafe();
                    long first = _emu.FirstAbs, end = _emu.ScreenTopAbs + _emu.Rows;
                    // die früheste Antwort genügt: liegt sie über der Ansicht, gibt es ein „nach oben“
                    for (long a = first; a < end; a++)
                    {
                        if (!IsAnswerStart(_emu.GetLineAbs(a))) continue;
                        nav = (true, Math.Max(first, a - 1) < pos - 0.5, _viewTop != null || _scrollAnimating && _scrollTarget < _emu.ScreenTopAbs);
                        break;
                    }
                }
            }
        }
        if (nav == AnswerNav) return;
        AnswerNav = nav;
        AnswerNavChanged?.Invoke();
    }

    /// <summary>Gleitet zur vorherigen (<paramref name="dir"/> &lt; 0) bzw. nächsten Antwort; nach der letzten ganz nach unten.</summary>
    /// <returns>false, wenn es in diese Richtung nichts mehr gibt.</returns>
    public bool JumpToAnswer(int dir)
    {
        Wake();
        if (_emu == null) return false;
        long found = -1;
        bool toBottom = false;
        lock (_emu.Sync)
        {
            if (_emu.AltActive) return false;
            double pos = _scrollAnimating ? _scrollTarget : ViewPosUnsafe();
            long first = _emu.FirstAbs, bottomTop = _emu.ScreenTopAbs, end = bottomTop + _emu.Rows;
            // Ziel: eine Zeile Luft über dem Punkt
            if (dir < 0)
            {
                for (long a = Math.Min(end - 1, (long)Math.Ceiling(pos) + 1); a >= first; a--)
                    if (Math.Max(first, a - 1) < pos - 0.5 && IsAnswerStart(_emu.GetLineAbs(a))) { found = a; break; }
                if (found < 0) return false;
            }
            else
            {
                if (_viewTop == null && !_scrollAnimating) return false;
                for (long a = (long)Math.Floor(pos) + 2; a < end; a++)
                    if (IsAnswerStart(_emu.GetLineAbs(a))) { found = a; break; }
                toBottom = found < 0 || found - 1 >= bottomTop;
            }
        }
        if (toBottom) ScrollToBottom();
        else ScrollTo(Math.Max(0, found - 1));
        if (found >= 0) StartFlash(found);
        // Ziel steht fest – die Pfeile sofort anpassen, nicht erst nach dem Gleiten
        RefreshAnswerNav();
        return true;
    }

    // ───────────────────────────── Aufleuchten nach dem Sprung ─────────────────────────────

    readonly DrawingVisual _flashVisual = new();
    long _flashAbs = -1;
    int _flashRows;
    double _flashT;
    bool _flashOn;
    const double FlashRise = 0.18, FlashDecay = 0.55, FlashEnd = 2.0;

    void StartFlash(long abs)
    {
        if (!FlashAnswers || _emu == null) return;
        int rows = 1;
        lock (_emu.Sync)
        {
            long end = _emu.ScreenTopAbs + _emu.Rows;
            long lastText = abs;
            for (long a = abs + 1; a < end && a < abs + 400; a++)
            {
                var l = _emu.GetLineAbs(a);
                if (l == null || BreaksBlock(l)) break;
                if (!IsBlankLine(l)) lastText = a;
            }
            rows = (int)(lastText - abs + 1);
        }
        _flashAbs = abs;
        _flashRows = rows;
        _flashT = 0;
        DrawFlash();
        PlaceFlash();
        _flashVisual.Opacity = 0;
        if (!_flashOn)
        {
            _flashOn = true;
            AnimationTicker.SubscribeFrame(OnFlash);
        }
    }

    void DrawFlash()
    {
        var t = ThemeManager.Current;
        Color accent = t.Mint, second = t.Teal, apex = t.LogoApex;
        double h = _flashRows * _lh, w = Math.Max(1, Cols * _cw);
        using var dc = _flashVisual.RenderOpen();
        var wash = new LinearGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(0x2E, accent.R, accent.G, accent.B), 0),
            new(Color.FromArgb(0x10, second.R, second.G, second.B), 0.45),
            new(Color.FromArgb(0x00, second.R, second.G, second.B), 0.9),
        }, new Point(0, 0), new Point(1, 0));
        wash.Freeze();
        double left = -Snap(_pad.Left * 0.6);
        dc.DrawRoundedRectangle(wash, null, new Rect(left, 0, w - left, h), 6, 6);
        double x = Snap(_cw / 2);
        var glow = new LinearGradientBrush(Color.FromArgb(0xD0, apex.R, apex.G, apex.B), Color.FromArgb(0x30, accent.R, accent.G, accent.B), 90);
        glow.Freeze();
        dc.DrawRectangle(glow, null, new Rect(x - 1, _lh / 2, 2, Math.Max(0, h - _lh)));
        var halo = new RadialGradientBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B));
        halo.Freeze();
        dc.DrawEllipse(halo, null, new Point(x, _lh / 2), _lh * 1.4, _lh * 1.4);
    }

    /// <summary>Folgt der Zeile beim Gleiten.</summary>
    void PlaceFlash()
    {
        if (_emu == null || _flashAbs < 0) return;
        double pos;
        lock (_emu.Sync) pos = ViewPosUnsafe();
        _flashVisual.Offset = new Vector(Snap(_pad.Left), Snap(_pad.Top + (_flashAbs - pos) * _lh));
    }

    void OnFlash(double dt)
    {
        _flashT += dt;
        double o = _flashT < FlashRise ? _flashT / FlashRise : Math.Exp(-(_flashT - FlashRise) / FlashDecay);
        if (_flashT >= FlashEnd || !IsVisible)
        {
            _flashOn = false;
            AnimationTicker.UnsubscribeFrame(OnFlash);
            _flashVisual.Opacity = 0;
            using (_flashVisual.RenderOpen()) { }
            _flashAbs = -1;
            return;
        }
        // weich ein- und ausblenden (Sinus-Rampe statt linear)
        o = Math.Sin(Math.Clamp(o, 0, 1) * Math.PI / 2);
        _flashVisual.Opacity = o;
        PlaceFlash();
    }
}
