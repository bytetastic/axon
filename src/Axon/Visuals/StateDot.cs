using System;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>Kleiner Statuspunkt für Tabs und Statusleiste: pulsiert bei Arbeit, atmet bernsteinfarben bei Freigaben.</summary>
public sealed class StateDot : FrameworkElement
{
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SessionState), typeof(StateDot), new FrameworkPropertyMetadata(SessionState.Starting, (d, _) => ((StateDot)d).Changed()));

    public SessionState State
    {
        get => (SessionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    readonly DrawingVisual _visual = new();
    bool _subscribed;
    double _t;

    public StateDot()
    {
        AddVisualChild(_visual);
        Width = 14;
        Height = 14;
        IsHitTestVisible = false;
        Loaded += (_, _) => Changed();
        Unloaded += (_, _) => Subscribe(false);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    bool Animated => State is SessionState.Working or SessionState.Attention or SessionState.Starting;

    void Changed()
    {
        Subscribe(IsLoaded && Animated);
        Draw();
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
        Draw();
    }

    public static Color ColorFor(SessionState s) => s switch
    {
        SessionState.Working => MoodPalette.Mint,
        SessionState.Attention => MoodPalette.Amber,
        SessionState.Done => MoodPalette.Emerald,
        SessionState.Error => MoodPalette.Coral,
        SessionState.Exited => ThemeManager.Current.Exited,
        SessionState.Starting => MoodPalette.Teal,
        _ => ThemeManager.Current.Ready,
    };

    void Draw()
    {
        var c = ColorFor(State);
        var center = new Point(7, 7);
        using var dc = _visual.RenderOpen();
        double core = 3;
        switch (State)
        {
            case SessionState.Working:
                {
                    double p = (_t % 1.6) / 1.6;
                    var ring = new Pen(new SolidColorBrush(MoodPalette.WithAlpha(c, 0.7 * (1 - p))), 1.2);
                    ring.Freeze();
                    dc.DrawEllipse(null, ring, center, 3 + 4 * p, 3 + 4 * p);
                    break;
                }
            case SessionState.Attention:
            case SessionState.Starting:
                {
                    double b = 0.5 + 0.5 * Math.Sin(_t * Math.PI * 2 / (State == SessionState.Attention ? 1.4 : 2.2));
                    var glow = new RadialGradientBrush(MoodPalette.WithAlpha(c, 0.55 * b), MoodPalette.WithAlpha(c, 0));
                    glow.Freeze();
                    dc.DrawEllipse(glow, null, center, 7, 7);
                    break;
                }
            case SessionState.Done:
                {
                    var glow = new RadialGradientBrush(MoodPalette.WithAlpha(c, 0.4), MoodPalette.WithAlpha(c, 0));
                    glow.Freeze();
                    dc.DrawEllipse(glow, null, center, 7, 7);
                    break;
                }
        }
        var fill = new SolidColorBrush(c);
        fill.Freeze();
        dc.DrawEllipse(fill, null, center, core, core);
    }
}
