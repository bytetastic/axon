using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Das Axon-Logo (ein „A“ aus Neuronen), das sich selbst zeichnen kann:
/// Spitze erscheint → Schenkel wachsen nach unten → Querbalken schließt sich → Knoten ploppen auf.
/// <see cref="Progress"/> 0…1; bei 1 sieht es aus wie das statische Logo. Farben kommen aus dem aktuellen Thema.
/// </summary>
public sealed class LogoMark : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(LogoMark), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    // Koordinaten im 100×100-Raster, identisch mit dem Titelleisten-Logo (AxonTheme.Logo)
    static readonly Point Apex = new(50, 14), Bl = new(18, 86), Br = new(82, 86), Ml = new(32, 58), Mr = new(68, 58);

    /// <summary>Stifte und Pinsel in den Farben eines Themas.</summary>
    sealed record Look(Pen GlowPen, Pen LinePen, Brush NodeBrush, Brush ApexBrush, Brush NodeHalo);

    static readonly Dictionary<AxonTheme, Look> Looks = new();

    static Look LookFor(AxonTheme t)
    {
        if (Looks.TryGetValue(t, out var l)) return l;
        Color node = t.Mint;
        l = new Look(
            MakePen(Color.FromArgb(0x33, node.R, node.G, node.B), 16),
            MakeGradientPen(node, t.LogoBottom),
            Frozen(new SolidColorBrush(node)),
            Frozen(new SolidColorBrush(t.LogoApex)),
            Frozen(new RadialGradientBrush(Color.FromArgb(0xAA, node.R, node.G, node.B), Color.FromArgb(0, node.R, node.G, node.B))));
        Looks[t] = l;
        return l;
    }

    public LogoMark()
    {
        // Themenwechsel: neu zeichnen
        Loaded += (_, _) => ThemeManager.Changed += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.Changed -= InvalidateVisual;
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static Pen MakePen(Color c, double w)
    {
        var p = new Pen(new SolidColorBrush(c), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    static Pen MakeGradientPen(Color top, Color bottom)
    {
        var b = new LinearGradientBrush(top, bottom, new Point(0, 0), new Point(0, 1))
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(0, 14),
            EndPoint = new Point(0, 86),
        };
        b.Freeze();
        var p = new Pen(b, 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    static double Seg(double p, double from, double to) => Math.Clamp((p - from) / (to - from), 0, 1);
    static double Ease(double x) => 1 - Math.Pow(1 - x, 3);
    static Point Lerp(Point a, Point b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    /// <summary>Ease-out-back: kurzes Überschwingen beim Aufploppen der Knoten.</summary>
    static double Pop(double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(x - 1, 3) + c1 * Math.Pow(x - 1, 2);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight) / 100;
        if (s <= 0) return;
        double p = Progress;
        var look = LookFor(ThemeManager.Current);
        dc.PushTransform(new ScaleTransform(s, s));

        double legs = Ease(Seg(p, 0.12, 0.62));
        double bar = Ease(Seg(p, 0.5, 0.78));
        var footL = Lerp(Apex, Bl, legs);
        var footR = Lerp(Apex, Br, legs);
        var mid = Lerp(Ml, Mr, 0.5);
        var barL = Lerp(mid, Ml, bar);
        var barR = Lerp(mid, Mr, bar);

        foreach (var pen in new[] { look.GlowPen, look.LinePen })
        {
            if (legs > 0)
            {
                dc.DrawLine(pen, Apex, footL);
                dc.DrawLine(pen, Apex, footR);
            }
            if (bar > 0) dc.DrawLine(pen, barL, barR);
        }

        // Knoten: Spitze zuerst, Mitte sobald die Schenkel vorbei sind, Füße am Ende
        DrawNode(dc, Apex, 9, look.ApexBrush, look.NodeHalo, Seg(p, 0.0, 0.18));
        double midNode = Seg(p, 0.42, 0.6);
        DrawNode(dc, Ml, 7, look.NodeBrush, look.NodeHalo, midNode);
        DrawNode(dc, Mr, 7, look.NodeBrush, look.NodeHalo, midNode);
        double feet = Seg(p, 0.6, 0.8);
        DrawNode(dc, Bl, 8, look.NodeBrush, look.NodeHalo, feet);
        DrawNode(dc, Br, 8, look.NodeBrush, look.NodeHalo, feet);

        dc.Pop();
    }

    /// <param name="x">Fortschritt dieses Knotens 0…1</param>
    static void DrawNode(DrawingContext dc, Point c, double r, Brush fill, Brush halo, double x)
    {
        if (x <= 0) return;
        // beim Erscheinen ein kurzer Lichthof, der wieder abklingt
        double glow = x < 1 ? Math.Sin(x * Math.PI) : 0;
        if (glow > 0.01)
        {
            dc.PushOpacity(glow);
            dc.DrawEllipse(halo, null, c, r * 3, r * 3);
            dc.Pop();
        }
        double k = Pop(x);
        dc.DrawEllipse(fill, null, c, r * k, r * k);
    }
}
