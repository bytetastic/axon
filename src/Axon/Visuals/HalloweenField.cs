using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Hintergrund des Halloween-Themas: eine Mondnacht über einem Friedhof. Irrlichter schweben und werfen sich
/// Seelenfunken zu, die Kaskaden auslösen; dornige Ranken wachsen von den Rändern herein; Kürbisse flackern,
/// Glut steigt auf, Fledermäuse ziehen vorbei, eine Spinne hängt an ihrem Netz.
/// Aufbau und Takt wie beim <see cref="NeuralField"/>: träge Ebenen zwischengespeichert, nur Schnelles im Bildtakt.
/// </summary>
public sealed class HalloweenField : Backdrop
{
    sealed class Wisp
    {
        public double NX, NY, Z, R, Amp, F1, F2, F3, F4, P1, P2, P3, P4, Fl1, Fl2, FlP1, FlP2, Tint;
        public double X, Y, Act, Flicker;
        /// <summary>Ausweichen vor dem Mauszeiger (weich nachgeführt).</summary>
        public double OX, OY;
        public double Appear;
        public bool Shown;
    }

    sealed class Spark
    {
        public int A, B;
        public double P, Speed, Energy, Bend;
        public bool Alt;
    }

    sealed class Branch
    {
        public readonly List<Point> Pts = new();
        /// <summary>Dornen als Dreiecke (je drei Punkte).</summary>
        public readonly List<Point> Thorns = new();
        public Point Tip;
        public double Heading, Turn, Width, Length, MaxLength, SinceBranch, SinceThorn;
        public int Depth, ThornSide = 1;
        public bool Growing = true;
        public StreamGeometry? Geo, ThornGeo;
        public int GeoCount, ThornCount;
    }

    sealed class Vine
    {
        public readonly List<Branch> Branches = new();
        public int Phase; // 0 wächst, 1 hält, 2 vergeht
        public double PhaseTime, HoldFor, Alpha, Speed;
    }

    sealed class VPulse
    {
        public Vine V = null!;
        public Branch B = null!;
        public double S;
    }

    sealed class Ember
    {
        public double X, Y, VX, VY, Life, Age, Size, Sway, SwayF;
        public bool Alt;
    }

    sealed class Bat
    {
        public double X, Y, Speed, Dir, BaseY, BobAmp, BobF, BobP, Flap, FlapF, Size;
    }

    sealed class Ghost
    {
        public double X, Y0, Age, Life, Sway, Size;
    }

    sealed class Wave
    {
        public Point C;
        public double R, Max;
    }

    sealed class Pumpkin
    {
        public Point C;
        public double S, F1, F2, F3, P1, P2, P3;
        public Geometry Body = null!, Ridges = null!, Face = null!, Stem = null!;
    }

    readonly Random _rng = new();
    Wisp[] _wisps = Array.Empty<Wisp>();
    List<int>[] _nbrs = Array.Empty<List<int>>();
    readonly List<Spark> _sparks = new();
    readonly List<Vine> _vines = new();
    readonly List<VPulse> _vpulses = new();
    readonly List<Ember> _embers = new();
    readonly List<Bat> _bats = new();
    readonly List<Ghost> _ghosts = new();
    readonly List<Wave> _ripples = new();
    readonly List<Pumpkin> _pumpkins = new();
    (double nx, double ny, double z, double p, double f)[] _stars = Array.Empty<(double, double, double, double, double)>();

    double _t, _spawnAcc, _emberAcc, _nbrAcc, _vineCooldown, _batTimer = 4;
    double _initArea;
    Size _size;
    bool _subscribed;

    Color _primary, _secondary, _highlight;
    /// <summary>0 = blasser Mond, 1 = Blutmond (bei Fehlern).</summary>
    double _blood;
    double _lit = 1, _litFlash;
    Point _parallax, _parallaxTarget;
    Point _pointer = new(double.NaN, double.NaN);
    Point _focus = new(double.NaN, double.NaN);
    double _spiderLen = 46, _spiderVel;

    // Ebenen (von unten nach oben):
    //   Himmel    – Verlauf, Sterne, Mond; zwischengespeichert, 10× pro Sekunde neu
    //   Szene     – Hügel, Gräber, Zaun, toter Baum, Kürbiskörper, Spinnennetz; nur bei Größenänderung neu,
    //               Parallaxe per Transform
    //   Dunst     – Nebelschwaden, Mondhof, Wolken, Bodennebel; 10× pro Sekunde, in halber Auflösung gerastert
    //               (weich, man sieht es nicht – kostet aber nur ein Viertel)
    //   Belebtes  – Ranken, Irrlichter, Kürbislicht, Glut, Spinne, Geister; zwischengespeichert, langsamer Takt
    //   Schnelles – Funken, Fledermäuse, Wellen; jedes Bild, je eine eigene kleine Ebene
    //   Vignette  – nur bei Größenänderung
    readonly CachedVisual _skyVisual = new();
    readonly CachedVisual _mistVisual = new(0.5);
    readonly CachedVisual _sceneVisual = new();
    readonly CachedVisual _visual = new();
    readonly FastLayer _sparkLayer = new(), _batLayer = new(), _waveLayer = new();
    FastLayer[] FastLayers => new[] { _sparkLayer, _batLayer, _waveLayer };
    readonly List<Visual> _children = new();
    readonly CachedVisual _vigVisual = new();
    readonly TranslateTransform _groundShift = new();
    double _skyAcc = 1;

    static readonly Color StarColor = Color.FromRgb(0xFF, 0xF2, 0xDC);
    static readonly Color CloudColor = Color.FromRgb(0x1A, 0x10, 0x2A);
    static readonly Color FogColor = Color.FromRgb(0x7A, 0x6A, 0x96);
    static readonly Color GhostColor = Color.FromRgb(0xF2, 0xEA, 0xFF);
    static readonly Color ThreadColor = Color.FromRgb(0xD8, 0xC8, 0xE8);
    static readonly Color BatColor = Color.FromRgb(0x2C, 0x1B, 0x3A);
    static readonly Color SpiderColor = Color.FromRgb(0x2A, 0x18, 0x30);
    static readonly Color VineColor = Color.FromRgb(0x0C, 0x06, 0x12);
    static readonly Color PumpkinDark = Color.FromRgb(0x2E, 0x13, 0x06);
    static readonly Color StemColor = Color.FromRgb(0x22, 0x2A, 0x0E);
    static readonly Color MoonPale = Color.FromRgb(0xF6, 0xE2, 0xB8);
    static readonly Color MoonBlood = Color.FromRgb(0xE8, 0x3A, 0x30);

