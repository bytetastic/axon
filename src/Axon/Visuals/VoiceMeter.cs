using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

public enum VoiceMode { Idle, Recording, Busy, Progress }

/// <summary>
/// Licht hinter dem Mikrofon-Knopf: atmet beim Aufnehmen und schwillt weich mit der Stimme an,
/// kreist langsam, während Whisper rechnet, und zeigt beim Modell-Download den Fortschritt.
/// Hängt nur am Bildtakt, solange es etwas zu zeigen gibt.
/// </summary>
public sealed class VoiceMeter : FrameworkElement
{
    readonly DrawingVisual _visual = new();
    readonly Dictionary<int, Pen> _pens = new();
    readonly Dictionary<int, Brush> _glows = new();
    VoiceMode _mode, _shown; // _shown: zuletzt gezeigter Zustand, blendet nach Idle aus
    bool _subscribed;
    double _t, _level, _progress, _fade;

    /// <summary>Wird jedes Bild abgefragt, solange aufgenommen wird (0 … 1).</summary>
    public Func<double>? LevelSource { get; set; }

    public VoiceMeter()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
        Unloaded += (_, _) => Subscribe(false);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    public VoiceMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            if (_mode == VoiceMode.Idle) _t = 0;
            _mode = value;
            if (value != VoiceMode.Idle) _shown = value;
            Subscribe(true); // auch nach Idle noch kurz weiterlaufen, damit das Licht ausblendet statt zu verschwinden
        }
    }

    public double Progress
    {
        get => _progress;
        set => _progress = Math.Clamp(value, 0, 1);
    }

    void Subscribe(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        if (on) AnimationTicker.SubscribeFrame(OnTick);
        else AnimationTicker.UnsubscribeFrame(OnTick);
    }

    void OnTick(double dt)
    {
        _t += dt;
        double target = _mode == VoiceMode.Recording ? LevelSource?.Invoke() ?? 0 : 0;
        // schnell an, langsam ab – die Stimme „trägt“ das Licht, ohne zu flackern
        double k = target > _level ? 1 - Math.Exp(-dt / 0.06) : 1 - Math.Exp(-dt / 0.35);
        _level += (target - _level) * k;
        double fadeTarget = _mode == VoiceMode.Idle ? 0 : 1;
        _fade += (fadeTarget - _fade) * (1 - Math.Exp(-dt / 0.18));
        if (_mode == VoiceMode.Idle && _fade < 0.01)
        {
            _fade = 0;
            _level = 0;
            using (_visual.RenderOpen()) { }
            Subscribe(false);
            return;
        }
        Draw();
    }

    // Stifte und Pinsel nur in 32 Helligkeitsstufen – nie pro Bild neu erzeugen
    Pen PenFor(Color c, double alpha, double thickness)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 31);
        int key = (c.R << 24 | c.G << 16 | c.B << 8) ^ a ^ (int)(thickness * 10) << 5;
        if (!_pens.TryGetValue(key, out var pen))
        {
            pen = new Pen(new SolidColorBrush(MoodPalette.WithAlpha(c, a / 31.0)), thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            _pens[key] = pen;
        }
        return pen;
    }

    Brush GlowFor(Color c, double alpha)
    {
        int a = (int)Math.Round(Math.Clamp(alpha, 0, 1) * 31);
        int key = (c.R << 24 | c.G << 16 | c.B << 8) ^ a;
        if (!_glows.TryGetValue(key, out var b))
        {
            b = new RadialGradientBrush(MoodPalette.WithAlpha(c, a / 31.0), MoodPalette.WithAlpha(c, 0));
            b.Freeze();
            _glows[key] = b;
        }
        return b;
    }

    void Draw()
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        using var dc = _visual.RenderOpen();
        double f = _fade;
        switch (_shown)
        {
            case VoiceMode.Recording:
                {
                    var c = MoodPalette.Coral;
                    double breath = 0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 2.6);
                    double glowR = 11 + 5 * _level;
                    dc.DrawEllipse(GlowFor(c, f * (0.16 + 0.10 * breath + 0.34 * _level)), null, center, glowR, glowR);
                    double r = 10.5 + 2.5 * _level + 0.6 * breath;
                    dc.DrawEllipse(null, PenFor(c, f * (0.45 + 0.45 * _level), 1.3), center, r, r);
                    break;
                }
            case VoiceMode.Busy:
                {
                    var c = MoodPalette.Teal;
                    double breath = 0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / 1.8);
                    dc.DrawEllipse(GlowFor(c, f * (0.10 + 0.12 * breath)), null, center, 13, 13);
                    double start = _t * 300; // eine Runde in 1,2 s – ruhig, aber sichtbar beschäftigt
                    DrawArc(dc, center, 10.5, start, 110, PenFor(MoodPalette.Mint, f * 0.85, 1.5));
                    break;
                }
            case VoiceMode.Progress:
                {
                    dc.DrawEllipse(null, PenFor(MoodPalette.Teal, f * 0.22, 1.5), center, 10.5, 10.5);
                    if (_progress > 0.002) DrawArc(dc, center, 10.5, 0, 360 * _progress, PenFor(MoodPalette.Mint, f * 0.9, 1.5));
                    break;
                }
        }
    }

    static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        sweepDeg = Math.Min(sweepDeg, 359.9);
        double a0 = (startDeg - 90) * Math.PI / 180, a1 = (startDeg + sweepDeg - 90) * Math.PI / 180;
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p0, false, false);
            ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
