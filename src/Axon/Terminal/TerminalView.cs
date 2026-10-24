using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Axon.Terminal;

/// <summary>
/// Zeichnet einen <see cref="TerminalEmulator"/> und nimmt Tastatur-/Maus-Eingaben entgegen.
/// Jede Bildschirmzeile ist ein eigenes DrawingVisual und wird nur bei Änderung neu gezeichnet.
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    sealed class RowVisual : DrawingVisual
    {
        public TerminalLine? Line;
        public int Version = -1;
        public int Layout = -1;
        public RowVisual()
        {
            VisualTextRenderingMode = TextRenderingMode.Grayscale;
            VisualTextHintingMode = TextHintingMode.Fixed;
            // Zeile als Bild zwischenspeichern: Animationen dahinter zwingen WPF sonst, den Text in jedem Bild neu zu rastern.
            VisualCacheMode = new BitmapCache { SnapsToDevicePixels = true };
        }
    }

    readonly VisualCollection _children;
    readonly List<RowVisual> _rows = new();
    readonly DrawingVisual _selectionVisual = new();
    readonly DrawingVisual _cursorVisual = new();
    readonly DrawingVisual _scrollbarVisual = new();
    readonly DrawingVisual _linkVisual = new();

    TerminalEmulator? _emu;
    public TerminalEmulator? Emulator => _emu;

    /// <summary>Schickt Eingaben an den Prozess.</summary>
    public Action<string>? Send;
    /// <summary>Neue Rastergröße (Spalten, Zeilen).</summary>
    public event Action<int, int>? GridSizeChanged;
    /// <summary>Benutzer hat getippt (für Statuswechsel).</summary>
    public event Action<string>? UserTyped;
    /// <summary>Kurze Rückmeldung, z. B. „Kopiert“.</summary>
    public event Action<string>? Notice;
    /// <summary>Bitte um „letzten Screenshot einfügen“ (Kontextmenü).</summary>
    public event Action? ScreenshotRequested;

    // Schrift und Metrik
    string _fontFamily = "Cascadia Mono";
    double _fontSize = 14;
    const double LineSpacing = 1.12;
    GlyphTypeface _gtRegular = null!, _gtBold = null!, _gtItalic = null!, _gtBoldItalic = null!;
    Typeface _fallbackTypeface = null!, _fallbackBold = null!;
    double _cw = 8, _lh = 16, _baseline = 12, _ppd = 1;
    double _underlineY, _underlineT, _strikeY;
    int _layout;
    readonly Thickness _pad = new(16, 12, 10, 12);

    public int Cols { get; private set; }
    public int RowsCount { get; private set; }
    public double CellWidth => _cw;
    public double LineHeight => _lh;

    // Ansicht
    long? _viewTop;
    double _scrollbarAlpha;
    DateTime _scrollbarShown = DateTime.MinValue;
    bool _draggingScrollbar;
    double _dragOffset;

    // Auswahl
    (long abs, int col)? _selA, _selB;
    bool _selecting;
    int _selMode;
    int _selVersion;
    int _selDrawn = -1;
    long _selDrawnTop = -1;
    int _selLayout = -1;

    // Cursor
    double _curX, _curY;
    bool _curInit;
    DateTime _lastFrame = DateTime.UtcNow;
    double _time;
    int _cursorStyleDrawn = -1;
    bool _cursorFocusDrawn;
    double _cursorWDrawn;
    DateTime _lastTyped = DateTime.MinValue;

    int _seenVersion = -1;
    long _seenTop = -1;
    bool _subscribed;
    DateTime _syncStart = DateTime.MinValue;

    Point _mouse;
    string? _hoverLink;

    readonly Dictionary<uint, SolidColorBrush> _brushes = new();
    readonly Dictionary<(int, uint, bool), FormattedText> _fallbackCache = new();

    static readonly Regex UrlRegex = new(@"https?://[^\s<>""'`\]\)\}│]+", RegexOptions.Compiled);

    public TerminalView()
    {
        _children = new VisualCollection(this) { _selectionVisual, _linkVisual, _cursorVisual, _scrollbarVisual };
        Focusable = true;
        FocusVisualStyle = null;
        AllowDrop = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        SnapsToDevicePixels = true;
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => { UpdateMetrics(); UpdateSubscription(); };
        Unloaded += (_, _) => UpdateSubscription(false);
        ContextMenu = BuildContextMenu();
        ContextMenuOpening += OnContextMenuOpening;
    }

    public void Attach(TerminalEmulator emu)
    {
        _emu = emu;
        _seenVersion = -1;
        _layout++;
        emu.ScrollbackCleared += () => Dispatcher.BeginInvoke(() => { _viewTop = null; ClearSelection(); });
    }

    public double FontSizeValue => _fontSize;

    public void SetFont(string family, double size)
    {
        Wake();
        _fontFamily = family;
        _fontSize = Math.Clamp(size, 8, 32);
        _fallbackCache.Clear();
        if (IsLoaded) { UpdateMetrics(); Relayout(); }
    }

    // ───────────────────────────── Visuals ─────────────────────────────

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    // Gezeichnet wird nur bei Bedarf: neue Ausgabe, Eingabe, Maus, Scrollen oder Cursorbewegung wecken die Ansicht,
    // nach ein paar ruhigen Bildern hängt sie sich wieder vom Bildtakt ab.
    bool _framesOn;
    int _idleFrames;
    int _wakeQueued;

    void UpdateSubscription(bool? force = null)
    {
        bool want = force ?? (IsVisible && IsLoaded);
        if (want == _subscribed) return;
        _subscribed = want;
        if (want) { _seenVersion = -1; _layout++; Wake(); }
        else
        {
            StopFrames();
            if (_breathSubscribed) { _breathSubscribed = false; Visuals.AnimationTicker.UnsubscribeFrame(OnBreath); }
        }
    }

    void Wake()
    {
        if (!_subscribed) return;
        _idleFrames = 0;
        if (_framesOn) return;
        _framesOn = true;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopFrames()
    {
        if (!_framesOn) return;
        _framesOn = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    /// <summary>Neue Ausgabe ist da – darf von jedem Thread aufgerufen werden.</summary>
    public void RequestRender()
    {
        if (System.Threading.Interlocked.Exchange(ref _wakeQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _wakeQueued = 0;
            Wake();
        }));
    }

    void UpdateMetrics()
    {
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _gtRegular = LoadGlyphs(_fontFamily, FontWeights.Normal, FontStyles.Normal) ?? LoadGlyphs("Consolas", FontWeights.Normal, FontStyles.Normal)!;
        _gtBold = LoadGlyphs(_fontFamily, FontWeights.Bold, FontStyles.Normal) ?? _gtRegular;
        _gtItalic = LoadGlyphs(_fontFamily, FontWeights.Normal, FontStyles.Italic) ?? _gtRegular;
        _gtBoldItalic = LoadGlyphs(_fontFamily, FontWeights.Bold, FontStyles.Italic) ?? _gtBold;
        var fallback = new FontFamily($"{_fontFamily}, Cascadia Code, Segoe UI Symbol, Segoe UI Emoji, Segoe UI, Consolas");
        _fallbackTypeface = new Typeface(fallback, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _fallbackBold = new Typeface(fallback, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        double adv = _gtRegular.CharacterToGlyphMap.TryGetValue('M', out ushort gm) ? _gtRegular.AdvanceWidths[gm] * _fontSize : _fontSize * 0.6;
        _cw = Snap(adv);
        double h = _gtRegular.Height * _fontSize;
        _lh = Math.Ceiling(h * LineSpacing * _ppd) / _ppd;
        _baseline = Snap((_lh - h) / 2 + _gtRegular.Baseline * _fontSize);
        _underlineT = Math.Max(1 / _ppd, Snap(_gtRegular.UnderlineThickness * _fontSize));
        _underlineY = Snap(_baseline - _gtRegular.UnderlinePosition * _fontSize);
        _strikeY = Snap(_baseline - _gtRegular.StrikethroughPosition * _fontSize);
        _layout++;
    }

    static GlyphTypeface? LoadGlyphs(string family, FontWeight weight, FontStyle style)
    {
        var tf = new Typeface(new FontFamily(family), style, weight, FontStretches.Normal);
        return tf.TryGetGlyphTypeface(out var gt) ? gt : null;
    }

    double Snap(double v) => Math.Round(v * _ppd) / _ppd;

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMetrics();
        Relayout();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Relayout();
    }

    void Relayout()
    {
        Wake();
        if (_gtRegular == null) UpdateMetrics();
        double w = ActualWidth - _pad.Left - _pad.Right;
        double h = ActualHeight - _pad.Top - _pad.Bottom;
        if (w <= 0 || h <= 0) return;
        int cols = Math.Max(20, (int)(w / _cw));
        int rows = Math.Max(4, (int)(h / _lh));

        while (_rows.Count < rows)
        {
            var rv = new RowVisual();
            _rows.Add(rv);
            _children.Insert(_rows.Count - 1, rv);
        }
        while (_rows.Count > rows)
        {
            var rv = _rows[^1];
            _rows.RemoveAt(_rows.Count - 1);
            _children.Remove(rv);
        }
        double top = Snap(_pad.Top);
        for (int i = 0; i < _rows.Count; i++) _rows[i].Offset = new Vector(Snap(_pad.Left), top + i * _lh);
        _layout++;

        if (cols != Cols || rows != RowsCount)
        {
            Cols = cols;
            RowsCount = rows;
            if (_emu != null) lock (_emu.Sync) _emu.Resize(cols, rows);
            GridSizeChanged?.Invoke(cols, rows);
        }
        _seenVersion = -1;
    }

    // ───────────────────────────── Frame ─────────────────────────────

    void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
        _lastFrame = now;
        _time += dt;
        if (_emu == null || _rows.Count == 0) return;

        bool sync;
        lock (_emu.Sync) sync = _emu.SyncOutput;
        if (sync)
        {
            if (_syncStart == DateTime.MinValue) _syncStart = now;
            if ((now - _syncStart).TotalMilliseconds < 200) { AnimateCursor(dt); _idleFrames = 0; return; }
        }
        else _syncStart = DateTime.MinValue;

        bool busy = UpdateRows();
        busy |= AnimateCursor(dt);
        busy |= AnimateScrollbar(dt);
        busy |= _selecting || _draggingScrollbar || (now - _lastTyped).TotalSeconds < 1.0 || (now - _scrollbarShown).TotalSeconds < 1.6;
        if (busy) _idleFrames = 0;
        else if (++_idleFrames > 4) StopFrames();
    }

    long ViewTopUnsafe()
    {
        var emu = _emu!;
        long bottomTop = emu.ScreenTopAbs;
        if (_viewTop == null) return bottomTop;
        long first = emu.FirstAbs;
        long v = Math.Clamp(_viewTop.Value, first, bottomTop);
        if (v >= bottomTop) { _viewTop = null; return bottomTop; }
        return v;
    }

    /// <returns>true, wenn sich etwas geändert hat.</returns>
    bool UpdateRows()
    {
        var emu = _emu!;
        lock (emu.Sync)
        {
            long top = ViewTopUnsafe();
            if (emu.Version == _seenVersion && top == _seenTop && _rows[0].Layout == _layout && _selDrawn == _selVersion && _selDrawnTop == top && _selLayout == _layout)
                return false;
            _seenVersion = emu.Version;
            _seenTop = top;
            if (Trace) _traceFrames++;
            int n = Math.Min(_rows.Count, emu.Rows + (int)(emu.ScreenTopAbs - top));
            for (int r = 0; r < _rows.Count; r++)
            {
                var rv = _rows[r];
                var line = r < n ? emu.GetLineAbs(top + r) : null;
                if (rv.Line == line && rv.Layout == _layout && (line == null || rv.Version == line.Version)) continue;
                if (Trace) TraceRow(r, rv, line);
                rv.Line = line;
                rv.Version = line?.Version ?? -1;
                rv.Layout = _layout;
                using var dc = rv.RenderOpen();
                if (line != null) DrawLine(dc, line, emu);
            }
            if (_selDrawn != _selVersion || _selDrawnTop != top || _selLayout != _layout)
                DrawSelection(top);
        }
        return true;
    }

    // Diagnose (AXON_PERF=trace): wie oft und warum Zeilen neu gezeichnet werden
    static readonly bool Trace = (Environment.GetEnvironmentVariable("AXON_PERF") ?? "").Contains("trace");
    int _traceRedraws, _traceRef, _traceVer, _traceLayout, _traceFrames;
    DateTime _traceStart = DateTime.UtcNow;

    void TraceRow(int r, RowVisual rv, TerminalLine? line)
    {
        _traceRedraws++;
        if (rv.Line != line) _traceRef++;
        else if (rv.Layout != _layout) _traceLayout++;
        else _traceVer++;
        if ((DateTime.UtcNow - _traceStart).TotalSeconds >= 3)
        {
            Services.AppPaths.Log($"trace: {_traceRedraws} Zeilen neu in 3 s (neue Zeile {_traceRef}, Version {_traceVer}, Layout {_traceLayout}), Frames mit Update {_traceFrames}, emu.Version {_emu?.Version}");
            _traceRedraws = _traceRef = _traceVer = _traceLayout = _traceFrames = 0;
            _traceStart = DateTime.UtcNow;
        }
    }

    Brush BrushFor(Color c)
    {
        uint key = (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
        if (!_brushes.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(c);
            b.Freeze();
            _brushes[key] = b;
        }
        return b;
    }

    (Color fg, Color? bg) Colors(CellAttr a)
    {
        Color fg = TerminalTheme.Resolve(a.Fg, TerminalTheme.Foreground);
        Color? bg = a.Bg == 0 ? null : TerminalTheme.Resolve(a.Bg, TerminalTheme.Background);
        // Reines Schwarz als Hintergrund (z. B. im Claude-Logo) würde auf dem grünen Glas als Loch wirken.
        if (bg is { R: 0, G: 0, B: 0 } && (a.Bg & CellAttr.RgbTag) != 0) bg = null;
        if ((a.Flags & CellFlags.Inverse) != 0)
        {
            var nf = bg ?? TerminalTheme.Background;
            bg = fg;
            fg = nf;
        }
        if ((a.Flags & CellFlags.Dim) != 0) fg = Color.FromArgb(150, fg.R, fg.G, fg.B);
        return (fg, bg);
    }

    void DrawLine(DrawingContext dc, TerminalLine line, TerminalEmulator emu)
    {
        var cells = line.Cells;
        int n = Math.Min(cells.Length, Cols);

        // Hintergründe zusammenhängend zeichnen
        int i = 0;
        while (i < n)
        {
            var (_, bg) = Colors(cells[i].Attr);
            int j = i + 1;
            while (j < n && Colors(cells[j].Attr).bg == bg) j++;
            if (bg != null) dc.DrawRectangle(BrushFor(bg.Value), null, new Rect(i * _cw, 0, (j - i) * _cw, _lh));
            i = j;
        }

        var indices = new List<ushort>(n);
        var advances = new List<double>(n);
        i = 0;
        while (i < n)
        {
            var c = cells[i];
            if (c.Kind == Cell.WideTail || c.Rune == 0 || c.Rune == ' ' || (c.Attr.Flags & CellFlags.Hidden) != 0)
            {
                DrawDecorations(dc, c.Attr, i, 1, emu);
                i++;
                continue;
            }
            var (fg, _) = Colors(c.Attr);
            var brush = BrushFor(fg);
            bool bold = (c.Attr.Flags & CellFlags.Bold) != 0;
            bool italic = (c.Attr.Flags & CellFlags.Italic) != 0;
            var gt = bold ? (italic ? _gtBoldItalic : _gtBold) : (italic ? _gtItalic : _gtRegular);

            if (BoxDrawing.Handles(c.Rune))
            {
                BoxDrawing.Draw(dc, c.Rune, i * _cw, 0, _cw, _lh, brush, _ppd);
                DrawDecorations(dc, c.Attr, i, 1, emu);
                i++;
                continue;
            }

            if (c.Kind == Cell.WideLead || !gt.CharacterToGlyphMap.ContainsKey(c.Rune))
            {
                DrawFallback(dc, c.Rune, i, c.Kind == Cell.WideLead ? 2 : 1, fg, bold);
                DrawDecorations(dc, c.Attr, i, c.Kind == Cell.WideLead ? 2 : 1, emu);
                i += c.Kind == Cell.WideLead ? 2 : 1;
                continue;
            }

            // Lauf gleicher Darstellung sammeln
            indices.Clear();
            advances.Clear();
            int start = i;
            while (i < n)
            {
                var d = cells[i];
                if (d.Kind != Cell.Normal || d.Rune == 0 || d.Rune == ' ' || BoxDrawing.Handles(d.Rune)) break;
                if (d.Attr != c.Attr && Colors(d.Attr).fg != fg) break;
                if ((d.Attr.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Hidden)) != (c.Attr.Flags & (CellFlags.Bold | CellFlags.Italic | CellFlags.Hidden))) break;
                if (!gt.CharacterToGlyphMap.TryGetValue(d.Rune, out ushort g)) break;
                indices.Add(g);
                advances.Add(_cw);
                i++;
            }
            if (indices.Count > 0)
            {
                var run = new GlyphRun(gt, 0, false, _fontSize, (float)_ppd, indices.ToArray(), new Point(start * _cw, _baseline),
                    advances.ToArray(), null, null, null, null, null, null);
                dc.DrawGlyphRun(brush, run);
            }
            for (int k = start; k < i; k++) DrawDecorations(dc, cells[k].Attr, k, 1, emu);
            if (i == start) i++;
        }
    }

    void DrawDecorations(DrawingContext dc, CellAttr a, int col, int width, TerminalEmulator emu)
    {
        var f = a.Flags;
        if ((f & (CellFlags.Underline | CellFlags.DoubleUnderline | CellFlags.CurlyUnderline | CellFlags.Strike | CellFlags.Overline)) == 0 && a.Link == 0) return;
        var (fg, _) = Colors(a);
        var b = BrushFor(fg);
        double x = col * _cw, w = width * _cw;
        if ((f & CellFlags.Underline) != 0 || a.Link != 0 && (f & (CellFlags.DoubleUnderline | CellFlags.CurlyUnderline)) == 0)
        {
            var ub = a.Link != 0 && (f & CellFlags.Underline) == 0 ? BrushFor(Color.FromArgb(110, fg.R, fg.G, fg.B)) : b;
            dc.DrawRectangle(ub, null, new Rect(x, _underlineY, w, _underlineT));
        }
        if ((f & CellFlags.DoubleUnderline) != 0)
        {
            dc.DrawRectangle(b, null, new Rect(x, _underlineY - _underlineT, w, _underlineT));
            dc.DrawRectangle(b, null, new Rect(x, _underlineY + _underlineT, w, _underlineT));
        }
        if ((f & CellFlags.CurlyUnderline) != 0)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                double amp = Math.Max(1, _underlineT * 1.2);
                ctx.BeginFigure(new Point(x, _underlineY), false, false);
                int steps = Math.Max(2, (int)(w / 2));
                for (int s = 1; s <= steps; s++)
                {
                    double px = x + w * s / steps;
                    ctx.LineTo(new Point(px, _underlineY + Math.Sin(px / _cw * Math.PI * 2) * amp), true, true);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(b, _underlineT), g);
        }
        if ((f & CellFlags.Strike) != 0) dc.DrawRectangle(b, null, new Rect(x, _strikeY, w, _underlineT));
        if ((f & CellFlags.Overline) != 0) dc.DrawRectangle(b, null, new Rect(x, 0, w, _underlineT));
    }

    void DrawFallback(DrawingContext dc, int rune, int col, int width, Color fg, bool bold)
    {
        uint key = (uint)(fg.A << 24 | fg.R << 16 | fg.G << 8 | fg.B);
        if (!_fallbackCache.TryGetValue((rune, key, bold), out var ft))
        {
            if (_fallbackCache.Count > 2048) _fallbackCache.Clear();
            ft = new FormattedText(char.ConvertFromUtf32(rune), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                bold ? _fallbackBold : _fallbackTypeface, _fontSize, BrushFor(fg), _ppd);
            _fallbackCache[(rune, key, bold)] = ft;
        }
        double cellW = width * _cw;
        double x = col * _cw + Math.Max(0, (cellW - ft.Width) / 2);
        double y = _baseline - ft.Baseline;
        dc.PushClip(new RectangleGeometry(new Rect(col * _cw, 0, cellW, _lh)));
        dc.DrawText(ft, new Point(Snap(x), Snap(y)));
        dc.Pop();
    }

    // ───────────────────────────── Cursor ─────────────────────────────

    /// <returns>true, solange der Cursor noch gleitet.</returns>
    bool AnimateCursor(double dt)
    {
        var emu = _emu!;
        int cx, cy, style;
        bool visible;
        lock (emu.Sync)
        {
            cx = emu.CursorX;
            cy = emu.CursorY;
            visible = emu.CursorVisible && _viewTop == null;
            style = emu.CursorStyle;
        }
        _cursorShown = visible && cy < _rows.Count;
        if (!_cursorShown)
        {
            SetCursorOpacity(0);
            return false;
        }
        bool focus = IsKeyboardFocused;
        double w = _cw;
        if (style != _cursorStyleDrawn || focus != _cursorFocusDrawn || Math.Abs(w - _cursorWDrawn) > 0.01 || _rows[0].Layout != _layout)
        {
            _cursorStyleDrawn = style;
            _cursorFocusDrawn = focus;
            _cursorWDrawn = w;
            using var dc = _cursorVisual.RenderOpen();
            var c = TerminalTheme.Cursor;
            var glow = new RadialGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            glow.Freeze();
            dc.DrawEllipse(glow, null, new Point(w / 2, _lh / 2), w * 2.2, _lh * 1.1);
            var solid = BrushFor(focus ? c : Color.FromArgb(150, c.R, c.G, c.B));
            double t = Math.Max(2 / _ppd, Snap(_cw * 0.18));
            if (!focus) dc.DrawRectangle(null, new Pen(solid, 1 / _ppd), new Rect(0.5 / _ppd, 0.5 / _ppd, w - 1 / _ppd, _lh - 1 / _ppd));
            else if (style is 3 or 4) dc.DrawRectangle(solid, null, new Rect(0, _lh - t, w, t));
            else if (style is 1 or 2) dc.DrawRectangle(BrushFor(Color.FromArgb(150, c.R, c.G, c.B)), null, new Rect(0, 0, w, _lh));
            else dc.DrawRectangle(solid, null, new Rect(0, Snap(_lh * 0.12), t, Snap(_lh * 0.76)));
        }
        double tx = Snap(_pad.Left) + cx * _cw, ty = Snap(_pad.Top) + cy * _lh;
        if (!_curInit || Math.Abs(tx - _curX) > _cw * 30 || Math.Abs(ty - _curY) > _lh * 6) { _curX = tx; _curY = ty; _curInit = true; }
        double k = 1 - Math.Exp(-dt / 0.045);
        _curX += (tx - _curX) * k;
        _curY += (ty - _curY) * k;
        if (Math.Abs(tx - _curX) < 0.2) _curX = tx;
        if (Math.Abs(ty - _curY) < 0.2) _curY = ty;
        var off = new Vector(_curX, _curY);
        if (off != _cursorVisual.Offset) _cursorVisual.Offset = off;

        // Atmen läuft im gemeinsamen Animationstakt (OnBreath); hier nur die festen Zustände.
        double sinceType = (DateTime.UtcNow - _lastTyped).TotalSeconds;
        if (!focus) SetCursorOpacity(0.7);
        else if (sinceType < 0.8 || !CursorBreathing) SetCursorOpacity(1);
        _cursorFocused = focus;
        bool wantBreath = focus && CursorBreathing && sinceType >= 0.8;
        if (wantBreath != _breathSubscribed)
        {
            _breathSubscribed = wantBreath;
            if (wantBreath) { _breathT = 0; Visuals.AnimationTicker.SubscribeFrame(OnBreath); }
            else Visuals.AnimationTicker.UnsubscribeFrame(OnBreath);
        }
        return _curX != tx || _curY != ty;
    }

    /// <summary>Sanftes Atmen statt hartem Blinken. Aus, wenn Animationen abgeschaltet sind.</summary>
    public static bool CursorBreathing { get; set; } = true;
    bool _breathSubscribed, _cursorFocused, _cursorShown;
    double _breathT;

    void OnBreath(double dt)
    {
        _breathT += dt;
        if (!_cursorFocused || !_cursorShown) return;
        SetCursorOpacity(0.62 + 0.38 * Math.Cos(_breathT * Math.PI * 2 / 1.6));
    }

    void SetCursorOpacity(double o)
    {
        if (Math.Abs(_cursorVisual.Opacity - o) > 0.001) _cursorVisual.Opacity = o;
    }

    // ───────────────────────────── Scrollen ─────────────────────────────

    public void ScrollBy(long lines)
    {
        Wake();
        if (_emu == null) return;
        lock (_emu.Sync)
        {
            long top = ViewTopUnsafe() + lines;
            long bottomTop = _emu.ScreenTopAbs;
            top = Math.Clamp(top, _emu.FirstAbs, bottomTop);
            _viewTop = top >= bottomTop ? null : top;
        }
        _scrollbarShown = DateTime.UtcNow;
    }

    public void ScrollToBottom() { _viewTop = null; Wake(); }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Wake();
        base.OnMouseWheel(e);
        if (_emu == null) return;
        int notches = e.Delta / 120;
        if (notches == 0) notches = Math.Sign(e.Delta);
        bool mouseMode;
        lock (_emu.Sync) mouseMode = _emu.MouseMode != 0 && _emu.MouseSgr;
        if (mouseMode && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            var (_, col, row) = CellAt(e.GetPosition(this));
            for (int i = 0; i < Math.Abs(notches); i++)
                Send?.Invoke($"\x1b[<{(notches > 0 ? 64 : 65)};{col + 1};{row + 1}M");
        }
        else ScrollBy(-notches * 3);
        e.Handled = true;
    }

    Rect _thumbDrawn;

    /// <returns>true, solange die Scrollleiste ein- oder ausblendet.</returns>
    bool AnimateScrollbar(double dt)
    {
        var emu = _emu!;
        long first, bottomTop, top;
        lock (emu.Sync)
        {
            first = emu.FirstAbs;
            bottomTop = emu.ScreenTopAbs;
            top = ViewTopUnsafe();
        }
        long hidden = bottomTop - first;
        bool hover = IsMouseOver && _mouse.X > ActualWidth - 16;
        double target = hidden <= 0 ? 0 : (_draggingScrollbar || hover || (DateTime.UtcNow - _scrollbarShown).TotalSeconds < 1.2 || _viewTop != null) ? 1 : 0;
        double k = 1 - Math.Exp(-dt / 0.18);
        _scrollbarAlpha += (target - _scrollbarAlpha) * k;
        if (Math.Abs(target - _scrollbarAlpha) < 0.01) _scrollbarAlpha = target;
        if (_scrollbarAlpha <= 0)
        {
            if (_scrollbarVisual.Opacity != 0) _scrollbarVisual.Opacity = 0;
            return false;
        }
        if (_scrollbarVisual.Opacity != _scrollbarAlpha) _scrollbarVisual.Opacity = _scrollbarAlpha;
        var (y, h) = ThumbRect(first, bottomTop, top);
        double w = hover || _draggingScrollbar ? 6 : 4;
        var rect = new Rect(ActualWidth - w - 4, y, w, h);
        if (rect != _thumbDrawn)
        {
            _thumbDrawn = rect;
            using var dc = _scrollbarVisual.RenderOpen();
            var c = TerminalTheme.Cursor;
            dc.DrawRoundedRectangle(BrushFor(Color.FromArgb((byte)(hover || _draggingScrollbar ? 150 : 90), c.R, c.G, c.B)), null, rect, w / 2, w / 2);
        }
        return _scrollbarAlpha != target;
    }

    (double y, double h) ThumbRect(long first, long bottomTop, long top)
    {
        double trackTop = 6, trackH = ActualHeight - 12;
        long total = bottomTop - first + RowsCount;
        double h = Math.Max(28, trackH * RowsCount / Math.Max(1, total));
        double frac = bottomTop - first <= 0 ? 1 : (double)(top - first) / (bottomTop - first);
        return (trackTop + (trackH - h) * frac, h);
    }

    // ───────────────────────────── Auswahl ─────────────────────────────

    (long abs, int col, int row) CellAt(Point p)
    {
        int col = (int)Math.Floor((p.X - _pad.Left) / _cw);
        int row = (int)Math.Floor((p.Y - _pad.Top) / _lh);
        col = Math.Clamp(col, 0, Math.Max(0, Cols - 1));
        int rowClamped = Math.Clamp(row, 0, Math.Max(0, RowsCount - 1));
        long top;
        lock (_emu!.Sync) top = ViewTopUnsafe();
        return (top + rowClamped, col, rowClamped);
    }

    public bool HasSelection => _selA != null && _selB != null && _selA != _selB;

    void ClearSelection()
    {
        Wake();
        if (_selA == null) return;
        _selA = _selB = null;
        _selVersion++;
    }

    ((long abs, int col) s, (long abs, int col) e) OrderedSelection()
    {
        var a = _selA!.Value;
        var b = _selB!.Value;
        return (a.abs < b.abs || a.abs == b.abs && a.col <= b.col) ? (a, b) : (b, a);
    }

    void DrawSelection(long top)
    {
        _selDrawn = _selVersion;
        _selDrawnTop = top;
        _selLayout = _layout;
        using var dc = _selectionVisual.RenderOpen();
        if (!HasSelection) return;
        var (s, e) = OrderedSelection();
        var brush = BrushFor(TerminalTheme.Selection);
        double left = Snap(_pad.Left), y0 = Snap(_pad.Top);
        for (int r = 0; r < RowsCount; r++)
        {
            long abs = top + r;
            if (abs < s.abs || abs > e.abs) continue;
            int c0 = abs == s.abs ? s.col : 0;
            int c1 = abs == e.abs ? e.col : Cols;
            if (c1 <= c0) continue;
            dc.DrawRoundedRectangle(brush, null, new Rect(left + c0 * _cw, y0 + r * _lh, (c1 - c0) * _cw, _lh), 2, 2);
        }
    }

    public string SelectedText()
    {
        if (!HasSelection || _emu == null) return "";
        var (s, e) = OrderedSelection();
        lock (_emu.Sync) return _emu.GetText(s.abs, s.col, e.abs, e.col);
    }

    public void Copy()
    {
        string t = SelectedText();
        if (t.Length == 0) return;
        try { Clipboard.SetText(t); Notice?.Invoke("Kopiert"); }
        catch { Notice?.Invoke("Zwischenablage ist gerade belegt"); }
        ClearSelection();
    }

    public void SelectAll()
    {
        Wake();
        if (_emu == null) return;
        lock (_emu.Sync)
        {
            _selA = (_emu.FirstAbs, 0);
            _selB = (_emu.ScreenTopAbs + _emu.Rows - 1, _emu.Cols);
        }
        _selVersion++;
    }

    (int from, int to) WordAt(long abs, int col)
    {
        lock (_emu!.Sync)
        {
            var line = _emu.GetLineAbs(abs);
            if (line == null) return (col, col);
            bool IsWord(int x)
            {
                if (x < 0 || x >= line.Cells.Length) return false;
                int r = line.Cells[x].Rune;
                if (r == 0 || r == ' ' || r > 0xFFFF) return false;
                char ch = (char)r;
                return char.IsLetterOrDigit(ch) || "_-./\\:~@%+#=?&".IndexOf(ch) >= 0;
            }
            if (!IsWord(col)) return (col, col + 1);
            int a = col, b = col;
            while (IsWord(a - 1)) a--;
            while (IsWord(b + 1)) b++;
            return (a, b + 1);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Wake();
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_emu == null) return;
        var p = e.GetPosition(this);

        if (p.X > ActualWidth - 16 && _scrollbarAlpha > 0.05)
        {
            long first, bottomTop, top;
            lock (_emu.Sync) { first = _emu.FirstAbs; bottomTop = _emu.ScreenTopAbs; top = ViewTopUnsafe(); }
            var (ty, th) = ThumbRect(first, bottomTop, top);
            _draggingScrollbar = true;
            _dragOffset = p.Y >= ty && p.Y <= ty + th ? p.Y - ty : th / 2;
            DragScrollbar(p.Y);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var link = LinkAt(p);
            if (link != null) { OpenLink(link); e.Handled = true; return; }
        }

        bool mouseMode;
        lock (_emu.Sync) mouseMode = _emu.MouseMode != 0 && _emu.MouseSgr;
        if (mouseMode && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            var (_, col, row) = CellAt(p);
            Send?.Invoke($"\x1b[<0;{col + 1};{row + 1}M");
            e.Handled = true;
            return;
        }

        var (abs, c, _) = CellAt(p);
        if (e.ClickCount == 2)
        {
            var (a, b) = WordAt(abs, c);
            _selA = (abs, a); _selB = (abs, b);
            _selMode = 1;
        }
        else if (e.ClickCount >= 3)
        {
            _selA = (abs, 0); _selB = (abs, Cols);
            _selMode = 2;
        }
        else
        {
            int col = (int)Math.Round((p.X - _pad.Left) / _cw);
            col = Math.Clamp(col, 0, Cols);
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _selA != null) _selB = (abs, col);
            else { _selA = (abs, col); _selB = (abs, col); }
            _selMode = 0;
        }
        _selecting = true;
        _selVersion++;
        CaptureMouse();
        e.Handled = true;
    }

    void DragScrollbar(double y)
    {
        long first, bottomTop;
        lock (_emu!.Sync) { first = _emu.FirstAbs; bottomTop = _emu.ScreenTopAbs; }
        var (_, th) = ThumbRect(first, bottomTop, first);
        double trackTop = 6, trackH = ActualHeight - 12;
        double frac = Math.Clamp((y - _dragOffset - trackTop) / Math.Max(1, trackH - th), 0, 1);
        long top = first + (long)Math.Round(frac * (bottomTop - first));
        _viewTop = top >= bottomTop ? null : top;
        _scrollbarShown = DateTime.UtcNow;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Wake();
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        if (_emu == null) return;
        if (_draggingScrollbar) { DragScrollbar(_mouse.Y); return; }

        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_mouse.Y < 0) ScrollBy(-1);
            else if (_mouse.Y > ActualHeight) ScrollBy(1);
            var (abs, c, _) = CellAt(_mouse);
            if (_selMode == 1)
            {
                var (a, b) = WordAt(abs, c);
                var anchor = _selA!.Value;
                _selB = abs > anchor.abs || abs == anchor.abs && c >= anchor.col ? (abs, b) : (abs, a);
            }
            else if (_selMode == 2) _selB = (abs, Cols);
            else
            {
                int col = (int)Math.Round((_mouse.X - _pad.Left) / _cw);
                _selB = (abs, Math.Clamp(col, 0, Cols));
            }
            _selVersion++;
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        string? link = ctrl ? LinkAt(_mouse) : null;
        if (link != _hoverLink)
        {
            _hoverLink = link;
            Cursor = link != null ? Cursors.Hand : Cursors.IBeam;
            ToolTip = link != null ? "Strg+Klick öffnet " + link : null;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        Wake();
        base.OnMouseLeftButtonUp(e);
        if (_draggingScrollbar) { _draggingScrollbar = false; ReleaseMouseCapture(); return; }
        if (_emu != null)
        {
            bool mouseMode;
            lock (_emu.Sync) mouseMode = _emu.MouseMode != 0 && _emu.MouseSgr;
            if (mouseMode && !_selecting)
            {
                var (_, col, row) = CellAt(e.GetPosition(this));
                Send?.Invoke($"\x1b[<0;{col + 1};{row + 1}m");
            }
        }
        if (_selecting)
        {
            _selecting = false;
            ReleaseMouseCapture();
            if (!HasSelection) ClearSelection();
        }
    }

    // ───────────────────────────── Links ─────────────────────────────

    string? LinkAt(Point p)
    {
        if (_emu == null) return null;
        var (abs, col, _) = CellAt(p);
        lock (_emu.Sync)
        {
            var line = _emu.GetLineAbs(abs);
            if (line == null || col >= line.Cells.Length) return null;
            if (line.Cells[col].Attr.Link != 0) return _emu.GetLink(line.Cells[col].Attr.Link);
            var chars = new char[line.Cells.Length];
            for (int i = 0; i < chars.Length; i++)
            {
                int r = line.Cells[i].Rune;
                chars[i] = r == 0 ? ' ' : r < 0x10000 ? (char)r : '?';
            }
            var text = new string(chars);
            foreach (Match m in UrlRegex.Matches(text))
                if (col >= m.Index && col < m.Index + m.Length) return m.Value.TrimEnd('.', ',', ';', ':');
        }
        return null;
    }

    static void OpenLink(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    // ───────────────────────────── Tastatur ─────────────────────────────

    void SendUser(string s)
    {
        Wake();
        if (string.IsNullOrEmpty(s)) return;
        _viewTop = null;
        _lastTyped = DateTime.UtcNow;
        if (HasSelection && !_selecting) ClearSelection();
        Send?.Invoke(s);
        UserTyped?.Invoke(s);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || _emu == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.CapsLock or Key.NumLock) return;
        if (HandleKey(key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>Verarbeitet eine Taste. Öffentlich, damit der Testmodus Tasten ohne echte Tastatur einspeisen kann.</summary>
    public bool HandleKey(Key key, ModifierKeys mods)
    {
        if (_emu == null) return false;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;

        if (ctrl && !alt && key == Key.C && (shift || HasSelection)) { Copy(); return true; }
        if (ctrl && !alt && key == Key.V || shift && !ctrl && key == Key.Insert) { Paste(); return true; }
        if (shift && !ctrl && !alt && key is Key.PageUp or Key.PageDown)
        {
            ScrollBy(key == Key.PageUp ? -(RowsCount - 1) : RowsCount - 1);
            return true;
        }
        if (ctrl && shift && key is Key.Home or Key.End)
        {
            if (key == Key.End) ScrollToBottom();
            else lock (_emu.Sync) _viewTop = _emu.FirstAbs;
            return true;
        }
        // Shift+Enter = Zeilenumbruch in Claude Code (wie /terminal-setup in VS Code: „\“ + Enter)
        if (shift && !ctrl && !alt && key == Key.Enter) { SendUser("\\\r"); return true; }

        bool appCursor, win32;
        lock (_emu.Sync) { appCursor = _emu.AppCursorKeys; win32 = _emu.Win32InputMode; }
        var seq = KeyEncoder.Encode(key, mods, appCursor, win32);
        if (string.IsNullOrEmpty(seq)) return false;
        SendUser(seq);
        return true;
    }

    /// <summary>Text wie getippt senden (Testmodus).</summary>
    public void TypeText(string text) => SendUser(text);

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled) return;
        string t = e.Text;
        if (string.IsNullOrEmpty(t)) return;
        // Steuerzeichen (Enter, Tab, Strg+X …) wurden bereits in KeyDown verschickt.
        if (t.All(ch => ch < 0x20 || ch == 0x7F)) return;
        SendUser(t);
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Wake();
        base.OnGotKeyboardFocus(e);
        if (_emu != null && _emu.FocusEvents) Send?.Invoke("\x1b[I");
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        Wake();
        base.OnLostKeyboardFocus(e);
        if (_emu != null && _emu.FocusEvents) Send?.Invoke("\x1b[O");
    }

    public void PasteText(string text)
    {
        if (_emu == null || string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (_emu.Sync) bracketed = _emu.BracketedPaste;
        if (bracketed) text = "\x1b[200~" + text.Replace("\x1b[201~", "") + "\x1b[201~";
        SendUser(text);
    }

    public void Paste()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                PasteText(Clipboard.GetText());
                return;
            }
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>();
                PasteText(string.Join(" ", files.Select(QuotePath)));
                return;
            }
            if (Clipboard.ContainsImage() && _emu != null)
            {
                bool win32;
                lock (_emu.Sync) win32 = _emu.Win32InputMode;
                SendUser(KeyEncoder.AltV(win32));
                Notice?.Invoke("Bild aus der Zwischenablage an Claude übergeben");
            }
        }
        catch
        {
            Notice?.Invoke("Zwischenablage ist gerade belegt");
        }
    }

    public static string QuotePath(string p) => p.Contains(' ') ? "\"" + p + "\"" : p;

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            PasteText(string.Join(" ", files.Select(QuotePath)));
        else if (e.Data.GetData(DataFormats.UnicodeText) is string s)
            PasteText(s);
        Focus();
        e.Handled = true;
    }

    // ───────────────────────────── Kontextmenü ─────────────────────────────

    MenuItem _miCopy = null!, _miPaste = null!, _miLink = null!;
    string? _contextLink;

    ContextMenu BuildContextMenu()
    {
        var m = new ContextMenu();
        _miCopy = new MenuItem { Header = "Kopieren", InputGestureText = "Strg+C" };
        _miCopy.Click += (_, _) => Copy();
        _miPaste = new MenuItem { Header = "Einfügen", InputGestureText = "Strg+V" };
        _miPaste.Click += (_, _) => Paste();
        var all = new MenuItem { Header = "Alles markieren" };
        all.Click += (_, _) => SelectAll();
        var shot = new MenuItem { Header = "Letzten Screenshot einfügen", InputGestureText = "Strg+Umschalt+S" };
        shot.Click += (_, _) => ScreenshotRequested?.Invoke();
        _miLink = new MenuItem { Header = "Link öffnen" };
        _miLink.Click += (_, _) => { if (_contextLink != null) OpenLink(_contextLink); };
        m.Items.Add(_miCopy);
        m.Items.Add(_miPaste);
        m.Items.Add(all);
        m.Items.Add(new Separator());
        m.Items.Add(shot);
        m.Items.Add(_miLink);
        return m;
    }

    void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _miCopy.IsEnabled = HasSelection;
        _contextLink = LinkAt(Mouse.GetPosition(this));
        _miLink.Visibility = _contextLink != null ? Visibility.Visible : Visibility.Collapsed;
    }
}