    static readonly Brush SkyBrush = Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x07, 0x04, 0x0C), 0),
            new GradientStop(Color.FromRgb(0x0E, 0x08, 0x18), 0.55),
            new GradientStop(Color.FromRgb(0x1E, 0x11, 0x28), 1),
        },
    });

    static readonly Brush FarHillBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x12, 0x0A, 0x1C)));
    static readonly Brush NearGroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x03, 0x09)));

    static readonly Brush Vignette = Frozen(new RadialGradientBrush
    {
        RadiusX = 0.75,
        RadiusY = 0.75,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.5),
            new GradientStop(Color.FromArgb(140, 0, 0, 0), 1),
        },
    });

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    /// <summary>Flügelschlag in 16 Stufen, einmal berechnet (Spannweite 2, Mitte im Ursprung).</summary>
    static readonly Geometry[] BatFrames = BuildBatFrames();

    public HalloweenField()
    {
        _children.AddRange(new Visual[] { _skyVisual, _sceneVisual, _mistVisual, _visual });
        foreach (var l in FastLayers) _children.Add(l.Visual);
        _children.Add(_vigVisual);
        foreach (var v in _children) AddVisualChild(v);
        _sceneVisual.Transform = _groundShift;
        IsHitTestVisible = false;
        (_primary, _secondary, _highlight) = MoodPalette.For(Mood.Calm);
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => SetSubscribed(false);
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

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
            if (value == Mood.Done && old != Mood.Done) { Ripple(); SpawnGhost(); }
            if (value == Mood.Working && old != Mood.Working) { Burst(4); _batTimer = Math.Min(_batTimer, 1.5); }
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
    // Dunkel · der Himmel dämmert, der Mond steigt auf · Silhouetten tauchen auf · Irrlichter entzünden sich
    // von der Mitte nach außen · ≈ 2 s die Kürbisse flammen auf, Fledermäuse stieben aus dem Mond · ab 2,8 s Normalbetrieb.

    const double IgniteAt = 1.95, IntroEnd = 2.8;
    double _introT = -1, _introSpeed = 1;
    bool _ignited;

    public override bool IntroRunning => _introT >= 0;

    public override void PlayIntro(double speed = 1)
    {
        if (Level == AnimationLevel.Off) return;
        _introT = 0;
        _introSpeed = speed;
        _ignited = false;
        _sparks.Clear();
        _vpulses.Clear();
        _ripples.Clear();
        _embers.Clear();
        _bats.Clear();
        _lit = 0;
        foreach (var w in _wisps) w.Shown = false;
        _skyVisual.Opacity = _mistVisual.Opacity = 0;
        _sceneVisual.Opacity = 0;
    }

    static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    double Visible(Wisp w) => _introT < 0 ? 1 : Smooth((_introT - w.Appear) / 0.4);

    void StepIntro(double dt)
    {
        _introT += dt * _introSpeed;
        foreach (var w in _wisps)
            if (!w.Shown && _introT >= w.Appear)
            {
                w.Shown = true;
                w.Act = Math.Max(w.Act, 0.9); // kurzes Aufflammen beim Entzünden
            }
        double sky = Smooth(_introT / 1.4);
        if (Math.Abs(_skyVisual.Opacity - sky) > 0.002) _skyVisual.Opacity = _mistVisual.Opacity = sky;
        double scene = Smooth((_introT - 0.5) / 1.0);
        if (Math.Abs(_sceneVisual.Opacity - scene) > 0.002) _sceneVisual.Opacity = scene;
        if (!_ignited && _introT >= IgniteAt)
        {
            _ignited = true;
            _lit = 1;
            _litFlash = 1;
            var keep = _focus;
            _focus = new Point(_size.Width / 2, _size.Height * 0.45);
            Ripple();
            _focus = keep;
            Burst(7);
            // Fledermäuse stieben aus dem Mond
            var moon = MoonCenter();
            for (int i = 0; i < 6; i++)
            {
                double dir = i % 2 == 0 ? -1 : 1;
                _bats.Add(new Bat
                {
                    X = moon.X + (_rng.NextDouble() - 0.5) * 30, BaseY = moon.Y + (_rng.NextDouble() - 0.5) * 40,
                    Dir = dir, Speed = 150 + _rng.NextDouble() * 90, BobAmp = 18 + _rng.NextDouble() * 22,
                    BobF = 2.2 + _rng.NextDouble() * 1.4, BobP = _rng.NextDouble() * 6.3,
                    FlapF = 9 + _rng.NextDouble() * 3, Flap = _rng.NextDouble() * 6.3, Size = 12 + _rng.NextDouble() * 9,
                });
            }
            foreach (var p in _pumpkins) SpawnEmbers(p.C, 4);
            RaiseIgnited();
        }
        if (_introT >= IntroEnd)
        {
            _introT = -1;
            _skyVisual.Opacity = _mistVisual.Opacity = 1;
            _sceneVisual.Opacity = 1;
        }
    }

    public override void SetFocusPoint(Point p) => _focus = p;

    public override void SetPointer(Point p)
    {
        if (_size.Width <= 0) return;
        _pointer = p;
        _parallaxTarget = new Point((p.X / _size.Width - 0.5) * -2, (p.Y / _size.Height - 0.5) * -2);
    }

    /// <summary>Benutzer hat etwas abgeschickt: Glut wandert die Ranken entlang, die Irrlichter flackern auf, die Kürbisse sprühen Funken.</summary>
    public override void FireInput()
    {
        if (Level == AnimationLevel.Off) return;
        foreach (var v in _vines)
            if (v.Branches.Count > 0 && v.Branches[0].Pts.Count > 2 && v.Phase < 2)
                _vpulses.Add(new VPulse { V = v, B = v.Branches[0], S = 0 });
        foreach (var w in _wisps) w.Act = Math.Max(w.Act, 0.25 + _rng.NextDouble() * 0.35);
        foreach (var p in _pumpkins) SpawnEmbers(p.C, 3);
        _litFlash = Math.Max(_litFlash, 0.6);
        Burst(3);
    }

    public void Ripple()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        var c = double.IsNaN(_focus.X) ? new Point(_size.Width / 2, _size.Height / 2) : _focus;
        double max = Math.Sqrt(_size.Width * _size.Width + _size.Height * _size.Height) * 0.75;
        _ripples.Add(new Wave { C = c, R = 0, Max = max });
    }

    /// <summary>Neuer Tab: eine neue Ranke wächst herein.</summary>
    public override void GrowDendrite()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        if (_vines.Count >= MaxVines + 1) return;
        SpawnVine();
    }

    void Burst(int count)
    {
        if (_wisps.Length == 0) return;
        for (int i = 0; i < count; i++) StartSpark(_rng.Next(_wisps.Length), 1.0);
    }

    /// <summary>„Fertig“: ein Geist steigt vom Boden auf und verweht.</summary>
    void SpawnGhost()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0 || _ghosts.Count >= 3) return;
        double x = _size.Width * (0.1 + _rng.NextDouble() * 0.8);
        _ghosts.Add(new Ghost { X = x, Y0 = GroundY(x) - 10, Life = 5.5, Sway = _rng.NextDouble() * 6.3, Size = 15 + _rng.NextDouble() * 6 });
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
            ClearFast();
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _size = sizeInfo.NewSize;
        using (var dc = _vigVisual.RenderOpen())
            dc.DrawRectangle(Vignette, null, new Rect(0, 0, _size.Width, _size.Height));
        BuildScene();
        _skyAcc = 1;
        if (Level == AnimationLevel.Off) Redraw();
    }

    void Redraw()
    {
        if (_size.Width <= 0) return;
        EnsureWisps();
        Step(0);
        _skyAcc = 1;
        Draw(0);
        ClearFast();
    }

    void OnSlow(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        if (IntroRunning) return; // während der Startanimation zeichnet der Bildtakt alles
        EnsureWisps();
        Step(dt);
        Draw(dt);
    }

    void OnFrame(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        if (IntroRunning)
        {
            EnsureWisps();
            StepIntro(dt);
            Step(dt);
            _skyAcc = 1; // Mond steigt auf – Himmel jedes Bild
            Draw(dt);
        }
        if (_wisps.Length == 0) return;
        StepFast(dt);
        DrawFast();
    }

    int MaxVines => Level == AnimationLevel.Subtle ? 2 : 3;

    void EnsureWisps()
    {
        double area = _size.Width * _size.Height;
        if (_wisps.Length > 0 && area > _initArea * 0.5 && area < _initArea * 2) return;
        _initArea = area;
        double density = Level == AnimationLevel.Subtle ? 36000 : 24000;
        int count = (int)Math.Clamp(area / density, 16, 64);
        var list = new List<Wisp>(count);
        for (int i = 0; i < count; i++)
        {
            double bx = 0, by = 0, best = -1;
            for (int k = 0; k < 10; k++)
            {
                double x = -0.02 + _rng.NextDouble() * 1.04, y = 0.04 + _rng.NextDouble() * 0.82;
                double dmin = double.MaxValue;
                foreach (var n in list)
                {
                    double dx = (n.NX - x) * _size.Width, dy = (n.NY - y) * _size.Height;
                    dmin = Math.Min(dmin, dx * dx + dy * dy);
                }
                if (dmin > best) { best = dmin; bx = x; by = y; }
            }
            double z = 0.25 + Math.Pow(_rng.NextDouble(), 1.4) * 0.75;
            list.Add(new Wisp
            {
                NX = bx, NY = by, Z = z,
                R = 1.4 + z * 2.2,
                Amp = 16 + _rng.NextDouble() * 38,
                F1 = Freq(24, 60), F2 = Freq(16, 40), F3 = Freq(26, 64), F4 = Freq(18, 44),
                P1 = Ph(), P2 = Ph(), P3 = Ph(), P4 = Ph(),
                // Flackern wie eine Kerzenflamme: zwei unruhige Schwingungen
                Fl1 = 2 * Math.PI * (1.6 + _rng.NextDouble() * 1.8), Fl2 = 2 * Math.PI * (4.5 + _rng.NextDouble() * 3.5),
                FlP1 = Ph(), FlP2 = Ph(),
                Tint = _rng.NextDouble() < 0.3 ? 1 : 0,
            });
        }
        foreach (var w in list)
        {
            double dx = w.NX - 0.5, dy = (w.NY - 0.45) * _size.Height / Math.Max(1, _size.Width);
            double d = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / 0.62);
            w.Appear = 0.3 + 1.15 * d + _rng.NextDouble() * 0.16;
        }
        _wisps = list.ToArray();
        _nbrs = new List<int>[_wisps.Length];
        for (int i = 0; i < _nbrs.Length; i++) _nbrs[i] = new List<int>();
        _sparks.Clear();
        _nbrAcc = 999;

        // Sterne: nur in der oberen Himmelshälfte, ein paar heller
        int stars = (int)Math.Clamp(area / 9000, 40, 160);
        _stars = new (double, double, double, double, double)[stars];
        for (int i = 0; i < stars; i++)
            _stars[i] = (_rng.NextDouble(), Math.Pow(_rng.NextDouble(), 1.5) * 0.62, 0.3 + Math.Pow(_rng.NextDouble(), 3) * 0.7, Ph(), Freq(2.5, 9));

        if (_vines.Count == 0 && Level != AnimationLevel.Off)
        {
            for (int i = 0; i < MaxVines; i++)
            {
                var v = SpawnVine();
                double pre = 4 + _rng.NextDouble() * 14;
                for (double s = 0; s < pre; s += 0.1) GrowVine(v, 0.1);
            }
        }
    }

    double Freq(double minPeriod, double maxPeriod) => Math.PI * 2 / (minPeriod + _rng.NextDouble() * (maxPeriod - minPeriod));
    double Ph() => _rng.NextDouble() * Math.PI * 2;

    double MaxDist => Math.Clamp(Math.Sqrt(_size.Width * _size.Height / Math.Max(1, _wisps.Length)) * 1.7, 130, 260);

    void Step(double dt)
    {
        _t += dt;

        var (tp, ts, th) = MoodPalette.For(Mood);
        double kc = 1 - Math.Exp(-dt / 0.9);
        _primary = MoodPalette.Approach(_primary, tp, kc);
        _secondary = MoodPalette.Approach(_secondary, ts, kc);
        _highlight = MoodPalette.Approach(_highlight, th, kc);
        // Blutmond langsam an- und abschwellen lassen
        double bloodTarget = Mood == Mood.Error ? 1 : 0;
        _blood += (bloodTarget - _blood) * (1 - Math.Exp(-dt / 2.2));
        if (Math.Abs(_blood - bloodTarget) < 0.004) _blood = bloodTarget;
        _litFlash *= Math.Exp(-dt / 0.7);

        if (Level == AnimationLevel.Full)
        {
            double kp = 1 - Math.Exp(-dt / 1.2);
            _parallax = new Point(_parallax.X + (_parallaxTarget.X - _parallax.X) * kp, _parallax.Y + (_parallaxTarget.Y - _parallax.Y) * kp);
            double gx = Math.Round(_parallax.X * 6), gy = Math.Round(_parallax.Y * 3);
            if (_groundShift.X != gx) _groundShift.X = gx;
            if (_groundShift.Y != gy) _groundShift.Y = gy;
        }

        double W = _size.Width, H = _size.Height;
        double decay = Math.Exp(-dt / 1.3);
        double ko = 1 - Math.Exp(-dt / 0.6);
        bool pointer = !double.IsNaN(_pointer.X) && Level == AnimationLevel.Full;
        foreach (var w in _wisps)
        {
            double ox = w.Amp * (Math.Sin(_t * w.F1 + w.P1) * 0.7 + Math.Sin(_t * w.F2 + w.P2) * 0.3);
            double oy = w.Amp * (Math.Sin(_t * w.F3 + w.P3) * 0.7 + Math.Sin(_t * w.F4 + w.P4) * 0.3);
            double bx = w.NX * W + ox + _parallax.X * 14 * w.Z;
            double by = w.NY * H + oy + _parallax.Y * 10 * w.Z;
            // Irrlichter weichen dem Mauszeiger scheu aus
            double tx = 0, ty = 0;
            if (pointer)
            {
                double dx = bx - _pointer.X, dy = by - _pointer.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 170 && d > 0.1)
                {
                    double push = 70 * Math.Pow(1 - d / 170, 2);
                    tx = dx / d * push;
                    ty = dy / d * push;
                }
            }
            w.OX += (tx - w.OX) * ko;
            w.OY += (ty - w.OY) * ko;
            w.X = bx + w.OX;
            w.Y = by + w.OY;
            w.Act *= decay;
            w.Flicker = 0.74 + 0.26 * (0.62 * Math.Sin(_t * w.Fl1 + w.FlP1) + 0.38 * Math.Sin(_t * w.Fl2 + w.FlP2));
        }

        _nbrAcc += dt;
        if (_nbrAcc > 0.5)
        {
            _nbrAcc = 0;
            double md = MaxDist * 0.92, md2 = md * md;
            for (int i = 0; i < _wisps.Length; i++) _nbrs[i].Clear();
            for (int i = 0; i < _wisps.Length; i++)
                for (int j = i + 1; j < _wisps.Length; j++)
                {
                    double dx = _wisps[i].X - _wisps[j].X, dy = _wisps[i].Y - _wisps[j].Y;
                    if (dx * dx + dy * dy < md2) { _nbrs[i].Add(j); _nbrs[j].Add(i); }
                }
        }

        if (dt <= 0) return;
        UpdateVines(dt);
    }

    void StepFast(double dt)
    {
        if (dt <= 0) return;
        bool subtle = Level == AnimationLevel.Subtle;
        if (IntroRunning && !_ignited)
        {
            UpdateRipples(dt);
            UpdateBats(dt);
            UpdateSpider(dt);
            return;
        }
        double rate = Mood switch
        {
            Mood.Working => 2.0,
            Mood.Attention => 0.6,
            Mood.Done => 0.45,
            _ => 0.3,
        };
        if (subtle) rate *= 0.5;
        _spawnAcc += dt * rate;
        while (_spawnAcc >= 1 && _wisps.Length > 0)
        {
            _spawnAcc -= 1;
            StartSpark(_rng.Next(_wisps.Length), 1.0);
        }

        // Glut aus den Kürbissen – mehr, wenn Claude arbeitet
        double emberRate = (Mood == Mood.Working ? 3.2 : 1.1) * (subtle ? 0.5 : 1);
        _emberAcc += dt * emberRate;
        while (_emberAcc >= 1 && _pumpkins.Count > 0)
        {
            _emberAcc -= 1;
            SpawnEmbers(_pumpkins[_rng.Next(_pumpkins.Count)].C, 1);
        }

        // Fledermausschwärme in unregelmäßigen Abständen
        _batTimer -= dt;
        if (_batTimer <= 0)
        {
            SpawnFlock();
            double min = Mood == Mood.Working ? 6 : 18, span = Mood == Mood.Working ? 8 : 22;
            _batTimer = (min + _rng.NextDouble() * span) * (subtle ? 1.8 : 1);
        }

        UpdateSparks(dt);
        UpdateVinePulses(dt);
        UpdateEmbers(dt);
        UpdateBats(dt);
        UpdateSpider(dt);
        UpdateGhosts(dt);
        UpdateRipples(dt);
    }

    void StartSpark(int from, double energy, int exclude = -1)
    {
        if (from < 0 || from >= _wisps.Length || _nbrs.Length == 0) return;
        var list = _nbrs[from];
        if (list.Count == 0 || _sparks.Count > 40) return;
        int to = list[_rng.Next(list.Count)];
        if (to == exclude && list.Count > 1) to = list[_rng.Next(list.Count)];
        if (to == exclude) return;
        bool working = Mood == Mood.Working;
        _sparks.Add(new Spark
        {
            A = from,
            B = to,
            Speed = working ? 115 + _rng.NextDouble() * 70 : 60 + _rng.NextDouble() * 40,
            Energy = energy,
            // Seelenfunken fliegen im Bogen, nicht geradeaus
            Bend = (_rng.NextDouble() < 0.5 ? -1 : 1) * (0.18 + _rng.NextDouble() * 0.22),
            Alt = _rng.NextDouble() < (Mood is Mood.Attention or Mood.Error ? 0.6 : 0.3),
        });
        _wisps[from].Act = Math.Max(_wisps[from].Act, energy * 0.6);
    }

    /// <summary>Punkt auf dem Bogen eines Funkens (quadratische Bézierkurve).</summary>
    Point SparkAt(Spark s, double u)
    {
        var a = _wisps[s.A];
        var b = _wisps[s.B];
        double mx = (a.X + b.X) / 2 - (b.Y - a.Y) * s.Bend, my = (a.Y + b.Y) / 2 + (b.X - a.X) * s.Bend;
        double v = 1 - u;
        return new Point(v * v * a.X + 2 * v * u * mx + u * u * b.X, v * v * a.Y + 2 * v * u * my + u * u * b.Y);
    }

    void UpdateSparks(double dt)
    {
        double keep = Mood == Mood.Working ? 0.82 : 0.7;
        for (int i = _sparks.Count - 1; i >= 0; i--)
        {
            var s = _sparks[i];
            var a = _wisps[s.A];
            var b = _wisps[s.B];
            double len = Math.Max(1, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) * (1 + Math.Abs(s.Bend) * 0.6));
            s.P += s.Speed * dt / len;
            if (s.P < 1) continue;
            _sparks.RemoveAt(i);
            b.Act = Math.Max(b.Act, s.Energy);
            double e = s.Energy * keep;
            if (e < 0.2) continue;
            int children = e > 0.6 ? 1 + (_rng.NextDouble() < 0.45 ? 1 : 0) : (_rng.NextDouble() < 0.65 ? 1 : 0);
            for (int c = 0; c < children; c++) StartSpark(s.B, e, s.A);
        }
    }

    void SpawnEmbers(Point at, int count)
    {
        for (int i = 0; i < count && _embers.Count < 30; i++)
        {
            _embers.Add(new Ember
            {
                X = at.X + (_rng.NextDouble() - 0.5) * 14 + _groundShift.X,
                Y = at.Y - 6 + _groundShift.Y,
                VX = (_rng.NextDouble() - 0.5) * 10,
                VY = -(20 + _rng.NextDouble() * 22),
                Life = 2.6 + _rng.NextDouble() * 2.4,
                Size = 0.8 + _rng.NextDouble() * 1.3,
                Sway = Ph(),
                SwayF = 1.2 + _rng.NextDouble() * 1.6,
                Alt = _rng.NextDouble() < 0.25,
            });
        }
    }

    void UpdateEmbers(double dt)
    {
        for (int i = _embers.Count - 1; i >= 0; i--)
        {
            var e = _embers[i];
            e.Age += dt;
            if (e.Age >= e.Life) { _embers.RemoveAt(i); continue; }
            e.X += (e.VX + Math.Sin(e.Age * e.SwayF + e.Sway) * 9) * dt;
            e.Y += e.VY * dt;
            e.VY *= Math.Exp(-dt * 0.25);
        }
    }

    void SpawnFlock()
    {
        if (_size.Width <= 0) return;
        int n = 1 + _rng.Next(Level == AnimationLevel.Subtle ? 3 : 5);
        double dir = _rng.NextDouble() < 0.5 ? 1 : -1;
        double y = _size.Height * (0.1 + _rng.NextDouble() * 0.42);
        double speed = 95 + _rng.NextDouble() * 60;
        for (int i = 0; i < n; i++)
        {
            double size = 11 + _rng.NextDouble() * 10;
            _bats.Add(new Bat
            {
                X = dir > 0 ? -30 - i * (22 + _rng.NextDouble() * 30) : _size.Width + 30 + i * (22 + _rng.NextDouble() * 30),
                BaseY = y + (_rng.NextDouble() - 0.5) * 70,
                Dir = dir,
                Speed = speed * (0.85 + _rng.NextDouble() * 0.3) * (0.7 + size / 30),
                BobAmp = 10 + _rng.NextDouble() * 18,
                BobF = 1.8 + _rng.NextDouble() * 1.4,
                BobP = Ph(),
                FlapF = 8 + _rng.NextDouble() * 4,
                Flap = Ph(),
                Size = size,
            });
        }
    }

    void UpdateBats(double dt)
    {
        for (int i = _bats.Count - 1; i >= 0; i--)
        {
            var b = _bats[i];
            b.X += b.Dir * b.Speed * dt;
            b.BobP += b.BobF * dt;
            b.Y = b.BaseY + Math.Sin(b.BobP) * b.BobAmp;
            b.Flap += b.FlapF * dt * Math.PI * 2;
            if (b.X < -80 || b.X > _size.Width + 80) _bats.RemoveAt(i);
        }
    }

    /// <summary>Die Spinne hängt ruhig, seilt sich ab, wenn Claude auf dich wartet, und turnt, während Claude arbeitet.</summary>
    void UpdateSpider(double dt)
    {
        double target = Mood switch
        {
            Mood.Attention => Math.Clamp(_size.Height * 0.3, 140, 260),
            Mood.Working => 70 + 45 * Math.Sin(_t * 0.45),
            Mood.Done => 28,
            Mood.Error => 90,
            _ => 46 + 6 * Math.Sin(_t * 0.6),
        };
        // gedämpfte Feder: wippt beim Anhalten leicht nach
        double k = 3.2, damping = 2.6;
        _spiderVel += ((target - _spiderLen) * k - _spiderVel * damping) * dt;
        _spiderLen += _spiderVel * dt;
    }

    void UpdateGhosts(double dt)
    {
        for (int i = _ghosts.Count - 1; i >= 0; i--)
        {
            _ghosts[i].Age += dt;
            if (_ghosts[i].Age >= _ghosts[i].Life) _ghosts.RemoveAt(i);
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
            foreach (var w in _wisps)
            {
                double dx = w.X - r.C.X, dy = w.Y - r.C.Y;
                double dd = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r.R);
                if (dd < 26) w.Act = Math.Max(w.Act, 0.75 * strength * (1 - dd / 26));
            }
        }
    }

    // ───────────────────────────── Ranken ─────────────────────────────

    Vine SpawnVine()
    {
        double W = _size.Width, H = _size.Height;
        // Ranken kommen von oben und von den Seiten – unten steht der Friedhof
        int edge = _rng.Next(3);
        double along = 0.12 + _rng.NextDouble() * 0.7;
        Point root;
        double heading;
        switch (edge)
        {
            case 0: root = new Point(W * along, -6); heading = Math.PI / 2; break;
            case 1: root = new Point(W + 6, H * along); heading = Math.PI; break;
            default: root = new Point(-6, H * along); heading = 0; break;
        }
        heading += (_rng.NextDouble() - 0.5) * 0.9;
        var v = new Vine
        {
            Speed = (Level == AnimationLevel.Subtle ? 13 : 18) + _rng.NextDouble() * 9,
            HoldFor = 10 + _rng.NextDouble() * 8,
        };
        var b = new Branch
        {
            Tip = root,
            Heading = heading,
            Width = 2.4,
            MaxLength = Math.Min(W, H) * (0.3 + _rng.NextDouble() * 0.28),
        };
        b.Pts.Add(root);
        v.Branches.Add(b);
        _vines.Add(v);
        return v;
    }

    void UpdateVines(double dt)
    {
        _vineCooldown -= dt;
        int alive = 0;
        foreach (var v in _vines) if (v.Phase < 2) alive++;
        if (alive < MaxVines && _vineCooldown <= 0)
        {
            SpawnVine();
            _vineCooldown = 5 + _rng.NextDouble() * 5;
        }
        for (int i = _vines.Count - 1; i >= 0; i--)
        {
            var v = _vines[i];
            GrowVine(v, dt);
            if (v.Phase == 2 && v.Alpha <= 0.001) _vines.RemoveAt(i);
        }
    }

    void GrowVine(Vine v, double dt)
    {
        v.PhaseTime += dt;
        double W = _size.Width, H = _size.Height;
        switch (v.Phase)
        {
            case 0:
                v.Alpha = Math.Min(1, v.Alpha + dt / 2.5);
                bool any = false;
                for (int bi = 0; bi < v.Branches.Count; bi++)
                {
                    var b = v.Branches[bi];
                    if (!b.Growing) continue;
                    any = true;
                    // Ranken winden sich stärker als Dendriten
                    b.Turn += (_rng.NextDouble() - 0.5) * 3.0 * dt;
                    b.Turn *= Math.Exp(-dt * 1.0);
                    b.Heading += b.Turn * dt * 1.9 + Math.Sin(b.Length * 0.035 + bi) * 0.35 * dt;
                    double step = v.Speed * dt * (1 - 0.18 * b.Depth);
                    b.Tip = new Point(b.Tip.X + Math.Cos(b.Heading) * step, b.Tip.Y + Math.Sin(b.Heading) * step);
                    b.Length += step;
                    var last = b.Pts[^1];
                    if ((b.Tip - last).Length >= 5) b.Pts.Add(b.Tip);
                    b.SinceThorn += step;
                    if (b.SinceThorn > 11 + b.Depth * 3)
                    {
                        b.SinceThorn = 0;
                        AddThorn(b);
                    }
                    b.SinceBranch += dt;
                    if (b.Depth < 3 && b.SinceBranch > 2.0 && _rng.NextDouble() < dt * 0.5 && v.Branches.Count < 14)
                    {
                        b.SinceBranch = 0;
                        double side = _rng.NextDouble() < 0.5 ? -1 : 1;
                        var child = new Branch
                        {
                            Tip = b.Tip,
                            Heading = b.Heading + side * (0.5 + _rng.NextDouble() * 0.55),
                            Width = b.Width * 0.66,
                            Depth = b.Depth + 1,
                            MaxLength = Math.Max(20, (b.MaxLength - b.Length) * (0.45 + _rng.NextDouble() * 0.35)),
                        };
                        child.Pts.Add(b.Tip);
                        v.Branches.Add(child);
                    }
                    bool outside = b.Tip.X < -30 || b.Tip.Y < -30 || b.Tip.X > W + 30 || b.Tip.Y > H + 30;
                    if (b.Length >= b.MaxLength || outside && b.Length > 40)
                    {
                        b.Growing = false;
                        if ((b.Tip - b.Pts[^1]).Length > 0.5) b.Pts.Add(b.Tip);
                    }
                }
                if (!any) { v.Phase = 1; v.PhaseTime = 0; }
                break;
            case 1:
                if (v.PhaseTime > v.HoldFor) { v.Phase = 2; v.PhaseTime = 0; }
                break;
            case 2:
                v.Alpha = Math.Max(0, v.Alpha - dt / 6);
                break;
        }
    }

    /// <summary>Ein Dorn an der Spitze, abwechselnd links und rechts, leicht nach vorn geneigt.</summary>
    void AddThorn(Branch b)
    {
        double len = 4 + b.Width * 1.6 + _rng.NextDouble() * 2;
        double ang = b.Heading + b.ThornSide * (1.05 + _rng.NextDouble() * 0.25);
        double half = b.Width * 0.75 + 0.6;
        var dir = new Vector(Math.Cos(b.Heading), Math.Sin(b.Heading));
        var p = b.Tip;
        b.Thorns.Add(p - dir * half);
        b.Thorns.Add(p + dir * half);
        b.Thorns.Add(new Point(p.X + Math.Cos(ang) * len + dir.X * len * 0.35, p.Y + Math.Sin(ang) * len + dir.Y * len * 0.35));
        b.ThornSide = -b.ThornSide;
    }

    void UpdateVinePulses(double dt)
    {
        for (int i = _vpulses.Count - 1; i >= 0; i--)
        {
            var p = _vpulses[i];
            p.S += 140 * dt;
            if (p.S < p.B.Length) continue;
            _vpulses.RemoveAt(i);
            // an der Spitze: das nächste Irrlicht entflammt
            var tip = p.B.Tip;
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < _wisps.Length; k++)
            {
                double dx = _wisps[k].X - tip.X, dy = _wisps[k].Y - tip.Y;
                double dd = dx * dx + dy * dy;
                if (dd < bestD) { bestD = dd; best = k; }
            }
            if (best >= 0)
            {
                _wisps[best].Act = 1;
                StartSpark(best, 1);
                StartSpark(best, 0.9);
            }
        }
    }

    // ───────────────────────────── Szene (Silhouetten) ─────────────────────────────

    double GroundY(double x)
    {
        double H = _size.Height;
        return H - Math.Max(46, H * 0.07) - 7 * Math.Sin(x * 0.006 + 0.4) - 4 * Math.Sin(x * 0.017 + 1.7);
    }

    double FarHillY(double x)
    {
        double H = _size.Height;
        return H - Math.Max(84, H * 0.135) - 16 * Math.Sin(x * 0.0038 + 1.1) - 9 * Math.Sin(x * 0.0105 + 2.3);
    }

    Point MoonCenter()
    {
        double W = _size.Width, H = _size.Height;
        double rise = IntroRunning ? (1 - Smooth(_introT / 1.8)) * 46 : 0;
        return new Point(W * 0.8 + _parallax.X * 3, H * 0.2 + rise + _parallax.Y * 2);
    }

    double MoonRadius => Math.Clamp(Math.Min(_size.Width, _size.Height) * 0.075, 30, 66);

    // Anker der Spinne: auf einer Speiche des Netzes oben links, genau über der Graph-Linie der Projektliste
    Point SpiderAnchor => new(22, 38);

    /// <summary>Silhouetten einmal pro Größe erzeugen – fester Zufall, damit die Landschaft beim Vergrößern nicht springt.</summary>
    void BuildScene()
    {
        double W = _size.Width, H = _size.Height;
        _pumpkins.Clear();
        if (W <= 0 || H <= 0) return;
        var rng = new Random(31);
        using var dc = _sceneVisual.RenderOpen();
        // etwas Rand für die Parallaxe
        double x0 = -12, x1 = W + 12;

        // ferne Hügel
        var far = new StreamGeometry();
        using (var ctx = far.Open())
        {
            ctx.BeginFigure(new Point(x0, H + 10), true, true);
            var pts = new List<Point>();
            for (double x = x0; x <= x1; x += 8) pts.Add(new Point(x, FarHillY(x)));
            pts.Add(new Point(x1, H + 10));
            ctx.PolyLineTo(pts, true, true);
        }
        far.Freeze();
        dc.DrawGeometry(FarHillBrush, null, far);

        // naher Boden mit Gräbern, Zaun und Baum – alles eine Silhouette
        var near = new StreamGeometry();
        using (var ctx = near.Open())
        {
            ctx.BeginFigure(new Point(x0, H + 10), true, true);
            var pts = new List<Point>();
            for (double x = x0; x <= x1; x += 6) pts.Add(new Point(x, GroundY(x)));
            pts.Add(new Point(x1, H + 10));
            ctx.PolyLineTo(pts, true, true);

            double treeX = W > 700 ? W * 0.925 : double.NaN;
            double fenceA = W * 0.36, fenceB = W * 0.36 + Math.Min(220, W * 0.17);

            // Grabsteine
            int stones = (int)Math.Clamp(W / 150, 4, 12);
            for (int i = 0; i < stones; i++)
            {
                double x = W * (0.03 + rng.NextDouble() * 0.94);
                if (!double.IsNaN(treeX) && Math.Abs(x - treeX) < 40) continue;
                if (x > fenceA - 10 && x < fenceB + 10) continue;
                double h = 16 + rng.NextDouble() * 16, w = 10 + rng.NextDouble() * 9;
                double tilt = (rng.NextDouble() - 0.5) * 0.2;
                Stone(ctx, new Point(x, GroundY(x) + 4), w, h, tilt, rng.Next(3));
            }

            // Eisenzaun mit Speerspitzen
            for (double x = fenceA; x <= fenceB; x += 7)
            {
                double gy = GroundY(x) + 3, top = gy - 26;
                Poly(ctx, new Point(x - 0.8, gy), new Point(x - 0.8, top), new Point(x - 2.4, top), new Point(x, top - 5),
                    new Point(x + 2.4, top), new Point(x + 0.8, top), new Point(x + 0.8, gy));
            }
            for (int r = 0; r < 2; r++)
            {
                double off = r == 0 ? 20 : 8;
                Poly(ctx, new Point(fenceA, GroundY(fenceA) + 3 - off), new Point(fenceB, GroundY(fenceB) + 3 - off),
                    new Point(fenceB, GroundY(fenceB) + 4.6 - off), new Point(fenceA, GroundY(fenceA) + 4.6 - off));
            }

            // toter Baum am rechten Rand
            if (!double.IsNaN(treeX))
                TreeBranch(ctx, rng, new Point(treeX, GroundY(treeX) + 6), -Math.PI / 2 - 0.12, Math.Max(60, H * 0.12), 13, 0);
        }
        near.Freeze();
        dc.DrawGeometry(NearGroundBrush, null, near);

        // Spinnennetz in der Ecke oben links
        var web = new StreamGeometry();
        using (var ctx = web.Open())
        {
            int spokes = 7;
            double[] rings = { 22, 38, 56, 76, 98, 122, 146 };
            var ends = new double[spokes];
            for (int s = 0; s < spokes; s++)
            {
                double a = Math.PI / 2 * s / (spokes - 1);
                ends[s] = 150 + rng.NextDouble() * 30;
                ctx.BeginFigure(new Point(0, 0), false, false);
                ctx.LineTo(new Point(Math.Cos(a) * ends[s], Math.Sin(a) * ends[s]), true, false);
            }
            foreach (double r in rings)
            {
                for (int s = 0; s < spokes - 1; s++)
                {
                    double a1 = Math.PI / 2 * s / (spokes - 1), a2 = Math.PI / 2 * (s + 1) / (spokes - 1);
                    var p1 = new Point(Math.Cos(a1) * r, Math.Sin(a1) * r);
                    var p2 = new Point(Math.Cos(a2) * r, Math.Sin(a2) * r);
                    // Fäden hängen leicht zur Ecke hin durch
                    var mid = new Point((p1.X + p2.X) / 2 * 0.93, (p1.Y + p2.Y) / 2 * 0.93);
                    ctx.BeginFigure(p1, false, false);
                    ctx.QuadraticBezierTo(mid, p2, true, false);
                }
            }
        }
        web.Freeze();
        var webPen = new Pen(new SolidColorBrush(MoodPalette.WithAlpha(ThreadColor, 0.13)), 0.75);
        webPen.Freeze();
        dc.DrawGeometry(null, webPen, web);

        // Kürbisse: einer unten links (in der Seitenleiste sichtbar), weitere verteilt
        int count = (int)Math.Clamp(W / 380, 2, 5);
        for (int i = 0; i < count; i++)
        {
            // der erste rechts neben der Seitenleiste, deren Hinweistext unten sonst über ihm läge
            double x = i == 0 ? Math.Min(300, W * 0.24) : W * (0.3 + rng.NextDouble() * 0.56);
            double s = i == 0 ? 15 : 10 + rng.NextDouble() * 7;
            var p = MakePumpkin(new Point(x, GroundY(x) + 2), s, rng);
            _pumpkins.Add(p);
            dc.DrawGeometry(PumpkinBodyBrush, null, p.Body);
            dc.DrawGeometry(PumpkinSkinBrush, null, p.Body);
            dc.DrawGeometry(null, PumpkinRidgePen, p.Ridges);
            dc.DrawGeometry(PumpkinStemBrush, null, p.Stem);
            dc.DrawGeometry(PumpkinBodyBrush, null, p.Face);
        }
    }

    static readonly Brush PumpkinBodyBrush = Frozen(new SolidColorBrush(PumpkinDark));
    static readonly Brush PumpkinSkinBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x90, 0xC8, 0x58, 0x18)));
    static readonly Brush PumpkinStemBrush = Frozen(new SolidColorBrush(StemColor));
    static readonly Pen PumpkinRidgePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x2E, 0x13, 0x06)), 1));

    static void Poly(StreamGeometryContext ctx, params Point[] pts)
    {
        ctx.BeginFigure(pts[0], true, true);
        ctx.PolyLineTo(pts[1..], true, false);
    }

    /// <summary>Grabstein: 0 = rund, 1 = Kreuz, 2 = spitz.</summary>
    static void Stone(StreamGeometryContext ctx, Point basePt, double w, double h, double tilt, int kind)
    {
        Point R(double x, double y)
        {
            double c = Math.Cos(tilt), s = Math.Sin(tilt);
            return new Point(basePt.X + x * c - y * s, basePt.Y + x * s + y * c);
        }
        switch (kind)
        {
            case 0:
                ctx.BeginFigure(R(-w / 2, 0), true, true);
                ctx.LineTo(R(-w / 2, -h + w / 2), true, false);
                ctx.ArcTo(R(w / 2, -h + w / 2), new Size(w / 2, w / 2), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(R(w / 2, 0), true, false);
                break;
            case 1:
                double t = w * 0.28;
                Poly(ctx, R(-t, 0), R(-t, -h * 0.62), R(-w / 2, -h * 0.62), R(-w / 2, -h * 0.62 - t * 2), R(-t, -h * 0.62 - t * 2),
                    R(-t, -h), R(t, -h), R(t, -h * 0.62 - t * 2), R(w / 2, -h * 0.62 - t * 2), R(w / 2, -h * 0.62), R(t, -h * 0.62), R(t, 0));
                break;
            default:
                Poly(ctx, R(-w / 2, 0), R(-w / 2, -h * 0.78), R(0, -h), R(w / 2, -h * 0.78), R(w / 2, 0));
                break;
        }
    }

    static void TreeBranch(StreamGeometryContext ctx, Random rng, Point p, double angle, double len, double width, int depth)
    {
        if (depth > 7 || width < 0.7) return;
        // krumm: Ast in zwei Abschnitten mit Knick
        double bend = (rng.NextDouble() - 0.5) * 0.5;
        var mid = new Point(p.X + Math.Cos(angle) * len * 0.5, p.Y + Math.Sin(angle) * len * 0.5);
        var end = new Point(mid.X + Math.Cos(angle + bend) * len * 0.5, mid.Y + Math.Sin(angle + bend) * len * 0.5);
        double w2 = width * 0.68;
        Taper(ctx, p, mid, width, (width + w2) / 2);
        Taper(ctx, mid, end, (width + w2) / 2, w2);
        int children = depth < 2 ? 2 : 2 + (rng.NextDouble() < 0.35 ? 1 : 0);
        for (int i = 0; i < children; i++)
        {
            double spread = (i - (children - 1) / 2.0) * (0.55 + rng.NextDouble() * 0.35) + (rng.NextDouble() - 0.5) * 0.3;
            // Äste hängen nach außen, zur Fenstermitte hin
            double lean = -0.12;
            TreeBranch(ctx, rng, end, angle + bend + spread + lean, len * (0.66 + rng.NextDouble() * 0.16), w2, depth + 1);
        }
    }

    static void Taper(StreamGeometryContext ctx, Point a, Point b, double wa, double wb)
    {
        var d = b - a;
        if (d.Length < 0.1) return;
        d.Normalize();
        var n = new Vector(-d.Y, d.X);
        Poly(ctx, a + n * wa / 2, b + n * wb / 2, b - n * wb / 2, a - n * wa / 2);
    }

    Pumpkin MakePumpkin(Point c, double s, Random rng)
    {
        // Körper aus fünf Wülsten, Mittelpunkt c liegt am Boden
        var body = new GeometryGroup { FillRule = FillRule.Nonzero };
        var ridges = new StreamGeometry();
        double h = s * 1.55, cy = c.Y - h / 2;
        double[] offs = { -0.62, -0.32, 0, 0.32, 0.62 };
        foreach (var o in offs)
        {
            double rx = s * (1.02 - Math.Abs(o) * 0.55) * 0.62;
            body.Children.Add(new EllipseGeometry(new Point(c.X + o * s, cy), rx, h / 2 * (1 - Math.Abs(o) * 0.12)));
        }
        body.Freeze();
        using (var ctx = ridges.Open())
            foreach (var o in new[] { -0.32, 0.32 })
            {
                ctx.BeginFigure(new Point(c.X + o * s * 0.7, cy - h * 0.42), false, false);
                ctx.QuadraticBezierTo(new Point(c.X + o * s * 1.25, cy), new Point(c.X + o * s * 0.7, cy + h * 0.44), true, false);
            }
        ridges.Freeze();

        // Gesicht: dreieckige Augen, Nase, gezackter Mund
        var face = new StreamGeometry();
        using (var ctx = face.Open())
        {
            double ey = cy - h * 0.1;
            Poly(ctx, new Point(c.X - s * 0.52, ey + s * 0.12), new Point(c.X - s * 0.18, ey + s * 0.12), new Point(c.X - s * 0.33, ey - s * 0.2));
            Poly(ctx, new Point(c.X + s * 0.18, ey + s * 0.12), new Point(c.X + s * 0.52, ey + s * 0.12), new Point(c.X + s * 0.33, ey - s * 0.2));
            Poly(ctx, new Point(c.X - s * 0.08, ey + s * 0.3), new Point(c.X + s * 0.08, ey + s * 0.3), new Point(c.X, ey + s * 0.16));
            double my = cy + h * 0.16;
            var mouth = new List<Point> { new(c.X - s * 0.6, my) };
            int teeth = 4;
            for (int i = 0; i <= teeth; i++)
            {
                double x = c.X - s * 0.5 + s * i / teeth;
                mouth.Add(new Point(x, my + (i % 2 == 0 ? s * 0.06 : s * 0.16)));
            }
            mouth.Add(new Point(c.X + s * 0.6, my));
            mouth.Add(new Point(c.X + s * 0.38, my + s * 0.34));
            mouth.Add(new Point(c.X - s * 0.38, my + s * 0.34));
            Poly(ctx, mouth.ToArray());
        }
        face.Freeze();

        var stem = new StreamGeometry();
        using (var ctx = stem.Open())
            Poly(ctx, new Point(c.X - s * 0.1, cy - h * 0.46), new Point(c.X - s * 0.04, cy - h * 0.78), new Point(c.X + s * 0.18, cy - h * 0.86),
                new Point(c.X + s * 0.1, cy - h * 0.46));
        stem.Freeze();

        return new Pumpkin
        {
            C = new Point(c.X, cy), S = s, Body = body, Ridges = ridges, Face = face, Stem = stem,
            F1 = 2 * Math.PI * (5 + rng.NextDouble() * 3), F2 = 2 * Math.PI * (9 + rng.NextDouble() * 5), F3 = 2 * Math.PI * (1.1 + rng.NextDouble()),
            P1 = rng.NextDouble() * 6.3, P2 = rng.NextDouble() * 6.3, P3 = rng.NextDouble() * 6.3,
        };
    }

    static Geometry[] BuildBatFrames()
    {
        var frames = new Geometry[16];
        for (int f = 0; f < 16; f++)
        {
            double phase = Math.PI * 2 * f / 16;
            double lift = -Math.Sin(phase) * 0.55; // negativ = Flügel oben
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                foreach (int side in new[] { -1, 1 })
                {
                    Point P(double x, double y) => new(x * side, y);
                    Poly(ctx,
                        P(0.1, -0.06), P(0.45, -0.22 + lift * 0.55), P(1.0, -0.16 + lift), P(0.84, 0.1 + lift * 0.75),
                        P(0.7, 0.02 + lift * 0.6), P(0.58, 0.16 + lift * 0.5), P(0.44, 0.07 + lift * 0.38), P(0.3, 0.17 + lift * 0.25),
                        P(0.12, 0.12));
                }
                // Körper mit Kopf und Ohren
                Poly(ctx, new Point(-0.12, -0.06), new Point(-0.1, -0.2), new Point(-0.13, -0.33), new Point(-0.04, -0.24),
                    new Point(0.04, -0.24), new Point(0.13, -0.33), new Point(0.1, -0.2), new Point(0.12, -0.06),
                    new Point(0.1, 0.18), new Point(0, 0.28), new Point(-0.1, 0.18));
            }
            g.Freeze();
            frames[f] = g;
        }
        return frames;
    }

    // ───────────────────────────── Zeichnen ─────────────────────────────

    // Farbschlüssel: 1 Haupt, 2 Zweit, 3 Lichter, 4 Mond, 5 Sterne, 6 Wolken, 7 Nebel, 8 Geist, 9 Faden, 10 Fledermaus,
    // 11 Spinne, 12 Ranke, 13 Kürbis dunkel, 14 Stiel
    readonly Dictionary<long, Pen> _pens = new();
    readonly Dictionary<int, Brush> _solids = new();
    readonly Dictionary<int, Brush> _glows = new();
    readonly Dictionary<int, Brush> _moonBrushes = new();
    (Color, Color, Color, Color) _cacheColors;
    Color _moon;

    Color ColorOf(int key) => key switch
    {
        2 => _secondary, 3 => _highlight, 4 => _moon, 5 => StarColor, 6 => CloudColor, 7 => FogColor, 8 => GhostColor,
        9 => ThreadColor, 10 => BatColor, 11 => SpiderColor, 12 => VineColor, 13 => PumpkinDark, 14 => StemColor,
        _ => _primary,
    };

    void ValidateCaches()
    {
        _moon = MoodPalette.Mix(MoonPale, MoonBlood, _blood);
        var now = (_primary, _secondary, _highlight, _moon);
        if (now == _cacheColors) return;
        _cacheColors = now;
        _pens.Clear();
        _solids.Clear();
        _glows.Clear();
    }

    Pen PenFor(int colorKey, double alpha, double width)
    {
        int a = (int)Math.Clamp(alpha * 40, 0, 40);
        int wq = (int)Math.Round(width * 4);
        long key = colorKey * 1_000_000L + wq * 100L + a;
        if (!_pens.TryGetValue(key, out var p))
        {
            var b = new SolidColorBrush(MoodPalette.WithAlpha(ColorOf(colorKey), a / 40.0));
            b.Freeze();
            p = new Pen(b, wq / 4.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
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

    /// <summary>Weicher Lichthof; Deckkraft ist in den Verlauf eingebacken.</summary>
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

    /// <summary>Mondscheibe: hell in der Mitte, zum Rand wärmer – nach Farbe zwischengespeichert.</summary>
    Brush MoonDisk()
    {
        int key = _moon.R << 16 | _moon.G << 8 | _moon.B;
        if (!_moonBrushes.TryGetValue(key, out var b))
        {
            if (_moonBrushes.Count > 64) _moonBrushes.Clear();
            var g = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.38, 0.36),
                Center = new Point(0.45, 0.45),
                RadiusX = 0.62,
                RadiusY = 0.62,
                GradientStops =
                {
                    new GradientStop(MoodPalette.Mix(_moon, Colors.White, 0.55), 0),
                    new GradientStop(_moon, 0.6),
                    new GradientStop(MoodPalette.Mix(_moon, Color.FromRgb(0x8A, 0x4A, 0x30), 0.35), 1),
                },
            };
            g.Freeze();
            b = g;
            _moonBrushes[key] = b;
        }
        return b;
    }

    /// <summary>Krater: etwas dunkler und wärmer als die Scheibe, kaum sichtbar.</summary>
    Brush MoonCrater()
    {
        int key = -(_moon.R << 16 | _moon.G << 8 | _moon.B) - 1;
        if (!_moonBrushes.TryGetValue(key, out var b))
        {
            b = new SolidColorBrush(MoodPalette.WithAlpha(MoodPalette.Mix(_moon, Color.FromRgb(0x7A, 0x50, 0x38), 0.5), 0.28));
            b.Freeze();
            _moonBrushes[key] = b;
        }
        return b;
    }

    static readonly string Perf = Environment.GetEnvironmentVariable("AXON_PERF") ?? "";

    void DrawSky()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _skyVisual.RenderOpen();
        dc.DrawRectangle(SkyBrush, null, new Rect(0, 0, W, H));
        if (Perf.Contains("hw-nosky")) return;

        // Sterne funkeln
        var moon = MoonCenter();
        double mr = MoonRadius;
        double intro = IntroRunning ? Smooth(_introT / 1.6) : 1;
        foreach (var (nx, ny, z, p, f) in _stars)
        {
            double x = nx * W + _parallax.X * 2 * z, y = ny * H + _parallax.Y * 1.5 * z;
            double dmx = x - moon.X, dmy = y - moon.Y;
            if (dmx * dmx + dmy * dmy < mr * mr * 5) continue; // im Mondlicht verschwinden sie
            double tw = 0.55 + 0.45 * Math.Sin(_t * f + p);
            double a = (0.18 + 0.55 * z) * tw * intro;
            double r = 0.5 + z * 0.9;
            dc.DrawEllipse(SolidFor(5, a), null, new Point(x, y), r, r);
            if (z > 0.8) dc.DrawEllipse(GlowFor(5, a * 0.5), null, new Point(x, y), r * 5, r * 5);
        }

        // Mondscheibe (der Hof liegt in der Dunst-Ebene)
        dc.PushOpacity(intro);
        dc.DrawEllipse(MoonDisk(), null, moon, mr, mr);
        // Krater
        var crater = MoonCrater();
        dc.DrawEllipse(crater, null, new Point(moon.X - mr * 0.3, moon.Y - mr * 0.18), mr * 0.2, mr * 0.17);
        dc.DrawEllipse(crater, null, new Point(moon.X + mr * 0.25, moon.Y + mr * 0.28), mr * 0.26, mr * 0.22);
        dc.DrawEllipse(crater, null, new Point(moon.X + mr * 0.36, moon.Y - mr * 0.32), mr * 0.12, mr * 0.11);
        dc.DrawEllipse(crater, null, new Point(moon.X - mr * 0.42, moon.Y + mr * 0.34), mr * 0.1, mr * 0.09);
        dc.Pop();
    }

    /// <summary>Weiche Ebene über dem Himmel: Nebelschwaden, Mondhof, Wolken – in halber Auflösung gerastert.</summary>
    void DrawMist()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _mistVisual.RenderOpen();
        if (Perf.Contains("nonebula")) return;
        double big = Math.Max(W, H);

        // langsam wandernde Nebelschwaden in Violett und Glutorange
        for (int i = 0; i < 3; i++)
        {
            double cx = W * (0.5 + 0.38 * Math.Sin(_t * (0.019 + i * 0.007) + i * 2.1));
            double cy = H * (0.45 + 0.3 * Math.Cos(_t * (0.016 + i * 0.006) + i * 1.3));
            dc.DrawEllipse(GlowFor(i == 1 ? 1 : 2, i == 1 ? 0.045 : 0.07), null, new Point(cx, cy), big * 0.55, big * 0.42);
        }

        // Mondhof atmet langsam
        var moon = MoonCenter();
        double mr = MoonRadius;
        double intro = IntroRunning ? Smooth(_introT / 1.6) : 1;
        double breath = 0.92 + 0.08 * Math.Sin(_t * 0.35);
        dc.DrawEllipse(GlowFor(4, 0.2 * breath * intro), null, moon, mr * 4.6, mr * 4.6);
        dc.DrawEllipse(GlowFor(4, 0.3 * breath * intro), null, moon, mr * 2.1, mr * 2.1);

        // Wolken ziehen vor dem Mond vorbei
        for (int i = 0; i < 4; i++)
        {
            double speed = 5 + i * 2.2;
            double span = W + 600;
            double cx = (i * 0.29 * span + _t * speed) % span - 300;
            double cy = H * (0.12 + i * 0.07) + Math.Sin(_t * 0.05 + i) * 8;
            double s = 60 + i * 18;
            for (int k = 0; k < 5; k++)
            {
                double ox = (k - 2) * s * 0.55, oy = Math.Sin(k * 1.7 + i) * s * 0.12;
                dc.DrawEllipse(GlowFor(6, 0.75), null, new Point(cx + ox, cy + oy), s * (0.75 + 0.2 * Math.Sin(k + i)), s * 0.32);
            }
        }

        DrawFog(dc);
    }

    /// <summary>Bodennebel kriecht vor den Gräbern entlang (in der Dunst-Ebene, über der Szene).</summary>
    void DrawFog(DrawingContext dc)
    {
        double W = _size.Width, H = _size.Height;
        for (int i = 0; i < 3; i++)
        {
            double cx = W * (0.5 + 0.45 * Math.Sin(_t * (0.03 + i * 0.011) + i * 2.4));
            double cy = H - Math.Max(40, H * 0.06) + i * 6;
            dc.DrawEllipse(GlowFor(7, 0.11 - i * 0.02), null, new Point(cx, cy), W * (0.28 + i * 0.06), 34 + i * 8);
        }
    }

    void Draw(double dt)
    {
        ValidateCaches();
        double W = _size.Width, H = _size.Height;

        _skyAcc += dt;
        if (_skyAcc >= 0.1)
        {
            _skyAcc = 0;
            DrawSky();
            DrawMist();
        }

        using var dc = _visual.RenderOpen();
        if (Perf.Contains("hw-noslow")) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

        // Ranken
        double vineIntro = IntroRunning ? Smooth((_introT - 1.3) / 1.0) : 1;
        foreach (var v in _vines)
        {
            double va = v.Alpha * vineIntro;
            if (va <= 0.01 || Perf.Contains("nodend")) continue;
            foreach (var b in v.Branches)
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
                if (b.ThornGeo == null || b.ThornCount != b.Thorns.Count)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                        for (int k = 0; k + 2 < b.Thorns.Count; k += 3) Poly(ctx, b.Thorns[k], b.Thorns[k + 1], b.Thorns[k + 2]);
                    g.Freeze();
                    b.ThornGeo = g;
                    b.ThornCount = b.Thorns.Count;
                }
                double w = b.Width + 1;
                // dunkler Strang mit glimmender Kante
                dc.DrawGeometry(SolidFor(12, 0.9 * va), null, b.ThornGeo);
                dc.DrawGeometry(null, PenFor(12, 0.92 * va, w), b.Geo);
                dc.DrawGeometry(null, PenFor(1, (0.2 - 0.03 * b.Depth) * va, Math.Max(0.5, w * 0.3)), b.Geo);
                if (b.Growing) dc.DrawEllipse(GlowFor(3, 0.5 * va), null, b.Tip, 6, 6);
            }
        }

        // Irrlichter
        foreach (var w in _wisps)
        {
            double vis = Visible(w);
            if (vis <= 0) continue;
            double pop = vis >= 1 ? 1 : 1 + 0.7 * Math.Sin(vis * Math.PI) * (1 - vis * 0.5);
            double glowA = Math.Min(1, (0.12 + 0.22 * w.Z) * w.Flicker + 0.7 * w.Act) * vis;
            double gr = w.R * (5.5 + 6 * w.Act) * pop * (0.9 + 0.1 * w.Flicker);
            double rr = w.R * vis * pop * (0.85 + 0.15 * w.Flicker);
            var c = new Point(w.X, w.Y);
            int key = w.Tint > 0 ? 2 : 1;
            if (!Perf.Contains("noglow")) dc.DrawEllipse(GlowFor(key, glowA), null, c, gr, gr);
            // Flammenkern: etwas nach oben gezogen
            dc.DrawEllipse(SolidFor(3, (0.45 + 0.45 * w.Z * w.Flicker + 0.4 * w.Act) * vis), null, new Point(c.X, c.Y - rr * 0.2), rr * 0.7, rr * 1.05);
        }

        // Kürbisse – Licht flackert wie eine Kerze
        dc.PushTransform(_groundShift);
        double lit = _lit * (Mood == Mood.Working ? 1.25 : 1) + _litFlash * 0.8;
        foreach (var p in _pumpkins)
        {
            double fl = 0.72 + 0.16 * Math.Sin(_t * p.F1 + p.P1) + 0.08 * Math.Sin(_t * p.F2 + p.P2) + 0.04 * Math.Sin(_t * p.F3 + p.P3);
            double l = Math.Clamp(lit * fl, 0, 1.6);
            // Körper, Rippen und Stiel stehen fest in der Szene; hier flackern nur Lichtschein und Gesicht
            if (l <= 0.02) continue;
            dc.DrawEllipse(GlowFor(1, Math.Min(1, 0.3 * l)), null, p.C, p.S * 4.2, p.S * 3.2);
            dc.DrawGeometry(SolidFor(3, Math.Min(1, 0.95 * l)), null, p.Face);
        }
        dc.Pop();

        // Langsames im langsamen Takt: Glut steigt nur ~30 px/s, die Spinne wippt, Geister schweben –
        // dafür genügen 30 Bilder/s, und der Bildtakt bleibt im Ruhezustand fast leer (wie beim Netz)
        if (!Perf.Contains("hw-noembers")) DrawEmbers(dc);
        if (!Perf.Contains("hw-nospider")) DrawSpider(dc);
        foreach (var g in _ghosts) DrawGhost(dc, g);

        dc.Pop();
    }

    /// <summary>
    /// Eine schnelle Teilebene. WPF zeichnet bei jeder Änderung den ganzen Umriss einer Ebene neu – lägen Funken, Fledermäuse
    /// und Wellen in derselben, wäre das oft fast das ganze Fenster in jedem Bild. Getrennt bleibt jeder Umriss klein,
    /// und eine leere Ebene wird gar nicht erst wieder geöffnet.
    /// </summary>
    sealed class FastLayer
    {
        public readonly DrawingVisual Visual = new();
        bool _empty = true;

        /// <summary>Null, wenn es nichts zu zeichnen gibt und die Ebene schon leer ist.</summary>
        public DrawingContext? Open(bool hasContent)
        {
            if (!hasContent)
            {
                if (!_empty) { using (Visual.RenderOpen()) { } _empty = true; }
                return null;
            }
            _empty = false;
            return Visual.RenderOpen();
        }

        public void Clear()
        {
            using (Visual.RenderOpen()) { }
            _empty = true;
        }
    }

    /// <summary>Schnelle Ebenen, jedes Bild: Funken, Fledermäuse und Wellen.</summary>
    void DrawFast()
    {
        if (Perf.Contains("hw-nofast")) return;
        using (var dc = _sparkLayer.Open((_sparks.Count > 0 || _vpulses.Count > 0) && !Perf.Contains("hw-nosparks"))) if (dc != null) DrawSparks(dc);
        using (var dc = _batLayer.Open(_bats.Count > 0 && !Perf.Contains("hw-nobats"))) if (dc != null) DrawBats(dc);
        using (var dc = _waveLayer.Open(_ripples.Count > 0)) if (dc != null) DrawWaves(dc);
    }

    void ClearFast()
    {
        foreach (var l in FastLayers) l.Clear();
    }

    void DrawSparks(DrawingContext dc)
    {
        // Seelenfunken im Bogen, mit verglimmender Spur
        foreach (var s in _sparks)
        {
            double t = Math.Clamp(s.P, 0, 1);
            int key = s.Alt ? 2 : 3;
            var head = SparkAt(s, t);
            var prev = head;
            // drei Teilstücke wie beim Netz-Impuls – mehr kostet im Bildtakt spürbar, ohne dass man es sieht
            for (int k = 1; k <= 3; k++)
            {
                double u = Math.Max(0, t - k * 0.07);
                var pt = SparkAt(s, u);
                double fade = 1 - k / 4.0;
                dc.DrawLine(PenFor(key, 0.8 * fade * s.Energy, 2.4 * fade + 0.5), prev, pt);
                prev = pt;
                if (u <= 0) break;
            }
            dc.DrawEllipse(GlowFor(key, Math.Min(1, 0.35 + 0.65 * s.Energy)), null, head, 5 + 6 * s.Energy, 5 + 6 * s.Energy);
        }

        foreach (var p in _vpulses)
            dc.DrawEllipse(GlowFor(1, 0.95 * p.V.Alpha), null, PointOnPolyline(p.B.Pts, p.S), 10, 10);
    }

    void DrawEmbers(DrawingContext dc)
    {
        foreach (var e in _embers)
        {
            double life = e.Age / e.Life;
            double a = Math.Sin(Math.Min(1, life * 6) * Math.PI / 2) * (1 - life) * (0.7 + 0.3 * Math.Sin(e.Age * 14 + e.Sway));
            var c = new Point(e.X, e.Y);
            dc.DrawEllipse(GlowFor(e.Alt ? 3 : 1, a * 0.6), null, c, e.Size * 4, e.Size * 4);
            dc.DrawEllipse(SolidFor(3, a), null, c, e.Size * 0.6, e.Size * 0.6);
        }
    }

    // Fledermäuse als vorgerenderte Bilder: eine Form mit wechselnder Verschiebung müsste WPF in jedem Bild neu
    // zerlegen (gemessen ≈ 10 % mehr CPU), ein Bild wird nur kopiert. 16 Flügelschlag-Stufen × 2 Flugrichtungen.
    const double BatUnit = 20, BatBoxW = 80, BatBoxH = 60;
    static ImageSource[]? _batImages;

    static ImageSource[] BatImages()
    {
        if (_batImages != null) return _batImages;
        var glow = Frozen(new RadialGradientBrush(Color.FromArgb(0x24, 0x9B, 0x6B, 0xFF), Color.FromArgb(0, 0x9B, 0x6B, 0xFF)));
        var body = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, BatColor.R, BatColor.G, BatColor.B)));
        var images = new ImageSource[32];
        for (int i = 0; i < 32; i++)
        {
            int frame = i % 16;
            double dir = i < 16 ? 1 : -1;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var c = new Point(BatBoxW / 2, BatBoxH / 2);
                // schwacher violetter Schimmer, damit sie sich vom Nachthimmel abheben
                dc.DrawEllipse(glow, null, c, BatUnit * 1.9, BatUnit * 1.4);
                dc.PushTransform(new MatrixTransform(BatUnit * dir, 0, 0, BatUnit, c.X, c.Y));
                dc.DrawGeometry(body, null, BatFrames[frame]);
                dc.Pop();
            }
            // doppelte Auflösung, damit sie auch auf hochauflösenden Bildschirmen scharf bleiben
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(BatBoxW * 2), (int)(BatBoxH * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            images[i] = bmp;
        }
        return _batImages = images;
    }

    void DrawBats(DrawingContext dc)
    {
        var images = BatImages();
        foreach (var b in _bats)
        {
            int frame = (int)((b.Flap / (Math.PI * 2) % 1 + 1) % 1 * 16) % 16;
            double k = b.Size / BatUnit;
            dc.DrawImage(images[frame + (b.Dir > 0 ? 0 : 16)], new Rect(b.X - BatBoxW / 2 * k, b.Y - BatBoxH / 2 * k, BatBoxW * k, BatBoxH * k));
        }
    }

    void DrawWaves(DrawingContext dc)
    {
        foreach (var r in _ripples)
        {
            double s = 1 - r.R / r.Max;
            dc.DrawEllipse(null, PenFor(1, 0.26 * s, 1.5), r.C, r.R, r.R);
            dc.DrawEllipse(null, PenFor(2, 0.07 * s, 14), r.C, r.R, r.R);
        }
    }

    void DrawSpider(DrawingContext dc)
    {
        var a = SpiderAnchor;
        double sway = Math.Sin(_t * 0.8) * 3 + _spiderVel * 0.08;
        var body = new Point(a.X + sway, a.Y + Math.Max(8, _spiderLen));
        double intro = IntroRunning ? Smooth((_introT - 0.8) / 1.0) : 1;
        if (intro <= 0) return;
        const double scale = 1.4;
        dc.DrawLine(PenFor(9, 0.32 * intro, 0.8), a, new Point(body.X, body.Y - 9 * scale));
        dc.PushTransform(new ScaleTransform(scale, scale, body.X, body.Y));
        // Schimmer, damit sie sich vom dunklen Grund abhebt
        dc.DrawEllipse(GlowFor(2, 0.32 * intro), null, body, 24, 24);

        // Beine: vier pro Seite, mit Knie; beim Warten zappeln sie
        bool fidget = Mood is Mood.Attention or Mood.Working;
        double speed = fidget ? 7 : 1.6, amp = fidget ? 0.32 : 0.1;
        var legs = new StreamGeometry();
        using (var ctx = legs.Open())
        {
            for (int side = -1; side <= 1; side += 2)
                for (int i = 0; i < 4; i++)
                {
                    double baseAng = (-0.55 + i * 0.42) + Math.Sin(_t * speed + i * 1.3 + side) * amp;
                    var hip = new Point(body.X + side * 3, body.Y - 4 + i * 1.6);
                    var knee = new Point(hip.X + side * Math.Cos(baseAng) * 9, hip.Y + Math.Sin(baseAng) * 9 - 5);
                    var foot = new Point(knee.X + side * Math.Cos(baseAng + 0.9) * 9, knee.Y + Math.Sin(baseAng + 0.9) * 9 + 3);
                    ctx.BeginFigure(hip, false, false);
                    ctx.LineTo(knee, true, true);
                    ctx.LineTo(foot, true, true);
                }
        }
        legs.Freeze();
        dc.PushOpacity(intro);
        dc.DrawGeometry(null, PenFor(11, 1, 1.3), legs);
        dc.DrawEllipse(SolidFor(11, 1), null, new Point(body.X, body.Y + 4), 6.5, 8);
        dc.DrawEllipse(SolidFor(11, 1), null, new Point(body.X, body.Y - 5), 4.2, 3.8);
        // Augen glühen in der Zweitfarbe (giftgrün beim Warten, rot bei Fehlern)
        int eye = Mood is Mood.Attention or Mood.Error ? 2 : 3;
        dc.DrawEllipse(SolidFor(eye, 0.95), null, new Point(body.X - 1.5, body.Y - 5.6), 0.9, 0.9);
        dc.DrawEllipse(SolidFor(eye, 0.95), null, new Point(body.X + 1.5, body.Y - 5.6), 0.9, 0.9);
        dc.DrawEllipse(null, PenFor(2, 0.4, 0.6), new Point(body.X, body.Y + 4), 6.5, 8);
        dc.Pop();
        dc.Pop();
    }

    void DrawGhost(DrawingContext dc, Ghost g)
    {
        double life = g.Age / g.Life;
        double fade = Smooth(life / 0.18) * (1 - Smooth((life - 0.6) / 0.4));
        if (fade <= 0.01) return;
        double s = g.Size;
        double rise = Smooth(life) * Math.Max(140, _size.Height * 0.35);
        double x = g.X + Math.Sin(g.Age * 1.1 + g.Sway) * 18;
        double y = g.Y0 - rise - s * 1.6;
        double tilt = Math.Sin(g.Age * 1.1 + g.Sway + 0.6) * 0.12;

        var shape = new StreamGeometry();
        using (var ctx = shape.Open())
        {
            ctx.BeginFigure(new Point(x - s, y), true, true);
            ctx.ArcTo(new Point(x + s, y), new Size(s, s), 0, false, SweepDirection.Clockwise, true, false);
            double hem = y + s * 2.1;
            ctx.LineTo(new Point(x + s * 1.08, hem), true, false);
            // wehender Saum
            int waves = 4;
            for (int i = waves; i > 0; i--)
            {
                double x2 = x - s * 1.08 + s * 2.16 * (i - 1) / waves;
                double xm = x - s * 1.08 + s * 2.16 * (i - 0.5) / waves;
                double wob = Math.Sin(g.Age * 5 + i * 1.7) * s * 0.18;
                ctx.QuadraticBezierTo(new Point(xm, hem + s * 0.42 + wob), new Point(x2, hem), true, false);
            }
            ctx.LineTo(new Point(x - s, y), true, false);
        }
        shape.Freeze();
        dc.PushTransform(new RotateTransform(tilt * 57.3, x, y + s));
        dc.DrawEllipse(GlowFor(3, 0.35 * fade), null, new Point(x, y + s * 0.6), s * 3.4, s * 3.4);
        dc.DrawGeometry(SolidFor(8, 0.5 * fade), null, shape);
        dc.DrawEllipse(SolidFor(11, 0.7 * fade), null, new Point(x - s * 0.36, y + s * 0.05), s * 0.17, s * 0.25);
        dc.DrawEllipse(SolidFor(11, 0.7 * fade), null, new Point(x + s * 0.36, y + s * 0.05), s * 0.17, s * 0.25);
        dc.DrawEllipse(SolidFor(11, 0.55 * fade), null, new Point(x, y + s * 0.62), s * 0.16, s * 0.22);
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
