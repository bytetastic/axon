using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Hintergrund: ein langsam treibendes Netz aus Neuronen. Signale laufen als Lichtimpulse über die Verbindungen
/// und lösen Kaskaden aus; von den Fensterkanten wachsen Dendriten herein.
/// Alles ist zeitbasiert und bewusst langsam – Bewegung soll beruhigen, nicht ablenken.
/// </summary>
public sealed class NeuralField : Backdrop
{
    sealed class Node
    {
        public double NX, NY, Z, R, Amp, F1, F2, F3, F4, P1, P2, P3, P4, Breath, BreathF, Tint;
        public double X, Y, Act;
        /// <summary>Startanimation: Zeitpunkt (s), zu dem das Neuron aufleuchtet.</summary>
        public double Appear;
        public bool Shown;
    }

    sealed class Pulse
    {
        public int A, B;
        public double P, Speed, Energy;
        public bool Alt;
    }

    sealed class Branch
    {
        public readonly List<Point> Pts = new();
        public Point Tip;
        public double Heading, Turn, Width, Length, MaxLength, SinceBranch;
        public int Depth;
        public bool Growing = true;
        public StreamGeometry? Geo;
        public int GeoCount;
    }

    sealed class Dendrite
    {
        public readonly List<Branch> Branches = new();
        public int Phase; // 0 wächst, 1 hält, 2 vergeht
        public double PhaseTime, HoldFor, Alpha, Speed;
    }

    sealed class DPulse
    {
        public Dendrite D = null!;
        public Branch B = null!;
        public double S;
    }

    sealed class Wave
    {
        public Point C;
        public double R, Max;
    }

    readonly Random _rng = new();
    Node[] _nodes = Array.Empty<Node>();
    List<int>[] _nbrs = Array.Empty<List<int>>();
    readonly List<Pulse> _pulses = new();
    readonly List<Dendrite> _dendrites = new();
    readonly List<DPulse> _dpulses = new();
    readonly List<Wave> _ripples = new();

    double _t, _spawnAcc, _nbrAcc, _dendriteCooldown;
    double _initArea;
    Size _size;
    bool _subscribed;

    Color _primary, _secondary, _highlight;
    Point _parallax, _parallaxTarget;
    Point _focus = new(double.NaN, double.NaN);

