using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Klangkurve während der Spracheingabe: drei überlagerte, an den Enden auslaufende Wellen.
/// Beim Sprechen schwellen sie weich mit der Stimme an (in Ruhe atmen sie nur leicht), beim Erkennen beruhigen sie sich
/// und gleiten von Korallenrot nach Grün. Hängt nur am Bildtakt, solange sie läuft.
/// </summary>
public sealed class VoiceWave : FrameworkElement
{
    const int Segments = 72;
    const int ColorSteps = 24;
    static readonly double[] Freq = { 2.2, 3.1, 4.3 };     // Wellenzüge über die ganze Breite
    static readonly double[] Speed = { 1.9, -1.3, 2.6 };   // rad/s – ruhig, nie hektisch
    static readonly double[] Gain = { 1.0, 0.62, 0.38 };
    static readonly double[] Alpha = { 0.95, 0.5, 0.32 };

    readonly DrawingVisual _visual = new();
    readonly Dictionary<int, Pen> _pens = new();
    readonly double[] _phase = new double[3];
    bool _subscribed, _recording;
    double _level, _mix; // _mix: 0 = Aufnahme (rot), 1 = Erkennen (grün)

    /// <summary>Wird jedes Bild abgefragt, solange aufgenommen wird (0 … 1).</summary>
    public Func<double>? LevelSource { get; set; }

    public VoiceWave()
    {
        AddVisualChild(_visual);
        IsHitTestVisible = false;
        Unloaded += (_, _) => Stop();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    /// <summary>Aufnahme läuft (true) bzw. Whisper rechnet (false). Startet die Animation, falls nötig.</summary>
    public void Run(bool recording)
    {
        if (recording && !_subscribed) { _mix = 0; _level = 0; }
        _recording = recording;
        if (_subscribed) return;
        _subscribed = true;
        AnimationTicker.SubscribeFrame(OnTick);
    }

    public void Stop()
    {
        if (!_subscribed) return;
        _subscribed = false;
        AnimationTicker.UnsubscribeFrame(OnTick);
    }

    void OnTick(double dt)
    {
        // Sprache landet beim Pegel meist bei 0,6–0,9 → etwas spreizen, damit die Stimme die Welle wirklich füllt
        double target = _recording ? 0.08 + 0.92 * Math.Min(1, Math.Pow(LevelSource?.Invoke() ?? 0, 1.6) * 1.35) : 0.05;
        // schnell an, langsam ab – die Stimme trägt die Welle, ohne zu zappeln
        double k = target > _level ? 1 - Math.Exp(-dt / 0.07) : 1 - Math.Exp(-dt / 0.4);
        _level += (target - _level) * k;
        _mix += ((_recording ? 0 : 1) - _mix) * (1 - Math.Exp(-dt / 0.35));
        double tempo = 1 + 0.8 * _level; // lauter → etwas lebhafter
        for (int i = 0; i < 3; i++) _phase[i] += Speed[i] * tempo * dt;
        Draw();
    }

    Pen PenFor(int curve, bool glow)
    {
        int step = (int)Math.Round(_mix * (ColorSteps - 1));
        int key = step * 8 + curve * 2 + (glow ? 1 : 0);
        if (!_pens.TryGetValue(key, out var pen))
        {
            var c = MoodPalette.Mix(MoodPalette.Coral, MoodPalette.Mint, step / (double)(ColorSteps - 1));
            pen = glow
                ? new Pen(new SolidColorBrush(MoodPalette.WithAlpha(c, 0.16)), 6)
                : new Pen(new SolidColorBrush(MoodPalette.WithAlpha(c, Alpha[curve])), curve == 0 ? 1.8 : 1.2);
            pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
            pen.LineJoin = PenLineJoin.Round;
            pen.Freeze();
            _pens[key] = pen;
        }
        return pen;
    }

    void Draw()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double mid = h / 2, amp = (h / 2 - 2) * _level;
        using var dc = _visual.RenderOpen();
        // hinten die zarten, vorne die Hauptwelle mit weichem Schein
        for (int i = 2; i >= 0; i--)
        {
            var g = Curve(i, w, mid, amp);
            if (i == 0) dc.DrawGeometry(null, PenFor(0, true), g);
            dc.DrawGeometry(null, PenFor(i, false), g);
        }
    }

    StreamGeometry Curve(int i, double w, double mid, double amp)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, mid), false, false);
            var pts = new Point[Segments];
            for (int s = 1; s <= Segments; s++)
            {
                double u = s / (double)Segments;
                double env = Math.Pow(Math.Sin(Math.PI * u), 2); // läuft an beiden Enden in die Mittellinie aus
                double y = mid + amp * Gain[i] * env * Math.Sin(2 * Math.PI * Freq[i] * u + _phase[i]);
                pts[s - 1] = new Point(u * w, y);
            }
            ctx.PolyLineTo(pts, true, true);
        }
        g.Freeze();
        return g;
    }
}
