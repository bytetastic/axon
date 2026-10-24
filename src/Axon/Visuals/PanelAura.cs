using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Licht am Rand der Terminalfläche: ruhiger Schimmer im Leerlauf, ein wandernder Lichtimpuls, solange Claude arbeitet,
/// bernsteinfarbenes Atmen bei Freigaben und ein einmaliger Umlauf, wenn Claude fertig ist.
/// Liegt deckungsgleich über dem Panel und zeichnet nur auf dessen Rand.
/// </summary>
public sealed class PanelAura : FrameworkElement
{
    // Lichthof und Randlinie: zwischengespeichert, nur bei Farb- oder Größenänderung neu; Atmen über die Deckkraft.
    // Lichtschweif: jedes Bild.
    readonly CachedVisual _halo = new();
    readonly DrawingVisual _visual = new();
    Point[] _perimeter = Array.Empty<Point>();
    Size _size;
    double _t, _sweep = -1, _cometPhase, _intensity;
    bool _subscribed;
    Color _primary, _secondary, _highlight;
    Color _haloColor;
    Size _haloSize;

    /// <summary>Deckkraft, mit der der Lichthof gezeichnet wird; die tatsächliche Helligkeit regelt die Opacity.</summary>
    const double HaloAlpha = 0.6;

    public double CornerRadius { get; set; } = 14;

