using System;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>Farbe für eine Auslastung: grün → bernstein ab 60 % → koralle ab 85 %.</summary>
public static class UsageColors
{
    public static Color For(double percent) => percent switch
    {
        >= 85 => MoodPalette.Coral,
        >= 60 => MoodPalette.Amber,
        _ => MoodPalette.Mint,
    };
}

/// <summary>Kleiner Ring für die Statusleiste. <see cref="Value"/> in Prozent, NaN = keine Daten.</summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageRing), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public UsageRing()
    {
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        double r = size / 2 - 1.5;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x3D, 0xDC, 0x97)), 2);
        track.Freeze();
        dc.DrawEllipse(null, track, c, r, r);
        if (double.IsNaN(Value) || Value <= 0) return;

        double v = Math.Clamp(Value, 0, 100) / 100;
        var col = UsageColors.For(Value);
        var pen = new Pen(new SolidColorBrush(col), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (v >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double a0 = -Math.PI / 2, a1 = a0 + v * Math.PI * 2;
        var start = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var end = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Balken mit weichem Verlauf für die Nutzungsübersicht.</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public UsageBar()
    {
        Height = 6;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var track = new SolidColorBrush(Color.FromArgb(0x26, 0x3D, 0xDC, 0x97));
        track.Freeze();
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        double v = Math.Clamp(Value, 0, 100) / 100;
        if (v <= 0) return;
        var col = UsageColors.For(Value);
        var fill = new LinearGradientBrush(MoodPalette.Mix(MoodPalette.Emerald, col, 0.4), col, 0);
        fill.Freeze();
        double fw = Math.Max(h, w * v);
        dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, fw, h), h / 2, h / 2);
        // leichter Schein am Ende des Balkens
        var glow = new RadialGradientBrush(MoodPalette.WithAlpha(col, 0.55), MoodPalette.WithAlpha(col, 0));
        glow.Freeze();
        dc.DrawEllipse(glow, null, new Point(fw - h / 2, h / 2), h * 1.8, h * 1.8);
    }
}
