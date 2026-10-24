using System;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Gemeinsamer Takt für alle Animationen, in zwei Spuren:
/// <list type="bullet">
/// <item><b>Bildtakt</b> (<see cref="SubscribeFrame"/>): jedes Bild des Bildschirms – für alles, was sich schnell bewegt
/// (Impulse, Lichtschweif, Cursor). Nur hier wirkt Bewegung wirklich flüssig.</item>
/// <item><b>Langsamer Takt</b> (<see cref="Subscribe"/>): <see cref="Fps"/> Bilder/s – für teure, aber träge Ebenen
/// (das treibende Netz). Diese Ebenen sind zwischengespeichert; dazwischen blendet WPF nur die fertige Textur ein.</item>
/// </list>
/// Alle Teilnehmer einer Spur ändern sich im selben Bild, damit WPF pro Bild nur einmal zusammensetzt.
/// </summary>
public static class AnimationTicker
{
    static event Action<double>? Slow;
    static event Action<double>? Fast;
    static int _subscribers;
    static double _slowAcc, _fastAcc;
    static TimeSpan _last;

    /// <summary>Zielrate der langsamen Spur (Bilder/s).</summary>
    public static double Fps { get; set; } = 30;

    /// <summary>Zielrate des Bildtakts (Bilder/s).</summary>
    public static double FrameFps { get; set; } = 70;

    /// <summary>Gemessene Bildwiederholrate des Bildschirms, auf dem Axon gerade läuft.</summary>
    public static double DisplayHz => 1 / _frameInterval;

    static double _frameInterval = 1 / 60.0;
    static long _frameNo;

    public static void Subscribe(Action<double> handler) { Slow += handler; Add(); }
    public static void Unsubscribe(Action<double> handler) { Slow -= handler; Remove(); }
    public static void SubscribeFrame(Action<double> handler) { Fast += handler; Add(); }
    public static void UnsubscribeFrame(Action<double> handler) { Fast -= handler; Remove(); }

    static void Add()
    {
        if (_subscribers++ == 0) CompositionTarget.Rendering += OnRendering;
    }

    static void Remove()
    {
        if (--_subscribers > 0) return;
        _subscribers = 0;
        CompositionTarget.Rendering -= OnRendering;
        _last = TimeSpan.Zero;
    }

    static void OnRendering(object? sender, EventArgs e)
    {
        var t = ((RenderingEventArgs)e).RenderingTime;
        if (t == _last) return;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (t - _last).TotalSeconds;
        _last = t;
        dt = Math.Clamp(dt, 0, 0.25);

        // Bildwiederholrate mitlernen (Ausreißer durch verpasste Bilder ignorieren)
        if (dt > 0.003 && dt < _frameInterval * 1.6) _frameInterval += (dt - _frameInterval) * 0.05;

        // Gezählt wird in ganzen Bildschirmbildern: bei 144 Hz jedes 2. Bild (72/s), bei 60 Hz jedes Bild.
        // Gleichmäßige Abstände wirken flüssiger als eine höhere, aber unregelmäßige Rate.
        double hz = DisplayHz;
        int fastDiv = Math.Max(1, (int)Math.Round(hz / FrameFps));
        int slowDiv = Math.Max(1, (int)Math.Round(hz / Fps));
        _frameNo++;

        _fastAcc += dt;
        if (_frameNo % fastDiv == 0)
        {
            double step = Math.Min(_fastAcc, 0.1);
            _fastAcc = 0;
            Invoke(Fast, step);
        }

        _slowAcc += dt;
        if (_frameNo % slowDiv == 0)
        {
            double step = Math.Min(_slowAcc, 0.1);
            _slowAcc = 0;
            Invoke(Slow, step);
        }
        if (Timing) ReportTiming(dt);
    }

    // Diagnose (AXON_PERF=timing): Rechenzeit je Teilnehmer, alle 5 s ins Log
    static readonly bool Timing = (Environment.GetEnvironmentVariable("AXON_PERF") ?? "").Contains("timing");
    static readonly System.Collections.Generic.Dictionary<string, double> _ms = new();
    static double _timingWindow;
    static int _frames;

    static void Invoke(Action<double>? handlers, double step)
    {
        if (handlers == null) return;
        if (!Timing) { handlers(step); return; }
        foreach (Action<double> h in handlers.GetInvocationList())
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            h(step);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            string key = h.Target?.GetType().Name + "." + h.Method.Name;
            _ms[key] = (_ms.TryGetValue(key, out var sum) ? sum : 0) + ms;
        }
    }

    static void ReportTiming(double dt)
    {
        _frames++;
        _timingWindow += dt;
        if (_timingWindow < 5) return;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var kv in _ms) parts.Add($"{kv.Key} {kv.Value / _timingWindow:0.0} ms/s");
        Services.AppPaths.Log($"timing: Bildschirm {DisplayHz:0} Hz, {_frames / _timingWindow:0} Bilder/s · " + string.Join(" · ", parts));
        _ms.Clear();
        _frames = 0;
        _timingWindow = 0;
    }
}

/// <summary>DrawingVisual, das als Textur zwischengespeichert wird (nur neu gerastert, wenn sich sein Inhalt ändert).</summary>
public sealed class CachedVisual : DrawingVisual
{
    public CachedVisual() => VisualCacheMode = new BitmapCache { SnapsToDevicePixels = true };
}