    static readonly Brush BaseBrush = Frozen(new RadialGradientBrush
    {
        Center = new Point(0.5, 0.42),
        GradientOrigin = new Point(0.5, 0.42),
        RadiusX = 0.85,
        RadiusY = 0.85,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x0C, 0x1F, 0x18), 0),
            new GradientStop(Color.FromRgb(0x07, 0x12, 0x0E), 0.55),
            new GradientStop(Color.FromRgb(0x03, 0x08, 0x06), 1),
        },
    });

    static readonly Brush Vignette = Frozen(new RadialGradientBrush
    {
        RadiusX = 0.75,
        RadiusY = 0.75,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55),
            new GradientStop(Color.FromArgb(120, 0, 0, 0), 1),
        },
    });

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // Ebenen (von unten nach oben):
    //   Grund + Nebel  – zwischengespeichert, 10× pro Sekunde neu
    //   Netz           – zwischengespeichert, langsamer Takt (Knoten treiben nur wenige Pixel pro Sekunde)
    //   Impulse        – jedes Bild, damit schnelle Bewegung flüssig ist; bewusst wenige Zeichenbefehle
    //   Vignette       – zwischengespeichert, nur bei Größenänderung
    readonly CachedVisual _bgVisual = new();
    readonly CachedVisual _visual = new();
    readonly DrawingVisual _fastVisual = new();
    readonly CachedVisual _vigVisual = new();
    double _bgAcc = 1;

    public NeuralField()
    {
        AddVisualChild(_bgVisual);
        AddVisualChild(_visual);
        AddVisualChild(_fastVisual);
        AddVisualChild(_vigVisual);
        IsHitTestVisible = false;
        (_primary, _secondary, _highlight) = MoodPalette.For(Mood.Calm);
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => SetSubscribed(false);
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    protected override int VisualChildrenCount => 4;
    protected override Visual GetVisualChild(int index) => index switch { 0 => _bgVisual, 1 => _visual, 2 => _fastVisual, _ => _vigVisual };

    // ───────────────────────────── Steuerung ─────────────────────────────

    Mood _mood = Mood.Calm;
    public override Mood Mood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            var old = _mood;
            _mood = value;
            if (value == Mood.Done && old != Mood.Done) Ripple();
            if (value == Mood.Working && old != Mood.Working) Burst(4);
            if (Level == AnimationLevel.Off) Redraw();
        }
    }

    AnimationLevel _level = AnimationLevel.Full;
    public override AnimationLevel Level
    {
        get => _level;
        set
        {
            if (_level == value) return;
            _level = value;
            _initArea = 0;
            UpdateSubscription();
            if (value == AnimationLevel.Off) Redraw();
        }
    }

    // ───────────────────────────── Startanimation ─────────────────────────────
    // 0,0–0,3 s dunkel · Neuronen erwachen von der Mitte nach außen · Verbindungen wachsen ·
    // ≈ 2 s Zündung (Welle + erste Impulse) · bis 2,8 s ist alles im Normalbetrieb.

    const double IgniteAt = 1.95, IntroEnd = 2.8;
    double _introT = -1, _introSpeed = 1;
    bool _ignited;

    public override bool IntroRunning => _introT >= 0;

    /// <summary>Startanimation abspielen. <paramref name="speed"/> &gt; 1 = kürzer (z. B. bei „dezent“).</summary>
    public override void PlayIntro(double speed = 1)
    {
        if (Level == AnimationLevel.Off) return;
        _introT = 0;
        _introSpeed = speed;
        _ignited = false;
        _pulses.Clear();
        _dpulses.Clear();
        _ripples.Clear();
        foreach (var n in _nodes) n.Shown = false;
        _bgVisual.Opacity = 0;
    }

    static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    /// <summary>Sichtbarkeit eines Neurons während der Startanimation (0…1).</summary>
    double Visible(Node n) => _introT < 0 ? 1 : Smooth((_introT - n.Appear) / 0.35);

    void StepIntro(double dt)
    {
        _introT += dt * _introSpeed;
        foreach (var n in _nodes)
            if (!n.Shown && _introT >= n.Appear)
            {
                n.Shown = true;
                n.Act = Math.Max(n.Act, 0.9); // kurzes Aufblitzen beim Erwachen
            }
        double bg = Smooth(_introT / 1.4);
        if (Math.Abs(_bgVisual.Opacity - bg) > 0.002) _bgVisual.Opacity = bg;
        if (!_ignited && _introT >= IgniteAt)
        {
            _ignited = true;
            var keep = _focus;
            _focus = new Point(_size.Width / 2, _size.Height * 0.45);
            Ripple();
            _focus = keep;
            Burst(7);
            RaiseIgnited();
        }
        if (_introT >= IntroEnd)
        {
            _introT = -1;
            _bgVisual.Opacity = 1;
        }
    }

    /// <summary>Mittelpunkt des Terminals – Ursprung der „Fertig“-Welle.</summary>
    public override void SetFocusPoint(Point p) => _focus = p;

    public override void SetPointer(Point p)
    {
        if (_size.Width <= 0) return;
        _parallaxTarget = new Point((p.X / _size.Width - 0.5) * -2, (p.Y / _size.Height - 0.5) * -2);
    }

    /// <summary>Benutzer hat etwas abgeschickt: Signale laufen die Dendriten entlang ins Netz.</summary>
    public override void FireInput()
    {
        if (Level == AnimationLevel.Off) return;
        foreach (var d in _dendrites)
            if (d.Branches.Count > 0 && d.Branches[0].Pts.Count > 2 && d.Phase < 2)
                _dpulses.Add(new DPulse { D = d, B = d.Branches[0], S = 0 });
        Burst(3);
    }

    public void Ripple()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        var c = double.IsNaN(_focus.X) ? new Point(_size.Width / 2, _size.Height / 2) : _focus;
        double max = Math.Sqrt(_size.Width * _size.Width + _size.Height * _size.Height) * 0.75;
        _ripples.Add(new Wave { C = c, R = 0, Max = max });
    }

    /// <summary>Neuer Tab: ein neuer Dendrit wächst.</summary>
    public override void GrowDendrite()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        if (_dendrites.Count >= MaxDendrites + 1) return;
        SpawnDendrite();
    }

    void Burst(int count)
    {
        if (_nodes.Length == 0) return;
        for (int i = 0; i < count; i++) StartPulse(_rng.Next(_nodes.Length), 1.0);
    }

    // ───────────────────────────── Ablauf ─────────────────────────────

    void UpdateSubscription() => SetSubscribed(IsLoaded && IsVisible && Level != AnimationLevel.Off);

    void SetSubscribed(bool want)
    {
        if (want == _subscribed) return;
        _subscribed = want;
        if (want)
        {
            AnimationTicker.Subscribe(OnSlow);
            AnimationTicker.SubscribeFrame(OnFrame);
        }
        else
        {
            AnimationTicker.Unsubscribe(OnSlow);
            AnimationTicker.UnsubscribeFrame(OnFrame);
            using (_fastVisual.RenderOpen()) { }
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _size = sizeInfo.NewSize;
        using (var dc = _vigVisual.RenderOpen())
            dc.DrawRectangle(Vignette, null, new Rect(0, 0, _size.Width, _size.Height));
        _bgAcc = 1;
        if (Level == AnimationLevel.Off) Redraw();
    }

    void Redraw()
    {
        if (_size.Width <= 0) return;
        EnsureNodes();
        Step(0);
        _bgAcc = 1;
        Draw(0);
    }

    /// <summary>Langsamer Takt: Knoten, Farben, Dendriten – und die zwischengespeicherte Netz-Ebene.</summary>
    void OnSlow(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        if (IntroRunning) return; // während der Startanimation zeichnet der Bildtakt alles
        EnsureNodes();
        Step(dt);
        Draw(dt);
    }

    /// <summary>Bildtakt: alles, was sich schnell bewegt – und während der Startanimation auch das Netz selbst.</summary>
    void OnFrame(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        if (IntroRunning)
        {
            EnsureNodes();
            StepIntro(dt);
            Step(dt);
            Draw(dt);
        }
        if (_nodes.Length == 0) return;
        StepFast(dt);
        DrawFast();
    }

    int MaxDendrites => Level == AnimationLevel.Subtle ? 2 : 3;

    void EnsureNodes()
    {
        double area = _size.Width * _size.Height;
        if (_nodes.Length > 0 && area > _initArea * 0.5 && area < _initArea * 2) return;
        _initArea = area;
        double density = Level == AnimationLevel.Subtle ? 21000 : 13000;
        int count = (int)Math.Clamp(area / density, 30, 130);
        var nodes = new List<Node>(count);
        // „Best candidate“-Verteilung: gleichmäßig, aber organisch
        for (int i = 0; i < count; i++)
        {
            double bx = 0, by = 0, best = -1;
            for (int k = 0; k < 10; k++)
            {
                double x = -0.04 + _rng.NextDouble() * 1.08, y = -0.04 + _rng.NextDouble() * 1.08;
                double dmin = double.MaxValue;
                foreach (var n in nodes)
                {
                    double dx = (n.NX - x) * _size.Width, dy = (n.NY - y) * _size.Height;
                    dmin = Math.Min(dmin, dx * dx + dy * dy);
                }
                if (dmin > best) { best = dmin; bx = x; by = y; }
            }
            double z = 0.25 + Math.Pow(_rng.NextDouble(), 1.4) * 0.75;
            nodes.Add(new Node
            {
                NX = bx, NY = by, Z = z,
                R = 0.9 + z * 1.7,
                Amp = 14 + _rng.NextDouble() * 34,
                F1 = Freq(28, 70), F2 = Freq(18, 45), F3 = Freq(30, 75), F4 = Freq(20, 50),
                P1 = Ph(), P2 = Ph(), P3 = Ph(), P4 = Ph(),
                Breath = Ph(), BreathF = Freq(6, 11),
                Tint = _rng.NextDouble() < 0.28 ? 1 : 0,
            });
        }
        // Startanimation: Neuronen erwachen von der Mitte nach außen, mit etwas Zufall
        foreach (var n in nodes)
        {
            double dx = n.NX - 0.5, dy = (n.NY - 0.45) * _size.Height / Math.Max(1, _size.Width);
            double d = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / 0.62);
            n.Appear = 0.25 + 1.15 * d + _rng.NextDouble() * 0.14;
        }
        _nodes = nodes.ToArray();
        _nbrs = new List<int>[_nodes.Length];
        for (int i = 0; i < _nbrs.Length; i++) _nbrs[i] = new List<int>();
        _pulses.Clear();
        _nbrAcc = 999;

        if (_dendrites.Count == 0 && Level != AnimationLevel.Off)
        {
            // beim Start schon ein paar halb gewachsene Dendriten, damit der Rand nicht leer ist
            for (int i = 0; i < MaxDendrites; i++)
            {
                var d = SpawnDendrite();
                double pre = 4 + _rng.NextDouble() * 14;
                for (double s = 0; s < pre; s += 0.1) GrowDendrite(d, 0.1);
            }
        }
    }

    double Freq(double minPeriod, double maxPeriod) => Math.PI * 2 / (minPeriod + _rng.NextDouble() * (maxPeriod - minPeriod));
    double Ph() => _rng.NextDouble() * Math.PI * 2;

    double MaxDist => Math.Clamp(Math.Sqrt(_size.Width * _size.Height / Math.Max(1, _nodes.Length)) * 1.55, 110, 230);

    void Step(double dt)
    {
        _t += dt;

        var (tp, ts, th) = MoodPalette.For(Mood);
        double kc = 1 - Math.Exp(-dt / 0.9);
        _primary = MoodPalette.Approach(_primary, tp, kc);
        _secondary = MoodPalette.Approach(_secondary, ts, kc);
        _highlight = MoodPalette.Approach(_highlight, th, kc);

        if (Level == AnimationLevel.Full)
        {
            double kp = 1 - Math.Exp(-dt / 1.2);
            _parallax = new Point(_parallax.X + (_parallaxTarget.X - _parallax.X) * kp, _parallax.Y + (_parallaxTarget.Y - _parallax.Y) * kp);
        }

        double W = _size.Width, H = _size.Height;
        double decay = Math.Exp(-dt / 1.3);
        foreach (var n in _nodes)
        {
            double ox = n.Amp * (Math.Sin(_t * n.F1 + n.P1) * 0.7 + Math.Sin(_t * n.F2 + n.P2) * 0.3);
            double oy = n.Amp * (Math.Sin(_t * n.F3 + n.P3) * 0.7 + Math.Sin(_t * n.F4 + n.P4) * 0.3);
            n.X = n.NX * W + ox + _parallax.X * 14 * n.Z;
            n.Y = n.NY * H + oy + _parallax.Y * 10 * n.Z;
            n.Act *= decay;
        }

        _nbrAcc += dt;
        if (_nbrAcc > 0.5)
        {
            _nbrAcc = 0;
            double md = MaxDist * 0.92, md2 = md * md;
            for (int i = 0; i < _nodes.Length; i++) _nbrs[i].Clear();
            for (int i = 0; i < _nodes.Length; i++)
                for (int j = i + 1; j < _nodes.Length; j++)
                {
                    double dx = _nodes[i].X - _nodes[j].X, dy = _nodes[i].Y - _nodes[j].Y;
                    if (dx * dx + dy * dy < md2) { _nbrs[i].Add(j); _nbrs[j].Add(i); }
                }
        }

        if (dt <= 0) return;
        UpdateDendrites(dt);
    }

    void StepFast(double dt)
    {
        if (dt <= 0) return;
        // vor der Zündung feuert noch nichts von selbst
        if (IntroRunning && !_ignited)
        {
            UpdateRipples(dt);
            return;
        }
        // spontane Impulse je nach Stimmung
        double rate = Mood switch
        {
            Mood.Working => 2.2,
            Mood.Attention => 0.7,
            Mood.Done => 0.45,
            _ => 0.32,
        };
        if (Level == AnimationLevel.Subtle) rate *= 0.5;
        _spawnAcc += dt * rate;
        while (_spawnAcc >= 1 && _nodes.Length > 0)
        {
            _spawnAcc -= 1;
            StartPulse(_rng.Next(_nodes.Length), 1.0);
        }

        UpdatePulses(dt);
        UpdateDendritePulses(dt);
        UpdateRipples(dt);
    }

    void StartPulse(int from, double energy, int exclude = -1)
    {
        if (from < 0 || from >= _nodes.Length || _nbrs.Length == 0) return;
        var list = _nbrs[from];
        if (list.Count == 0) return;
        if (_pulses.Count > 70) return;
        int to = list[_rng.Next(list.Count)];
        if (to == exclude && list.Count > 1) to = list[_rng.Next(list.Count)];
        if (to == exclude) return;
        bool working = Mood == Mood.Working;
        _pulses.Add(new Pulse
        {
            A = from,
            B = to,
            P = 0,
            Speed = working ? 120 + _rng.NextDouble() * 70 : 65 + _rng.NextDouble() * 45,
            Energy = energy,
            Alt = _rng.NextDouble() < (Mood is Mood.Attention or Mood.Error ? 0.6 : 0.3),
        });
        _nodes[from].Act = Math.Max(_nodes[from].Act, energy * 0.6);
    }

    void UpdatePulses(double dt)
    {
        double keep = Mood == Mood.Working ? 0.82 : 0.7;
        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            var a = _nodes[p.A];
            var b = _nodes[p.B];
            double len = Math.Max(1, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
            p.P += p.Speed * dt / len;
            if (p.P < 1) continue;
            _pulses.RemoveAt(i);
            b.Act = Math.Max(b.Act, p.Energy);
            double e = p.Energy * keep;
            if (e < 0.2) continue;
            int children = e > 0.6 ? 1 + (_rng.NextDouble() < 0.45 ? 1 : 0) : (_rng.NextDouble() < 0.65 ? 1 : 0);
            for (int c = 0; c < children; c++) StartPulse(p.B, e, p.A);
        }
    }

    // ───────────────────────────── Dendriten ─────────────────────────────

    Dendrite SpawnDendrite()
    {
        double W = _size.Width, H = _size.Height;
        int edge = _rng.Next(4);
        double along = 0.12 + _rng.NextDouble() * 0.76;
        Point root;
        double heading;
        switch (edge)
        {
            case 0: root = new Point(W * along, -6); heading = Math.PI / 2; break;
            case 1: root = new Point(W + 6, H * along); heading = Math.PI; break;
            case 2: root = new Point(W * along, H + 6); heading = -Math.PI / 2; break;
            default: root = new Point(-6, H * along); heading = 0; break;
        }
        heading += (_rng.NextDouble() - 0.5) * 0.9;
        var d = new Dendrite
        {
            Speed = (Level == AnimationLevel.Subtle ? 14 : 19) + _rng.NextDouble() * 9,
            HoldFor = 9 + _rng.NextDouble() * 8,
            Alpha = 0,
        };
        var b = new Branch
        {
            Tip = root,
            Heading = heading,
            Width = 1.5,
            MaxLength = Math.Min(W, H) * (0.3 + _rng.NextDouble() * 0.28),
        };
        b.Pts.Add(root);
        d.Branches.Add(b);
        _dendrites.Add(d);
        return d;
    }

    void UpdateDendrites(double dt)
    {
        _dendriteCooldown -= dt;
        int alive = 0;
        foreach (var d in _dendrites) if (d.Phase < 2) alive++;
        if (alive < MaxDendrites && _dendriteCooldown <= 0)
        {
            SpawnDendrite();
            _dendriteCooldown = 5 + _rng.NextDouble() * 5;
        }

        for (int i = _dendrites.Count - 1; i >= 0; i--)
        {
            var d = _dendrites[i];
            GrowDendrite(d, dt);
            if (d.Phase == 2 && d.Alpha <= 0.001) _dendrites.RemoveAt(i);
        }
    }

    void UpdateDendritePulses(double dt)
    {
        for (int i = _dpulses.Count - 1; i >= 0; i--)
        {
            var p = _dpulses[i];
            p.S += 150 * dt;
            if (p.S < p.B.Length) continue;
            _dpulses.RemoveAt(i);
            // am Ende des Dendriten: nächstes Neuron feuern lassen
            var tip = p.B.Tip;
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < _nodes.Length; k++)
            {
                double dx = _nodes[k].X - tip.X, dy = _nodes[k].Y - tip.Y;
                double dd = dx * dx + dy * dy;
                if (dd < bestD) { bestD = dd; best = k; }
            }
            if (best >= 0)
            {
                _nodes[best].Act = 1;
                StartPulse(best, 1);
                StartPulse(best, 0.9);
            }
        }
    }

    void GrowDendrite(Dendrite d, double dt)
    {
        d.PhaseTime += dt;
        double W = _size.Width, H = _size.Height;
        switch (d.Phase)
        {
            case 0:
                d.Alpha = Math.Min(1, d.Alpha + dt / 2.5);
                bool any = false;
                for (int bi = 0; bi < d.Branches.Count; bi++)
                {
                    var b = d.Branches[bi];
                    if (!b.Growing) continue;
                    any = true;
                    b.Turn += (_rng.NextDouble() - 0.5) * 2.2 * dt;
                    b.Turn *= Math.Exp(-dt * 1.2);
                    b.Heading += b.Turn * dt * 1.6;
                    double step = d.Speed * dt * (1 - 0.18 * b.Depth);
                    b.Tip = new Point(b.Tip.X + Math.Cos(b.Heading) * step, b.Tip.Y + Math.Sin(b.Heading) * step);
                    b.Length += step;
                    var last = b.Pts[^1];
                    if ((b.Tip - last).Length >= 5) b.Pts.Add(b.Tip);
                    b.SinceBranch += dt;
                    if (b.Depth < 3 && b.SinceBranch > 2.2 && _rng.NextDouble() < dt * 0.45 && d.Branches.Count < 14)
                    {
                        b.SinceBranch = 0;
                        double side = _rng.NextDouble() < 0.5 ? -1 : 1;
                        var child = new Branch
                        {
                            Tip = b.Tip,
                            Heading = b.Heading + side * (0.45 + _rng.NextDouble() * 0.5),
                            Width = b.Width * 0.68,
                            Depth = b.Depth + 1,
                            MaxLength = Math.Max(20, (b.MaxLength - b.Length) * (0.45 + _rng.NextDouble() * 0.35)),
                        };
                        child.Pts.Add(b.Tip);
                        d.Branches.Add(child);
                    }
                    bool outside = b.Tip.X < -30 || b.Tip.Y < -30 || b.Tip.X > W + 30 || b.Tip.Y > H + 30;
                    if (b.Length >= b.MaxLength || outside && b.Length > 40)
                    {
                        b.Growing = false;
                        if ((b.Tip - b.Pts[^1]).Length > 0.5) b.Pts.Add(b.Tip);
                    }
                }
                if (!any) { d.Phase = 1; d.PhaseTime = 0; }
                break;
            case 1:
                if (d.PhaseTime > d.HoldFor) { d.Phase = 2; d.PhaseTime = 0; }
                break;
            case 2:
                d.Alpha = Math.Max(0, d.Alpha - dt / 6);
                break;
        }
    }

    void UpdateRipples(double dt)
    {
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var r = _ripples[i];
            r.R += dt * (170 + r.R * 0.35);
            double strength = 1 - r.R / r.Max;
            if (strength <= 0) { _ripples.RemoveAt(i); continue; }
            foreach (var n in _nodes)
            {
                double dx = n.X - r.C.X, dy = n.Y - r.C.Y;
                double dd = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r.R);
                if (dd < 24) n.Act = Math.Max(n.Act, 0.75 * strength * (1 - dd / 24));
            }
        }
    }

    // ───────────────────────────── Zeichnen ─────────────────────────────

    // Pinsel, Stifte und Glows bleiben über viele Bilder gleich (WPF muss sie dann nicht neu aufbauen).
    // Erst wenn sich die Farbstimmung ändert, wird der Cache geleert.
    readonly Dictionary<int, Pen> _pens = new();
    readonly Dictionary<int, Brush> _solids = new();
    readonly Dictionary<int, Brush> _glows = new();
    readonly Dictionary<int, List<(Point, Point)>> _lineBatches = new();
    (Color, Color, Color) _cacheColors;

    Color ColorOf(int key) => key switch { 2 or 4 or 6 => _secondary, 3 or 7 => _highlight, _ => _primary };

    void ValidateCaches()
    {
        var now = (_primary, _secondary, _highlight);
        if (now == _cacheColors) return;
        _cacheColors = now;
        _pens.Clear();
        _solids.Clear();
        _glows.Clear();
    }

    Pen PenFor(int colorKey, double alpha, int widthKey)
    {
        int a = (int)Math.Clamp(alpha * 40, 0, 40);
        int key = colorKey * 10000 + widthKey * 100 + a;
        if (!_pens.TryGetValue(key, out var p))
        {
            var b = new SolidColorBrush(MoodPalette.WithAlpha(ColorOf(colorKey), a / 40.0));
            b.Freeze();
            double w = widthKey switch { 0 => 0.75, 1 => 1.25, 2 => 1.5, 3 => 1.02, 4 => 0.7, 5 => 0.5, 6 => 2.2, 7 => 14, _ => 1.0 };
            p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            p.Freeze();
            _pens[key] = p;
        }
        return p;
    }

    Brush SolidFor(int colorKey, double alpha)
    {
        int a = (int)Math.Clamp(alpha * 32, 0, 32);
        int key = colorKey * 100 + a;
        if (!_solids.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(MoodPalette.WithAlpha(ColorOf(colorKey), a / 32.0));
            b.Freeze();
            _solids[key] = b;
        }
        return b;
    }

    /// <summary>Weicher Lichthof; Deckkraft ist in den Verlauf eingebacken (keine PushOpacity nötig).</summary>
    Brush GlowFor(int colorKey, double alpha)
    {
        int a = (int)Math.Clamp(alpha * 24, 0, 24);
        int key = colorKey * 100 + a;
        if (!_glows.TryGetValue(key, out var b))
        {
            var c = ColorOf(colorKey);
            double ca = a / 24.0;
            var g = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(MoodPalette.WithAlpha(c, ca), 0),
                    new GradientStop(MoodPalette.WithAlpha(c, ca * 0.35), 0.3),
                    new GradientStop(MoodPalette.WithAlpha(c, 0), 1),
                },
            };
            g.Freeze();
            b = g;
            _glows[key] = b;
        }
        return b;
    }

    static readonly string Perf = Environment.GetEnvironmentVariable("AXON_PERF") ?? "";

    void DrawBackground()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _bgVisual.RenderOpen();
        dc.DrawRectangle(BaseBrush, null, new Rect(0, 0, W, H));
        if (Perf.Contains("nonebula")) return;
        // langsam wandernde Nebel
        double big = Math.Max(W, H);
        for (int i = 0; i < 3; i++)
        {
            double cx = W * (0.5 + 0.38 * Math.Sin(_t * (0.021 + i * 0.007) + i * 2.1));
            double cy = H * (0.5 + 0.34 * Math.Cos(_t * (0.017 + i * 0.006) + i * 1.3));
            dc.DrawEllipse(GlowFor(i == 1 ? 2 : 1, i == 1 ? 0.06 : 0.075), null, new Point(cx, cy), big * 0.55, big * 0.45);
        }
    }

    void Draw(double dt)
    {
        ValidateCaches();
        double W = _size.Width, H = _size.Height;

        _bgAcc += dt;
        if (_bgAcc >= 0.1)
        {
            _bgAcc = 0;
            DrawBackground();
        }

        using var dc = _visual.RenderOpen();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

        // Dendriten (in der Startanimation erst nach den Neuronen)
        double dendIntro = IntroRunning ? Smooth((_introT - 1.3) / 1.0) : 1;
        foreach (var d in _dendrites)
        {
            double dAlpha = d.Alpha * dendIntro;
            if (dAlpha <= 0.01 || Perf.Contains("nodend")) continue;
            foreach (var b in d.Branches)
            {
                if (b.Pts.Count < 2 && !b.Growing) continue;
                int count = b.Pts.Count + (b.Growing ? 1 : 0);
                if (b.Geo == null || b.GeoCount != count || b.Growing)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(b.Pts[0], false, false);
                        var pts = new List<Point>(b.Pts.Count);
                        for (int k = 1; k < b.Pts.Count; k++) pts.Add(b.Pts[k]);
                        if (b.Growing) pts.Add(b.Tip);
                        ctx.PolyLineTo(pts, true, true);
                    }
                    g.Freeze();
                    b.Geo = g;
                    b.GeoCount = count;
                }
                double a = dAlpha * (0.30 - 0.05 * b.Depth);
                dc.DrawGeometry(null, PenFor(1, a, 2 + Math.Min(b.Depth, 3)), b.Geo);
                if (b.Growing) dc.DrawEllipse(GlowFor(3, 0.55 * dAlpha), null, b.Tip, 5, 5);
                else if (b.Depth > 0 || b.Pts.Count > 3) dc.DrawEllipse(SolidFor(1, 0.35 * dAlpha), null, b.Pts[^1], 1.4, 1.4);
            }
        }

        // Verbindungen – nach Stift gebündelt, damit WPF wenige große statt hunderter kleiner Zeichenbefehle bekommt
        foreach (var list in _lineBatches.Values) list.Clear();
        double md = MaxDist, md2 = md * md;
        for (int i = 0; i < (Perf.Contains("nolines") ? 0 : _nodes.Length); i++)
        {
            var a = _nodes[i];
            for (int j = i + 1; j < _nodes.Length; j++)
            {
                var b = _nodes[j];
                double dx = a.X - b.X, dy = a.Y - b.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 >= md2) continue;
                double f = 1 - Math.Sqrt(d2) / md;
                double alpha = f * f * (0.13 + 0.27 * Math.Min(a.Z, b.Z)) + 0.5 * Math.Max(a.Act, b.Act) * f;
                var p1 = new Point(a.X, a.Y);
                var p2 = new Point(b.X, b.Y);
                if (IntroRunning)
                {
                    // Startanimation: die Verbindung wächst vom früher erwachten zum später erwachten Neuron
                    double grow = Smooth((_introT - Math.Max(a.Appear, b.Appear) - 0.1) / 0.55);
                    if (grow <= 0) continue;
                    if (a.Appear > b.Appear) (p1, p2) = (p2, p1);
                    p2 = new Point(p1.X + (p2.X - p1.X) * grow, p1.Y + (p2.Y - p1.Y) * grow);
                    alpha *= 0.4 + 0.6 * grow;
                }
                if (alpha < 0.012) continue;
                bool tint = a.Tint + b.Tint >= 2;
                int key = (tint ? 2 : 1) * 10000 + (0.6 + Math.Min(a.Z, b.Z) > 1.05 ? 1 : 0) * 100 + (int)Math.Clamp(alpha * 40, 0, 40);
                if (!_lineBatches.TryGetValue(key, out var batch)) _lineBatches[key] = batch = new List<(Point, Point)>();
                batch.Add((p1, p2));
            }
        }
        foreach (var (key, batch) in _lineBatches)
        {
            if (batch.Count == 0) continue;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                foreach (var (p1, p2) in batch)
                {
                    ctx.BeginFigure(p1, false, false);
                    ctx.LineTo(p2, true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, PenFor(key / 10000, (key % 100) / 40.0, key / 100 % 100), g);
        }

        // Neuronen
        foreach (var n in _nodes)
        {
            double v = Visible(n);
            if (v <= 0) continue;
            // beim Erwachen kurz überschwingen, dann auf Normalgröße
            double pop = v >= 1 ? 1 : 1 + 0.6 * Math.Sin(v * Math.PI) * (1 - v * 0.5);
            double breath = 0.78 + 0.22 * Math.Sin(_t * n.BreathF + n.Breath);
            double glowA = Math.Min(1, (0.10 + 0.20 * n.Z) * breath + 0.7 * n.Act) * v;
            double gr = n.R * (4.5 + 5.5 * n.Act) * pop;
            double rr = n.R * v * pop;
            var center = new Point(n.X, n.Y);
            if (!Perf.Contains("noglow")) dc.DrawEllipse(GlowFor(n.Tint > 0 ? 2 : 1, glowA * 0.9), null, center, gr, gr);
            dc.DrawEllipse(SolidFor(n.Tint > 0 ? 6 : 5, (0.35 + 0.45 * n.Z * breath) * v), null, center, rr, rr);
            if (n.Act > 0.04)
            {
                double r = rr * (1 + 0.7 * n.Act);
                dc.DrawEllipse(SolidFor(7, n.Act * v), null, center, r, r);
            }
        }

        dc.Pop();
    }

    /// <summary>Schnelle Ebene, jedes Bild: Impulse mit Leuchtspur, Signale auf Dendriten, Wellen.
    /// Pro Impuls nur vier Zeichenbefehle, damit der Bildtakt bezahlbar bleibt.</summary>
    void DrawFast()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _fastVisual.RenderOpen();
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

        foreach (var p in _pulses)
        {
            var a = _nodes[p.A];
            var b = _nodes[p.B];
            double t = Math.Clamp(p.P, 0, 1);
            Point At(double u) => new(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u);
            var head = At(t);
            int key = p.Alt ? 4 : 3;
            // leuchtende Spur: schwach über die ganze Strecke, kräftiger kurz vor dem Kopf
            dc.DrawLine(PenFor(key, 0.30 * p.Energy, 1), new Point(a.X, a.Y), head);
            dc.DrawLine(PenFor(key, 0.45 * p.Energy, 6), At(Math.Max(0, t - 0.16)), head);
            dc.DrawLine(PenFor(key, 0.85 * p.Energy, 6), At(Math.Max(0, t - 0.05)), head);
            dc.DrawEllipse(GlowFor(key, Math.Min(1, 0.35 + 0.65 * p.Energy)), null, head, 5 + 6 * p.Energy, 5 + 6 * p.Energy);
        }

        foreach (var p in _dpulses)
            dc.DrawEllipse(GlowFor(3, 0.9 * p.D.Alpha), null, PointOnPolyline(p.B.Pts, p.S), 9, 9);

        foreach (var r in _ripples)
        {
            double s = 1 - r.R / r.Max;
            dc.DrawEllipse(null, PenFor(3, 0.28 * s, 3), r.C, r.R, r.R);
            dc.DrawEllipse(null, PenFor(1, 0.07 * s, 7), r.C, r.R, r.R);
        }

        dc.Pop();
    }

    static Point PointOnPolyline(List<Point> pts, double s)
    {
        if (pts.Count == 0) return default;
        for (int i = 1; i < pts.Count; i++)
        {
            double seg = (pts[i] - pts[i - 1]).Length;
            if (s <= seg && seg > 0)
            {
                double t = s / seg;
                return new Point(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * t, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * t);
            }
            s -= seg;
        }
        return pts[^1];
    }
}