    public PanelAura()
    {
        AddVisualChild(_halo);
        AddVisualChild(_visual);
        IsHitTestVisible = false;
        ClipToBounds = false;
        (_primary, _secondary, _highlight) = MoodPalette.For(Mood.Calm);
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => { if (_subscribed) { AnimationTicker.UnsubscribeFrame(OnTick); _subscribed = false; } };
        IsVisibleChanged += (_, _) => Update();
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _halo : _visual;

    Mood _mood = Mood.Calm;
    public Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            if (value == Mood.Done) _sweep = 0;
            _mood = value;
            if (Level == AnimationLevel.Off) DrawStatic();
        }
    }

    AnimationLevel _level = AnimationLevel.Full;
    public AnimationLevel Level
    {
        get => _level;
        set { _level = value; Update(); if (value == AnimationLevel.Off) DrawStatic(); }
    }

    void Update()
    {
        bool want = IsLoaded && IsVisible && Level != AnimationLevel.Off;
        if (want == _subscribed) return;
        _subscribed = want;
        if (want) AnimationTicker.SubscribeFrame(OnTick);
        else AnimationTicker.UnsubscribeFrame(OnTick);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _size = sizeInfo.NewSize;
        BuildPerimeter();
        if (Level == AnimationLevel.Off) DrawStatic();
    }

    /// <summary>Rand des abgerundeten Rechtecks als gleichmäßig verteilte Punkte (im Uhrzeigersinn ab oben links).</summary>
    void BuildPerimeter()
    {
        double w = _size.Width, h = _size.Height, r = Math.Min(CornerRadius, Math.Min(w, h) / 2);
        if (w <= 0 || h <= 0) { _perimeter = Array.Empty<Point>(); return; }
        var dense = new List<Point>();
        void Line(Point a, Point b)
        {
            int n = Math.Max(1, (int)((b - a).Length / 2));
            for (int i = 0; i < n; i++) dense.Add(a + (b - a) * ((double)i / n));
        }
        void Arc(Point c, double a0)
        {
            for (int i = 0; i < 12; i++)
            {
                double a = a0 + Math.PI / 2 * i / 12;
                dense.Add(new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r));
            }
        }
        Line(new Point(r, 0), new Point(w - r, 0));
        Arc(new Point(w - r, r), -Math.PI / 2);
        Line(new Point(w, r), new Point(w, h - r));
        Arc(new Point(w - r, h - r), 0);
        Line(new Point(w - r, h), new Point(r, h));
        Arc(new Point(r, h - r), Math.PI / 2);
        Line(new Point(0, h - r), new Point(0, r));
        Arc(new Point(r, r), Math.PI);

        // gleichmäßig neu abtasten
        var cum = new double[dense.Count + 1];
        for (int i = 1; i <= dense.Count; i++) cum[i] = cum[i - 1] + (dense[i % dense.Count] - dense[i - 1]).Length;
        double total = cum[^1];
        // ein Stützpunkt etwa alle 4 px – der Kopf wird zusätzlich stufenlos dazwischen gesetzt
        int count = Math.Max(240, (int)(total / 4));
        var res = new Point[count];
        int k = 0;
        for (int i = 0; i < count; i++)
        {
            double s = total * i / count;
            while (k < dense.Count - 1 && cum[k + 1] < s) k++;
            double seg = cum[k + 1] - cum[k];
            double t = seg > 0 ? (s - cum[k]) / seg : 0;
            var a = dense[k];
            var b = dense[(k + 1) % dense.Count];
            res[i] = new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }
        _perimeter = res;
    }

    void OnTick(double dt)
    {
        _t += dt;

        var (tp, ts, th) = MoodPalette.For(Mood);
        double kc = 1 - Math.Exp(-dt / 0.6);
        _primary = MoodPalette.Approach(_primary, tp, kc);
        _secondary = MoodPalette.Approach(_secondary, ts, kc);
        _highlight = MoodPalette.Approach(_highlight, th, kc);

        double targetIntensity = Mood == Mood.Working ? 1 : 0;
        _intensity += (targetIntensity - _intensity) * (1 - Math.Exp(-dt / 0.8));
        double speed = 0.055 + 0.075 * _intensity; // Umläufe pro Sekunde – bewusst langsam
        _cometPhase = (_cometPhase + dt * speed) % 1;
        if (_sweep >= 0) { _sweep += dt / 1.6; if (_sweep > 1.4) _sweep = -1; }

        Draw();
    }

    void DrawStatic()
    {
        ValidateCache();
        using (_visual.RenderOpen()) { }
        DrawHalo(0.16);
    }

    // Stifte bleiben zwischen Bildern erhalten; neu nur bei Farbwechsel.
    readonly Dictionary<int, Pen> _pens = new();
    readonly Dictionary<int, Brush> _heads = new();
    readonly List<Point>[] _buckets = { new(), new(), new(), new(), new(), new(), new(), new(), new(), new() };
    (Color, Color, Color) _cacheColors;

    void ValidateCache()
    {
        var now = (_primary, _secondary, _highlight);
        if (now == _cacheColors) return;
        _cacheColors = now;
        _pens.Clear();
        _heads.Clear();
    }

    Color ColorOf(int key) => key switch { 1 => _secondary, 2 => _highlight, _ => _primary };

    /// <summary>colorKey: 0 primär, 1 sekundär, 2 Highlight · widthKey: 0 = 1 px, 1 = 1,4 px, 2 = 6 px, 3.. = Halo</summary>
    Pen PenFor(int colorKey, double alpha, int widthKey)
    {
        int a = (int)Math.Clamp(alpha * 60, 0, 60);
        int key = colorKey * 10000 + widthKey * 100 + a;
        if (!_pens.TryGetValue(key, out var p))
        {
            var b = new SolidColorBrush(MoodPalette.WithAlpha(ColorOf(colorKey), a / 60.0));
            b.Freeze();
            double w = widthKey switch { 0 => 1, 1 => 1.4, 2 => 6, _ => (widthKey - 2) * 7 };
            // flache Enden: runde Kappen überlappen an den Stufenübergängen und erzeugen leuchtende „Perlen“
            p = new Pen(b, w) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
            p.Freeze();
            _pens[key] = p;
        }
        return p;
    }

    Brush HeadFor(int colorKey, double alpha)
    {
        int a = (int)Math.Clamp(alpha * 24, 0, 24);
        int key = colorKey * 100 + a;
        if (!_heads.TryGetValue(key, out var b))
        {
            var c = ColorOf(colorKey);
            b = new RadialGradientBrush(MoodPalette.WithAlpha(c, a / 24.0 * 0.9), MoodPalette.WithAlpha(c, 0));
            b.Freeze();
            _heads[key] = b;
        }
        return b;
    }

    /// <summary>Lichthof + Randlinie. Neu gezeichnet wird nur bei anderer Farbe oder Größe; die Helligkeit kommt über Opacity.</summary>
    void DrawHalo(double alpha)
    {
        if (_size.Width <= 0) return;
        var color = Mood == Mood.Attention ? _secondary : _primary;
        if (color != _haloColor || _size != _haloSize)
        {
            _haloColor = color;
            _haloSize = _size;
            var rect = new Rect(0, 0, _size.Width, _size.Height);
            int c = Mood == Mood.Attention ? 1 : 0;
            using var dc = _halo.RenderOpen();
            for (int i = 3; i >= 1; i--)
                dc.DrawRoundedRectangle(null, PenFor(c, HaloAlpha * 0.16 / i, 2 + i), rect, CornerRadius, CornerRadius);
            dc.DrawRoundedRectangle(null, PenFor(c, HaloAlpha, 0), rect, CornerRadius, CornerRadius);
        }
        double o = Math.Clamp(alpha / HaloAlpha, 0, 1);
        if (Math.Abs(_halo.Opacity - o) > 0.002) _halo.Opacity = o;
    }

    void Draw()
    {
        ValidateCache();
        double baseAlpha = 0.14 + 0.08 * _intensity;
        if (Mood == Mood.Attention) baseAlpha = 0.22 + 0.32 * (0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 2.6));
        if (Mood == Mood.Error) baseAlpha = 0.25;
        DrawHalo(baseAlpha);

        using var dc = _visual.RenderOpen();
        if (_perimeter.Length == 0) return;

        // ruhiger Komet im Leerlauf, zwei hellere während der Arbeit
        double cometAlpha = 0.22 + 0.68 * _intensity;
        if (Mood == Mood.Attention) cometAlpha *= 0.35;
        DrawComet(dc, _cometPhase, 0.16 + 0.08 * _intensity, cometAlpha, 2);
        if (_intensity > 0.02) DrawComet(dc, (_cometPhase + 0.5) % 1, 0.12, 0.55 * _intensity, 1);

        if (_sweep >= 0 && _sweep <= 1.2)
        {
            double fade = _sweep > 1 ? 1 - (_sweep - 1) / 0.2 : 1;
            DrawComet(dc, Math.Min(_sweep, 1), Math.Min(0.5, _sweep * 0.5 + 0.05), 0.9 * fade, 2);
        }
    }

    /// <summary>Lichtschweif entlang des Rands. Der Schweif wird in zehn Helligkeitsstufen zerlegt –
    /// pro Stufe ein zusammenhängender Linienzug statt Dutzender Einzelstriche.</summary>
    void DrawComet(DrawingContext dc, double phase, double length, double alpha, int colorKey)
    {
        if (alpha < 0.01) return;
        int n = _perimeter.Length;
        // Kopf stufenlos zwischen zwei Stützpunkten – sonst springt der Schweif von Punkt zu Punkt
        double headF = (phase % 1 + 1) % 1 * n;
        int i0 = (int)headF % n;
        double frac = headF - Math.Floor(headF);
        var p0 = _perimeter[i0];
        var p1 = _perimeter[(i0 + 1) % n];
        var headPt = new Point(p0.X + (p1.X - p0.X) * frac, p0.Y + (p1.Y - p0.Y) * frac);

        int len = Math.Max(10, (int)(length * n));
        int buckets = _buckets.Length;
        foreach (var b in _buckets) b.Clear();
        Point prev = headPt;
        for (int j = 0; j <= len; j++)
        {
            // j = 0 ist der Kopf, danach die Stützpunkte rückwärts
            var pt = j == 0 ? headPt : _perimeter[(i0 - (j - 1) + n * 4) % n];
            double dist = j == 0 ? 0 : j - 1 + frac;
            double f = Math.Max(0, 1 - dist / len);
            int k = Math.Min(buckets - 1, (int)(f * f * buckets));
            var list = _buckets[k];
            // Übergang zwischen Stufen: vorigen Punkt mitnehmen, damit keine Lücke entsteht
            if (list.Count == 0 && j > 0) list.Add(prev);
            list.Add(pt);
            prev = pt;
        }
        for (int k = 0; k < buckets; k++)
        {
            var pts = _buckets[k];
            if (pts.Count < 2) continue;
            double f = (k + 0.5) / buckets;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, PenFor(colorKey, alpha * 0.18 * f, 2), g);
            dc.DrawGeometry(null, PenFor(colorKey, alpha * f, 1), g);
        }
        dc.DrawEllipse(HeadFor(colorKey, alpha), null, headPt, 10, 10);
    }
}
