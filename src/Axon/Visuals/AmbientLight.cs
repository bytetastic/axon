using System;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Atmender Hintergrund: Solange Claude arbeitet, wird der Hintergrund ganz langsam heller und dunkler.
/// Beim Abschicken und beim Fertigwerden hellt er kurz sanft auf.
/// Zwei zwischengespeicherte Ebenen (Licht, Schatten) – pro Bild ändert sich nur ihre Deckkraft, das kostet kaum etwas.
/// </summary>
public sealed class AmbientLight : FrameworkElement
{
    readonly CachedVisual _light = new();
    readonly CachedVisual _shade = new();
    Size _size;
    Point _focus = new(double.NaN, double.NaN);
    bool _subscribed;
    double _t, _intensity, _flash;
    Color _lightColor;

    /// <summary>Dauer eines Atemzugs (hell → dunkel → hell) in Sekunden.</summary>
    const double Period = 4.6;

    public AmbientLight()
    {
        AddVisualChild(_shade);
        AddVisualChild(_light);
        IsHitTestVisible = false;
        _light.Opacity = 0;
        _shade.Opacity = 0;
        _lightColor = MoodPalette.Mint;
        Unloaded += (_, _) => SetSubscribed(false);
        Loaded += (_, _) => { if (Target > 0 || _flash > 0) Wake(); };
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _shade : _light;

    Mood _mood = Mood.Calm;
    public Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            var c = value == Mood.Attention ? MoodPalette.Amber : value == Mood.Error ? MoodPalette.Coral : MoodPalette.Mint;
            if (c != _lightColor) { _lightColor = c; DrawLayers(); }
            if (Target > 0) Wake();
        }
    }

    AnimationLevel _level = AnimationLevel.Full;
    public AnimationLevel Level
    {
        get => _level;
        set
        {
            _level = value;
            if (value == AnimationLevel.Off)
            {
                SetSubscribed(false);
                _intensity = _flash = 0;
                _light.Opacity = _shade.Opacity = 0;
            }
            else if (Target > 0) Wake();
        }
    }

    /// <summary>Kurzes, sanftes Aufhellen (0–1).</summary>
    public void Flash(double strength)
    {
        if (Level == AnimationLevel.Off) return;
        _flash = Math.Max(_flash, strength);
        Wake();
    }

    public void SetFocusPoint(Point p)
    {
        if (p == _focus) return;
        _focus = p;
        DrawLayers();
    }

    double Target => Mood switch
    {
        Mood.Working => 1,
        Mood.Attention => 0.55,
        _ => 0,
    };

    void Wake() => SetSubscribed(IsLoaded && Level != AnimationLevel.Off);

    void SetSubscribed(bool on)
    {
        if (on == _subscribed) return;
        _subscribed = on;
        if (on) AnimationTicker.SubscribeFrame(OnFrame);
        else AnimationTicker.UnsubscribeFrame(OnFrame);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _size = sizeInfo.NewSize;
        DrawLayers();
    }

    void DrawLayers()
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        var rect = new Rect(0, 0, _size.Width, _size.Height);
        var c = _lightColor;
        var center = double.IsNaN(_focus.X)
            ? new Point(0.5, 0.5)
            : new Point(Math.Clamp(_focus.X / _size.Width, 0, 1), Math.Clamp(_focus.Y / _size.Height, 0, 1));

        // Licht: ein weiter, weicher Schein um die Mitte des Terminals plus ein Hauch über die ganze Fläche
        var glow = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = 0.85,
            RadiusY = 0.95,
            GradientStops =
            {
                new GradientStop(MoodPalette.WithAlpha(c, 0.20), 0),
                new GradientStop(MoodPalette.WithAlpha(c, 0.09), 0.45),
                new GradientStop(MoodPalette.WithAlpha(c, 0.02), 1),
            },
        };
        glow.Freeze();
        using (var dc = _light.RenderOpen()) dc.DrawRectangle(glow, null, rect);

        // Schatten: zu den Rändern hin etwas kräftiger, damit das Dunklerwerden nicht flach wirkt
        var shade = new RadialGradientBrush
        {
            Center = center,
            GradientOrigin = center,
            RadiusX = 0.9,
            RadiusY = 1.0,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(70, 0, 0, 0), 0),
                new GradientStop(Color.FromArgb(120, 0, 0, 0), 1),
            },
        };
        shade.Freeze();
        using (var dc = _shade.RenderOpen()) dc.DrawRectangle(shade, null, rect);
    }

    void OnFrame(double dt)
    {
        _t += dt;
        double target = Target;
        // weich ein- und ausblenden, damit der Wechsel nie abrupt wirkt
        _intensity += (target - _intensity) * (1 - Math.Exp(-dt / 1.6));
        _flash *= Math.Exp(-dt / 0.9);

        double amp = Level == AnimationLevel.Subtle ? 0.5 : 1.0;
        // Sinus über eine weiche Kurve: verweilt kurz hell und kurz dunkel, wie Atmen
        double s = Math.Sin(_t * Math.PI * 2 / Period);
        double b = 0.5 + 0.5 * Math.Sign(s) * Math.Pow(Math.Abs(s), 0.8);
        double light = amp * (_intensity * (0.15 + 0.85 * b) + 0.7 * _flash);
        double shade = amp * _intensity * (1 - b);
        SetOpacity(_light, Math.Clamp(light, 0, 1));
        SetOpacity(_shade, Math.Clamp(shade, 0, 1));

        if (target == 0 && _intensity < 0.003 && _flash < 0.003)
        {
            _intensity = _flash = 0;
            SetOpacity(_light, 0);
            SetOpacity(_shade, 0);
            SetSubscribed(false);
        }
    }

    static void SetOpacity(Visual v, double o)
    {
        var cv = (ContainerVisual)v;
        if (Math.Abs(cv.Opacity - o) > 0.001) cv.Opacity = o;
    }
}
