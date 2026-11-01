using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Hintergrund des Weihnachtsthemas: eine verschneite Winternacht mit Nordlicht. Eiskristalle schweben und werfen sich
/// Sternfunken zu, die Kaskaden auslösen; Tannenzweige mit Ilexbeeren wachsen von den Rändern herein; Schnee fällt,
/// die Fenster einer Hütte leuchten warm, ein Christbaum funkelt, Sternschnuppen und ab und zu ein Rentierschlitten
/// ziehen vorbei, eine Christbaumkugel hängt über der Projektliste.
/// Aufbau und Takt wie beim <see cref="HalloweenField"/>: träge Ebenen zwischengespeichert, nur Schnelles im Bildtakt.
/// </summary>
public sealed class ChristmasField : Backdrop
{
    sealed class Crystal
    {
        public double NX, NY, Z, R, Amp, F1, F2, F3, F4, P1, P2, P3, P4, Tw1, Tw2, TwP1, TwP2, Tint, Spin, SpinF;
        public double X, Y, Act, Twinkle;
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
        /// <summary>Nadeln als schmale Dreiecke (je drei Punkte).</summary>
        public readonly List<Point> Needles = new();
        public Point Tip;
        public double Heading, Turn, Width, Length, MaxLength, SinceBranch, SinceNeedle;
        public int Depth, NeedleSide = 1;
        public bool Growing = true;
        /// <summary>Ilexbeeren am Ende (0 = keine).</summary>
        public int Berries;
        public double BerryAngle;
        public StreamGeometry? Geo, NeedleGeo;
        public int GeoCount, NeedleCount;
        /// <summary>So viele Punkte stecken schon in der zwischengespeicherten Ebene; der Rest bis zur Spitze kommt im Bildtakt.</summary>
        public int DrawnPts;
    }

    sealed class Bough
    {
        public readonly List<Branch> Branches = new();
        public int Phase; // 0 wächst, 1 hält, 2 vergeht
        public double PhaseTime, HoldFor, Alpha, Speed;
        public readonly CachedVisual Visual = new() { Opacity = 0 };
        public long Sig = -1;
        public double RedrawAcc;
    }

    sealed class BPulse
    {
        public Bough V = null!;
        public Branch B = null!;
        public double S;
    }

    sealed class Flake
    {
        public double X, Y, VY, Size, Sway, SwayF, SwayAmp, Z;
    }

    sealed class Glitter
    {
        public double X, Y, VX, VY, Life, Age, Size;
        public bool Alt;
    }

    sealed class Shooter
    {
        public double X, Y, VX, VY, Age, Life;
    }

    sealed class Sleigh
    {
        public double X, Y, BaseY, Speed, Dir, Gallop, BobP;
    }

    sealed class Wave
    {
        public Point C;
        public double R, Max;
    }

    sealed class Puff
    {
        public double X, Y, Age, Life, Size, Drift;
    }

    readonly Random _rng = new();
    Crystal[] _crystals = Array.Empty<Crystal>();
    List<int>[] _nbrs = Array.Empty<List<int>>();
    readonly List<Spark> _sparks = new();
    readonly List<Bough> _boughs = new();
    readonly List<BPulse> _bpulses = new();
    readonly List<Flake> _flakes = new();
    readonly List<Glitter> _glitter = new();
    readonly List<Shooter> _shooters = new();
    readonly List<Sleigh> _sleighs = new();
    readonly List<Wave> _ripples = new();
    readonly List<Puff> _puffs = new();
    (double nx, double ny, double z, double p, double f)[] _stars = Array.Empty<(double, double, double, double, double)>();
    /// <summary>Lichter am Christbaum und an der Dachkante: Ort, Farbindex, Phase, Takt, Reihenfolge beim Einschalten.</summary>
    (Point at, int color, double phase, double freq, double order)[] _lights = Array.Empty<(Point, int, double, double, double)>();
    Point _treeStar = new(double.NaN, double.NaN);
    Rect[] _windows = Array.Empty<Rect>();
    Point _chimney = new(double.NaN, double.NaN);

    double _t, _spawnAcc, _nbrAcc, _boughCooldown, _shooterTimer = 6, _sleighTimer = 25, _puffAcc, _flakeAcc;
    double _initArea;
    Size _size;
    bool _subscribed;

    Color _primary, _secondary, _highlight;
    /// <summary>0 = grünes Nordlicht, 1 = rotes (bei Fehlern).</summary>
    double _alarm;
    double _lit = 1, _litFlash, _starFlash;
    Point _parallax, _parallaxTarget;
    Point _pointer = new(double.NaN, double.NaN);
    Point _focus = new(double.NaN, double.NaN);
    double _ballLen = 60, _ballVel, _ballSwing, _ballSwingVel;

    // Ebenen (von unten nach oben):
    //   Himmel    – Verlauf, Sterne, Mond; zwischengespeichert, 10× pro Sekunde neu
    //   Dunst     – Nordlicht, Mondhof, Wolkenschleier; 10× pro Sekunde, in halber Auflösung gerastert
    //   Szene     – Hügel, Tannen, Hütte, Christbaum, Zweig in der Ecke; nur bei Größenänderung neu, Parallaxe per Transform
    //   Belebtes  – Zweige, Eiskristalle, Lichter, Fenster, Rauch, Schnee, Kugel, Sternenstaub; langsamer Takt
    //   Schnelles – Funken, Sternschnuppen, Schlitten, Wellen; jedes Bild, je eine eigene kleine Ebene
    //   Vignette  – nur bei Größenänderung
    readonly CachedVisual _skyVisual = new();
    readonly CachedVisual _mistVisual = new(0.5);
    readonly CachedVisual _sceneVisual = new();
    /// <summary>Tannenzweige: je Zweig eine eigene zwischengespeicherte Ebene (siehe <see cref="Bough.Visual"/>).</summary>
    readonly ContainerVisual _boughHost = new();
    readonly CachedVisual _visual = new();
    readonly FastLayer _tipLayer = new(), _snowLayer = new(), _sparkLayer = new(), _skyFxLayer = new(), _waveLayer = new();
    FastLayer[] FastLayers => new[] { _tipLayer, _snowLayer, _sparkLayer, _skyFxLayer, _waveLayer };
    readonly List<Visual> _children = new();
    readonly CachedVisual _vigVisual = new();
    readonly TranslateTransform _groundShift = new();
    double _skyAcc = 1;

    static readonly Color StarColor = Color.FromRgb(0xF4, 0xF6, 0xFF);
    static readonly Color SnowColor = Color.FromRgb(0xEE, 0xF4, 0xFF);
    static readonly Color AuroraGreen = Color.FromRgb(0x4A, 0xF0, 0xB0);
    static readonly Color AuroraTeal = Color.FromRgb(0x3C, 0xA8, 0xE8);
    static readonly Color AuroraRed = Color.FromRgb(0xF0, 0x3A, 0x4A);
    static readonly Color AuroraRose = Color.FromRgb(0xB0, 0x40, 0xA0);
    static readonly Color MoonColor = Color.FromRgb(0xEA, 0xF0, 0xFF);
    static readonly Color WindowColor = Color.FromRgb(0xFF, 0xB8, 0x5A);
    static readonly Color SmokeColor = Color.FromRgb(0x9A, 0xA8, 0xC4);
    static readonly Color PineColor = Color.FromRgb(0x0C, 0x24, 0x1A);
    /// <summary>Nadeln: Tannengrün mit einem Hauch Raureif.</summary>
    static readonly Color NeedleColor = Color.FromRgb(0x1A, 0x3E, 0x34);
    static readonly Color BerryColor = Color.FromRgb(0xE8, 0x2A, 0x3C);
    static readonly Color BallRed = Color.FromRgb(0xC8, 0x1E, 0x32);
    static readonly Color ThreadColor = Color.FromRgb(0xE8, 0xD8, 0xA8);
    static readonly Color[] LightColors =
    {
        Color.FromRgb(0xFF, 0xC8, 0x5A), Color.FromRgb(0xFF, 0x4A, 0x5A), Color.FromRgb(0x6A, 0xD8, 0xFF),
        Color.FromRgb(0x7C, 0xF2, 0x9A), Color.FromRgb(0xFF, 0xE8, 0xB0),
    };

    static readonly Brush SkyBrush = Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0x03, 0x06, 0x10), 0),
            new GradientStop(Color.FromRgb(0x07, 0x0E, 0x20), 0.55),
            new GradientStop(Color.FromRgb(0x10, 0x1A, 0x30), 1),
        },
    });

    static readonly Brush FarHillBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x26)));
    static readonly Brush FarSnowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xB8, 0xCC, 0xF0)));
    static readonly Brush ForestBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x07, 0x0D, 0x16)));
    static readonly Brush NearGroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x1C, 0x2C)));
    static readonly Pen SnowCrestPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xC8, 0xD8, 0xF8)), 1.4) { LineJoin = PenLineJoin.Round });
    static readonly Brush TreeSnowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x58, 0xC8, 0xD6, 0xF0)));
    static readonly Brush CabinBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x12)));
    static readonly Brush RoofSnowBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0xC8, 0xD6, 0xF0)));
    static readonly Brush BigTreeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x06, 0x14, 0x10)));
    static readonly Brush TrunkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x10, 0x0A, 0x08)));

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

    public ChristmasField()
    {
        _children.AddRange(new Visual[] { _skyVisual, _mistVisual, _sceneVisual, _boughHost, _visual });
        foreach (var l in FastLayers) _children.Add(l.Visual);
        _children.Add(_vigVisual);
        foreach (var v in _children) AddVisualChild(v);
        _sceneVisual.Transform = _groundShift;
        IsHitTestVisible = false;
        (_primary, _secondary, _highlight) = MoodPalette.For(Mood.Calm);
        // Testbilder: AXON_PERF=xm-sleigh schickt den Schlitten gleich nach der Startanimation los
        if (Perf.Contains("xm-sleigh")) _sleighTimer = 0.5;
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
            InvalidateBoughs(); // glimmende Kante der Zweige in der neuen Stimmungsfarbe
            if (value == Mood.Done && old != Mood.Done) { Ripple(); StarBurst(); }
            if (value == Mood.Working && old != Mood.Working) { Burst(4); _shooterTimer = Math.Min(_shooterTimer, 1.2); _sleighTimer = Math.Min(_sleighTimer, 6); }
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
    // Dunkel · der Himmel dämmert, das Nordlicht zieht auf · Tannen und Hütte tauchen auf · Eiskristalle entzünden sich
    // von der Mitte nach außen · ≈ 2 s die Lichterkette geht Licht für Licht an, der Stern flammt auf,
    // eine Sternschnuppe zieht vorbei · ab 2,8 s Normalbetrieb.

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
        _bpulses.Clear();
        _ripples.Clear();
        _glitter.Clear();
        _shooters.Clear();
        _sleighs.Clear();
        _lit = 0;
        foreach (var c in _crystals) c.Shown = false;
        _skyVisual.Opacity = _mistVisual.Opacity = 0;
        _sceneVisual.Opacity = 0;
    }

    static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }

    double Visible(Crystal c) => _introT < 0 ? 1 : Smooth((_introT - c.Appear) / 0.4);

    void StepIntro(double dt)
    {
        _introT += dt * _introSpeed;
        foreach (var c in _crystals)
            if (!c.Shown && _introT >= c.Appear)
            {
                c.Shown = true;
                c.Act = Math.Max(c.Act, 0.9);
            }
        double sky = Smooth(_introT / 1.4);
        if (Math.Abs(_skyVisual.Opacity - sky) > 0.002) _skyVisual.Opacity = _mistVisual.Opacity = sky;
        double scene = Smooth((_introT - 0.5) / 1.0);
        if (Math.Abs(_sceneVisual.Opacity - scene) > 0.002) _sceneVisual.Opacity = scene;
        if (!_ignited && _introT >= IgniteAt)
        {
            _ignited = true;
            _litFlash = 1;
            _starFlash = 1;
            var keep = _focus;
            _focus = new Point(_size.Width / 2, _size.Height * 0.45);
            Ripple();
            _focus = keep;
            Burst(7);
            SpawnShooter(true);
            RaiseIgnited();
        }
        // Lichterkette schaltet sich in den letzten 0,8 s der Startanimation Licht für Licht ein (siehe LightOn)
        _lit = Smooth((_introT - (IgniteAt - 0.35)) / 0.8);
        if (_introT >= IntroEnd)
        {
            _introT = -1;
            _lit = 1;
            _skyVisual.Opacity = _mistVisual.Opacity = 1;
            _sceneVisual.Opacity = 1;
        }
    }

    /// <summary>Während der Startanimation gehen die Lichter nacheinander an (<paramref name="order"/> 0…1).</summary>
    double LightOn(double order) => _lit >= 1 ? 1 : Smooth((_lit * 1.3 - order) / 0.3);

    public override void SetFocusPoint(Point p) => _focus = p;

    public override void SetPointer(Point p)
    {
        if (_size.Width <= 0) return;
        _pointer = p;
        _parallaxTarget = new Point((p.X / _size.Width - 0.5) * -2, (p.Y / _size.Height - 0.5) * -2);
    }

    /// <summary>Benutzer hat etwas abgeschickt: Licht wandert die Zweige entlang, Kristalle funkeln, die Lichter blinken auf.</summary>
    public override void FireInput()
    {
        if (Level == AnimationLevel.Off) return;
        foreach (var v in _boughs)
            if (v.Branches.Count > 0 && v.Branches[0].Pts.Count > 2 && v.Phase < 2)
                _bpulses.Add(new BPulse { V = v, B = v.Branches[0], S = 0 });
        foreach (var c in _crystals) c.Act = Math.Max(c.Act, 0.25 + _rng.NextDouble() * 0.35);
        _litFlash = Math.Max(_litFlash, 0.6);
        _ballSwingVel += (_rng.NextDouble() < 0.5 ? -1 : 1) * 0.5;
        Burst(3);
    }

    public void Ripple()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        var c = double.IsNaN(_focus.X) ? new Point(_size.Width / 2, _size.Height / 2) : _focus;
        double max = Math.Sqrt(_size.Width * _size.Width + _size.Height * _size.Height) * 0.75;
        _ripples.Add(new Wave { C = c, R = 0, Max = max });
    }

    /// <summary>Neuer Tab: ein neuer Tannenzweig wächst herein.</summary>
    public override void GrowDendrite()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        if (_boughs.Count >= MaxBoughs + 1) return;
        SpawnBough();
    }

    void Burst(int count)
    {
        if (_crystals.Length == 0) return;
        for (int i = 0; i < count; i++) StartSpark(_rng.Next(_crystals.Length), 1.0);
    }

    /// <summary>„Fertig“: der Stern auf dem Baum flammt auf und sprüht Sternenstaub, eine Sternschnuppe zieht vorbei.</summary>
    void StarBurst()
    {
        if (Level == AnimationLevel.Off || _size.Width <= 0) return;
        _starFlash = 1;
        if (!double.IsNaN(_treeStar.X))
        {
            var at = new Point(_treeStar.X + _groundShift.X, _treeStar.Y + _groundShift.Y);
            for (int i = 0; i < 26 && _glitter.Count < 60; i++)
            {
                double a = -Math.PI / 2 + (_rng.NextDouble() - 0.5) * 2.4;
                double v = 30 + _rng.NextDouble() * 70;
                _glitter.Add(new Glitter
                {
                    X = at.X, Y = at.Y, VX = Math.Cos(a) * v, VY = Math.Sin(a) * v,
                    Life = 2.2 + _rng.NextDouble() * 2.2, Size = 0.9 + _rng.NextDouble() * 1.4, Alt = _rng.NextDouble() < 0.35,
                });
            }
        }
        SpawnShooter(false);
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
        InvalidateBoughs();
        _skyAcc = 1;
        if (Level == AnimationLevel.Off) Redraw();
    }

    void Redraw()
    {
        if (_size.Width <= 0) return;
        EnsureCrystals();
        Step(0);
        _skyAcc = 1;
        InvalidateBoughs();
        Draw(0);
        ClearFast();
    }

    void OnSlow(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        if (IntroRunning) return; // während der Startanimation zeichnet der Bildtakt alles
        EnsureCrystals();
        Step(dt);
        StepSlow(dt);
        Draw(dt);
        _sinceStep = 0;
    }

    /// <summary>Zeit seit dem letzten langsamen Schritt – damit wächst die Zweigspitze im Bildtakt stufenlos weiter.</summary>
    double _sinceStep;

    void OnFrame(double dt)
    {
        if (_size.Width <= 0 || _size.Height <= 0) return;
        _sinceStep += dt;
        if (IntroRunning)
        {
            EnsureCrystals();
            StepIntro(dt);
            Step(dt);
            StepSlow(dt);
            _skyAcc = 1; // Nordlicht zieht auf – Himmel jedes Bild
            Draw(dt);
            _sinceStep = 0;
        }
        if (_crystals.Length == 0) return;
        StepFast(dt);
        DrawFast();
    }

    int MaxBoughs => Level == AnimationLevel.Subtle ? 2 : 3;
    int FlakeTarget => (int)Math.Clamp(_size.Width * _size.Height / (Level == AnimationLevel.Subtle ? 16000 : 9000), 30, 170)
        * (Mood == Mood.Working ? 5 : 4) / 4;

    void EnsureCrystals()
    {
        double area = _size.Width * _size.Height;
        if (_crystals.Length > 0 && area > _initArea * 0.5 && area < _initArea * 2) return;
        _initArea = area;
        double density = Level == AnimationLevel.Subtle ? 38000 : 26000;
        int count = (int)Math.Clamp(area / density, 14, 60);
        var list = new List<Crystal>(count);
        for (int i = 0; i < count; i++)
        {
            double bx = 0, by = 0, best = -1;
            for (int k = 0; k < 10; k++)
            {
                double x = -0.02 + _rng.NextDouble() * 1.04, y = 0.04 + _rng.NextDouble() * 0.78;
                double dmin = double.MaxValue;
                foreach (var n in list)
                {
                    double dx = (n.NX - x) * _size.Width, dy = (n.NY - y) * _size.Height;
                    dmin = Math.Min(dmin, dx * dx + dy * dy);
                }
                if (dmin > best) { best = dmin; bx = x; by = y; }
            }
            double z = 0.25 + Math.Pow(_rng.NextDouble(), 1.4) * 0.75;
            list.Add(new Crystal
            {
                NX = bx, NY = by, Z = z,
                R = 1.3 + z * 2.1,
                Amp = 14 + _rng.NextDouble() * 32,
                F1 = Freq(28, 64), F2 = Freq(18, 44), F3 = Freq(30, 70), F4 = Freq(20, 48),
                P1 = Ph(), P2 = Ph(), P3 = Ph(), P4 = Ph(),
                // Funkeln wie Eis im Mondlicht: ruhiger als eine Flamme
                Tw1 = 2 * Math.PI * (0.35 + _rng.NextDouble() * 0.5), Tw2 = 2 * Math.PI * (1.4 + _rng.NextDouble() * 1.4),
                TwP1 = Ph(), TwP2 = Ph(),
                Tint = _rng.NextDouble() < 0.35 ? 1 : 0,
                Spin = Ph(), SpinF = (_rng.NextDouble() - 0.5) * 0.25,
            });
        }
        foreach (var c in list)
        {
            double dx = c.NX - 0.5, dy = (c.NY - 0.45) * _size.Height / Math.Max(1, _size.Width);
            double d = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / 0.62);
            c.Appear = 0.3 + 1.15 * d + _rng.NextDouble() * 0.16;
        }
        _crystals = list.ToArray();
        _nbrs = new List<int>[_crystals.Length];
        for (int i = 0; i < _nbrs.Length; i++) _nbrs[i] = new List<int>();
        _sparks.Clear();
        _nbrAcc = 999;

        int stars = (int)Math.Clamp(area / 8000, 50, 180);
        _stars = new (double, double, double, double, double)[stars];
        for (int i = 0; i < stars; i++)
            _stars[i] = (_rng.NextDouble(), Math.Pow(_rng.NextDouble(), 1.4) * 0.7, 0.3 + Math.Pow(_rng.NextDouble(), 3) * 0.7, Ph(), Freq(2.5, 9));

        // Schnee von Anfang an über das ganze Fenster verteilt
        _flakes.Clear();
        for (int i = 0; i < FlakeTarget; i++) _flakes.Add(NewFlake(_rng.NextDouble() * _size.Height));

        if (_boughs.Count == 0 && Level != AnimationLevel.Off)
        {
            for (int i = 0; i < MaxBoughs; i++)
            {
                var v = SpawnBough();
                double pre = 4 + _rng.NextDouble() * 14;
                for (double s = 0; s < pre; s += 0.1) GrowBough(v, 0.1);
            }
        }
    }

    double Freq(double minPeriod, double maxPeriod) => Math.PI * 2 / (minPeriod + _rng.NextDouble() * (maxPeriod - minPeriod));
    double Ph() => _rng.NextDouble() * Math.PI * 2;

    double MaxDist => Math.Clamp(Math.Sqrt(_size.Width * _size.Height / Math.Max(1, _crystals.Length)) * 1.7, 130, 260);

    void Step(double dt)
    {
        _t += dt;

        var (tp, ts, th) = MoodPalette.For(Mood);
        double kc = 1 - Math.Exp(-dt / 0.9);
        _primary = MoodPalette.Approach(_primary, tp, kc);
        _secondary = MoodPalette.Approach(_secondary, ts, kc);
        _highlight = MoodPalette.Approach(_highlight, th, kc);
        // Nordlicht färbt sich bei Fehlern langsam rot
        double alarmTarget = Mood == Mood.Error ? 1 : 0;
        _alarm += (alarmTarget - _alarm) * (1 - Math.Exp(-dt / 2.2));
        if (Math.Abs(_alarm - alarmTarget) < 0.004) _alarm = alarmTarget;
        _litFlash *= Math.Exp(-dt / 0.7);
        _starFlash *= Math.Exp(-dt / 1.1);

        if (Level == AnimationLevel.Full)
        {
            double kp = 1 - Math.Exp(-dt / 1.2);
            _parallax = new Point(_parallax.X + (_parallaxTarget.X - _parallax.X) * kp, _parallax.Y + (_parallaxTarget.Y - _parallax.Y) * kp);
            // Die Landschaft bleibt stehen: verschoben werden dürfte sie nur in ganzen Pixeln (sonst verschwimmt der
            // Zwischenspeicher), und diese Ein-Pixel-Sprünge sah man an der Schneekante als Wackeln.
            // Tiefe geben weiter die stufenlos verschobenen Kristalle, Sterne und der Mond.
        }

        double W = _size.Width, H = _size.Height;
        double decay = Math.Exp(-dt / 1.3);
        double ko = 1 - Math.Exp(-dt / 0.6);
        bool pointer = !double.IsNaN(_pointer.X) && Level == AnimationLevel.Full;
        foreach (var c in _crystals)
        {
            double ox = c.Amp * (Math.Sin(_t * c.F1 + c.P1) * 0.7 + Math.Sin(_t * c.F2 + c.P2) * 0.3);
            double oy = c.Amp * (Math.Sin(_t * c.F3 + c.P3) * 0.7 + Math.Sin(_t * c.F4 + c.P4) * 0.3);
            double bx = c.NX * W + ox + _parallax.X * 14 * c.Z;
            double by = c.NY * H + oy + _parallax.Y * 10 * c.Z;
            // Kristalle weichen dem Mauszeiger aus wie Schnee einem Atemhauch
            double tx = 0, ty = 0;
            if (pointer)
            {
                double dx = bx - _pointer.X, dy = by - _pointer.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 160 && d > 0.1)
                {
                    double push = 60 * Math.Pow(1 - d / 160, 2);
                    tx = dx / d * push;
                    ty = dy / d * push;
                }
            }
            c.OX += (tx - c.OX) * ko;
            c.OY += (ty - c.OY) * ko;
            c.X = bx + c.OX;
            c.Y = by + c.OY;
            c.Act *= decay;
            c.Spin += c.SpinF * dt;
            c.Twinkle = 0.72 + 0.28 * (0.6 * Math.Sin(_t * c.Tw1 + c.TwP1) + 0.4 * Math.Sin(_t * c.Tw2 + c.TwP2));
        }

        _nbrAcc += dt;
        if (_nbrAcc > 0.5)
        {
            _nbrAcc = 0;
            double md = MaxDist * 0.92, md2 = md * md;
            for (int i = 0; i < _crystals.Length; i++) _nbrs[i].Clear();
            for (int i = 0; i < _crystals.Length; i++)
                for (int j = i + 1; j < _crystals.Length; j++)
                {
                    double dx = _crystals[i].X - _crystals[j].X, dy = _crystals[i].Y - _crystals[j].Y;
                    if (dx * dx + dy * dy < md2) { _nbrs[i].Add(j); _nbrs[j].Add(i); }
                }
        }

        if (dt <= 0) return;
        UpdateBoughs(dt);
    }

    /// <summary>Langsames im 30/s-Takt: Rauch, Kugel.</summary>
    void StepSlow(double dt)
    {
        if (dt <= 0) return;
        UpdatePuffs(dt);
        UpdateBall(dt);
    }

    void StepFast(double dt)
    {
        if (dt <= 0) return;
        bool subtle = Level == AnimationLevel.Subtle;
        // Schnee und Sternenstaub im Bildtakt: im 30/s-Takt ruckelten die vielen gleichmäßig fallenden Flocken sichtbar
        UpdateFlakes(dt);
        UpdateGlitter(dt);
        if (IntroRunning && !_ignited)
        {
            UpdateRipples(dt);
            UpdateShooters(dt);
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
        while (_spawnAcc >= 1 && _crystals.Length > 0)
        {
            _spawnAcc -= 1;
            StartSpark(_rng.Next(_crystals.Length), 1.0);
        }

        // Sternschnuppen in unregelmäßigen Abständen, öfter, wenn Claude arbeitet
        _shooterTimer -= dt;
        if (_shooterTimer <= 0)
        {
            SpawnShooter(false);
            double min = Mood == Mood.Working ? 5 : 12, span = Mood == Mood.Working ? 7 : 20;
            _shooterTimer = (min + _rng.NextDouble() * span) * (subtle ? 1.8 : 1);
        }

        // ab und zu zieht der Schlitten vor dem Mond vorbei
        _sleighTimer -= dt;
        if (_sleighTimer <= 0)
        {
            SpawnSleigh();
            double min = Mood == Mood.Working ? 18 : 45, span = Mood == Mood.Working ? 20 : 50;
            _sleighTimer = (min + _rng.NextDouble() * span) * (subtle ? 1.8 : 1);
        }

        UpdateSparks(dt);
        UpdateBoughPulses(dt);
        UpdateShooters(dt);
        UpdateSleighs(dt);
        UpdateRipples(dt);
    }

    void StartSpark(int from, double energy, int exclude = -1)
    {
        if (from < 0 || from >= _crystals.Length || _nbrs.Length == 0) return;
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
            Bend = (_rng.NextDouble() < 0.5 ? -1 : 1) * (0.1 + _rng.NextDouble() * 0.18),
            Alt = _rng.NextDouble() < (Mood is Mood.Attention or Mood.Error ? 0.6 : 0.35),
        });
        _crystals[from].Act = Math.Max(_crystals[from].Act, energy * 0.6);
    }

    Point SparkAt(Spark s, double u)
    {
        var a = _crystals[s.A];
        var b = _crystals[s.B];
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
            var a = _crystals[s.A];
            var b = _crystals[s.B];
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

    // ───────────────────────────── Schnee, Rauch, Sternenstaub ─────────────────────────────

    Flake NewFlake(double y)
    {
        // drei Tiefen: fern = klein, langsam, blass; nah = größer, schneller
        double z = Math.Pow(_rng.NextDouble(), 1.6);
        return new Flake
        {
            X = _rng.NextDouble() * (_size.Width + 60) - 30,
            Y = y,
            Z = z,
            VY = 12 + z * 30 + _rng.NextDouble() * 6,
            Size = 0.7 + z * 1.9,
            Sway = Ph(),
            SwayF = 0.4 + _rng.NextDouble() * 0.7,
            SwayAmp = 6 + _rng.NextDouble() * 12,
        };
    }

    void UpdateFlakes(double dt)
    {
        double H = _size.Height;
        // leichter Wind, der langsam dreht
        double wind = 6 * Math.Sin(_t * 0.07) + 3 * Math.Sin(_t * 0.19 + 1);
        for (int i = _flakes.Count - 1; i >= 0; i--)
        {
            var f = _flakes[i];
            f.Y += f.VY * dt;
            f.X += (wind * (0.4 + f.Z) + Math.Cos(_t * f.SwayF + f.Sway) * f.SwayAmp * f.SwayF) * dt;
            if (f.Y > H + 6 || f.X < -40 || f.X > _size.Width + 40) _flakes.RemoveAt(i);
        }
        int target = FlakeTarget;
        // fehlende Flocken langsam von oben nachliefern, nicht alle auf einmal
        _flakeAcc += dt * target / 8.0;
        while (_flakeAcc >= 1 && _flakes.Count < target)
        {
            _flakeAcc -= 1;
            _flakes.Add(NewFlake(-6 - _rng.NextDouble() * 20));
        }
        if (_flakes.Count >= target) _flakeAcc = 0;
    }

    void UpdatePuffs(double dt)
    {
        if (double.IsNaN(_chimney.X)) return;
        _puffAcc += dt * (Mood == Mood.Working ? 1.1 : 0.6);
        while (_puffAcc >= 1 && _puffs.Count < 14)
        {
            _puffAcc -= 1;
            _puffs.Add(new Puff { X = _chimney.X, Y = _chimney.Y, Life = 6 + _rng.NextDouble() * 3, Size = 4 + _rng.NextDouble() * 3, Drift = Ph() });
        }
        for (int i = _puffs.Count - 1; i >= 0; i--)
        {
            var p = _puffs[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _puffs.RemoveAt(i); continue; }
            p.Y -= (14 - p.Age * 0.9) * dt;
            p.X += (5 + Math.Sin(p.Age * 0.8 + p.Drift) * 4) * dt;
        }
    }

    void UpdateGlitter(double dt)
    {
        for (int i = _glitter.Count - 1; i >= 0; i--)
        {
            var g = _glitter[i];
            g.Age += dt;
            if (g.Age >= g.Life) { _glitter.RemoveAt(i); continue; }
            g.X += g.VX * dt;
            g.Y += g.VY * dt;
            g.VX *= Math.Exp(-dt * 1.1);
            g.VY = g.VY * Math.Exp(-dt * 1.1) + 9 * dt; // sinkt danach sacht wie Schnee
        }
    }

    /// <summary>Die Kugel hängt ruhig, sinkt herab und leuchtet, wenn Claude auf dich wartet, und schaukelt beim Arbeiten.</summary>
    void UpdateBall(double dt)
    {
        double target = Mood switch
        {
            Mood.Attention => Math.Clamp(_size.Height * 0.28, 130, 240),
            Mood.Working => 66 + 8 * Math.Sin(_t * 0.5),
            Mood.Done => 50,
            Mood.Error => 84,
            _ => 60 + 3 * Math.Sin(_t * 0.5),
        };
        double k = 3.0, damping = 2.6;
        _ballVel += ((target - _ballLen) * k - _ballVel * damping) * dt;
        _ballLen += _ballVel * dt;
        // Pendel: schwingt nach, wenn sich die Länge ändert; beim Arbeiten leicht angestoßen
        double drive = Mood == Mood.Working ? Math.Sin(_t * 1.6) * 0.25 : 0;
        double g = 9.0 / Math.Max(0.4, _ballLen / 40);
        _ballSwingVel += (-g * Math.Sin(_ballSwing) - _ballSwingVel * 0.5 + drive) * dt;
        _ballSwing += _ballSwingVel * dt;
    }

    // ───────────────────────────── Himmelserscheinungen ─────────────────────────────

    void SpawnShooter(bool fromCenter)
    {
        if (_size.Width <= 0 || _shooters.Count > 2) return;
        double W = _size.Width, H = _size.Height;
        double dir = _rng.NextDouble() < 0.5 ? 1 : -1;
        double x = fromCenter ? W * 0.5 : W * (0.15 + _rng.NextDouble() * 0.7);
        double y = H * (0.04 + _rng.NextDouble() * 0.22);
        double speed = 520 + _rng.NextDouble() * 260;
        double ang = 0.28 + _rng.NextDouble() * 0.3;
        _shooters.Add(new Shooter { X = x, Y = y, VX = Math.Cos(ang) * speed * dir, VY = Math.Sin(ang) * speed, Life = 0.9 + _rng.NextDouble() * 0.5 });
    }

    void UpdateShooters(double dt)
    {
        for (int i = _shooters.Count - 1; i >= 0; i--)
        {
            var s = _shooters[i];
            s.Age += dt;
            s.X += s.VX * dt;
            s.Y += s.VY * dt;
            if (s.Age >= s.Life) _shooters.RemoveAt(i);
        }
    }

    void SpawnSleigh()
    {
        if (_size.Width <= 0 || _sleighs.Count > 0) return;
        double dir = _rng.NextDouble() < 0.5 ? 1 : -1;
        var moon = MoonCenter();
        // die Bahn führt am Mond vorbei – dort sieht man die Silhouette am besten
        double y = moon.Y + (_rng.NextDouble() - 0.5) * MoonRadius * 1.2;
        _sleighs.Add(new Sleigh
        {
            X = dir > 0 ? -SleighBoxW : _size.Width + SleighBoxW,
            BaseY = y, Y = y, Dir = dir,
            Speed = 70 + _rng.NextDouble() * 30,
            BobP = Ph(),
        });
    }

    void UpdateSleighs(double dt)
    {
        for (int i = _sleighs.Count - 1; i >= 0; i--)
        {
            var s = _sleighs[i];
            s.X += s.Dir * s.Speed * dt;
            s.BobP += dt * 0.9;
            s.Y = s.BaseY + Math.Sin(s.BobP) * 10 - (s.X / Math.Max(1, _size.Width) - 0.5) * s.Dir * 24;
            s.Gallop += dt * 2.6;
            // Sternenstaub hinter dem Schlitten
            if (_glitter.Count < 60 && _rng.NextDouble() < dt * 14)
                _glitter.Add(new Glitter
                {
                    X = s.X - s.Dir * SleighBoxW * 0.42, Y = s.Y + 6, VX = -s.Dir * 10, VY = 6 + _rng.NextDouble() * 8,
                    Life = 1.6 + _rng.NextDouble() * 1.4, Size = 0.6 + _rng.NextDouble() * 0.9, Alt = true,
                });
            if (s.X < -SleighBoxW * 1.2 || s.X > _size.Width + SleighBoxW * 1.2) _sleighs.RemoveAt(i);
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
            foreach (var c in _crystals)
            {
                double dx = c.X - r.C.X, dy = c.Y - r.C.Y;
                double dd = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r.R);
                if (dd < 26) c.Act = Math.Max(c.Act, 0.75 * strength * (1 - dd / 26));
            }
        }
    }

    // ───────────────────────────── Tannenzweige ─────────────────────────────

    Bough SpawnBough()
    {
        double W = _size.Width, H = _size.Height;
        // Zweige kommen von oben und von den Seiten – unten liegt die Schneelandschaft
        int edge = _rng.Next(3);
        double along = 0.12 + _rng.NextDouble() * 0.6;
        Point root;
        double heading;
        switch (edge)
        {
            case 0: root = new Point(W * along, -6); heading = Math.PI / 2; break;
            case 1: root = new Point(W + 6, H * along); heading = Math.PI; break;
            default: root = new Point(-6, H * along); heading = 0; break;
        }
        heading += (_rng.NextDouble() - 0.5) * 0.9;
        var v = new Bough
        {
            Speed = (Level == AnimationLevel.Subtle ? 13 : 18) + _rng.NextDouble() * 9,
            HoldFor = 10 + _rng.NextDouble() * 8,
        };
        var b = new Branch
        {
            Tip = root,
            Heading = heading,
            Width = 2.6,
            MaxLength = Math.Min(W, H) * (0.26 + _rng.NextDouble() * 0.24),
        };
        b.Pts.Add(root);
        v.Branches.Add(b);
        _boughs.Add(v);
        _boughHost.Children.Add(v.Visual);
        return v;
    }

    void UpdateBoughs(double dt)
    {
        _boughCooldown -= dt;
        int alive = 0;
        foreach (var v in _boughs) if (v.Phase < 2) alive++;
        if (alive < MaxBoughs && _boughCooldown <= 0)
        {
            SpawnBough();
            _boughCooldown = 5 + _rng.NextDouble() * 5;
        }
        for (int i = _boughs.Count - 1; i >= 0; i--)
        {
            var v = _boughs[i];
            GrowBough(v, dt);
            if (v.Phase == 2 && v.Alpha <= 0.001)
            {
                _boughs.RemoveAt(i);
                _boughHost.Children.Remove(v.Visual);
            }
        }
    }

    void GrowBough(Bough v, double dt)
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
                    // Tannenzweige wachsen fast gerade, mit leichtem Bogen
                    b.Turn += (_rng.NextDouble() - 0.5) * 1.2 * dt;
                    b.Turn *= Math.Exp(-dt * 1.4);
                    b.Heading += b.Turn * dt * 1.2;
                    double step = v.Speed * dt * (1 - 0.18 * b.Depth);
                    b.Tip = new Point(b.Tip.X + Math.Cos(b.Heading) * step, b.Tip.Y + Math.Sin(b.Heading) * step);
                    b.Length += step;
                    var last = b.Pts[^1];
                    if ((b.Tip - last).Length >= 5) b.Pts.Add(b.Tip);
                    b.SinceNeedle += step;
                    if (b.SinceNeedle > 3.2)
                    {
                        b.SinceNeedle = 0;
                        AddNeedles(b);
                    }
                    b.SinceBranch += dt;
                    if (b.Depth < 2 && b.SinceBranch > 1.6 && _rng.NextDouble() < dt * 0.6 && v.Branches.Count < 12)
                    {
                        b.SinceBranch = 0;
                        double side = _rng.NextDouble() < 0.5 ? -1 : 1;
                        var child = new Branch
                        {
                            Tip = b.Tip,
                            Heading = b.Heading + side * (0.6 + _rng.NextDouble() * 0.35),
                            Width = b.Width * 0.7,
                            Depth = b.Depth + 1,
                            MaxLength = Math.Max(24, (b.MaxLength - b.Length) * (0.4 + _rng.NextDouble() * 0.3)),
                        };
                        child.Pts.Add(b.Tip);
                        v.Branches.Add(child);
                    }
                    bool outside = b.Tip.X < -30 || b.Tip.Y < -30 || b.Tip.X > W + 30 || b.Tip.Y > H + 30;
                    if (b.Length >= b.MaxLength || outside && b.Length > 40)
                    {
                        b.Growing = false;
                        if ((b.Tip - b.Pts[^1]).Length > 0.5) b.Pts.Add(b.Tip);
                        // am Ende eines Seitenzweigs oft ein Büschel Ilexbeeren
                        if (!outside && (b.Depth > 0 ? _rng.NextDouble() < 0.6 : _rng.NextDouble() < 0.9))
                        {
                            b.Berries = 2 + _rng.Next(2);
                            b.BerryAngle = Ph();
                        }
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

    /// <summary>Ein Paar Nadeln an der Spitze, schräg nach vorn – zur Spitze hin kürzer.</summary>
    void AddNeedles(Branch b)
    {
        double taper = 1 - Math.Min(0.5, b.Length / Math.Max(1, b.MaxLength) * 0.5);
        double len = (5.5 + b.Width * 1.9) * taper * (0.85 + _rng.NextDouble() * 0.3);
        // als schmale Dreiecke: eine Füllung kostet WPF viel weniger als hunderte gestrichelte Linien mit runden Enden
        var dir = new Vector(Math.Cos(b.Heading), Math.Sin(b.Heading)) * 0.7;
        for (int side = -1; side <= 1; side += 2)
        {
            double ang = b.Heading + side * (0.95 + _rng.NextDouble() * 0.2);
            b.Needles.Add(b.Tip - dir);
            b.Needles.Add(b.Tip + dir);
            b.Needles.Add(new Point(b.Tip.X + Math.Cos(ang) * len, b.Tip.Y + Math.Sin(ang) * len));
        }
    }

    void UpdateBoughPulses(double dt)
    {
        for (int i = _bpulses.Count - 1; i >= 0; i--)
        {
            var p = _bpulses[i];
            p.S += 140 * dt;
            if (p.S < p.B.Length) continue;
            _bpulses.RemoveAt(i);
            var tip = p.B.Tip;
            int best = -1;
            double bestD = double.MaxValue;
            for (int k = 0; k < _crystals.Length; k++)
            {
                double dx = _crystals[k].X - tip.X, dy = _crystals[k].Y - tip.Y;
                double dd = dx * dx + dy * dy;
                if (dd < bestD) { bestD = dd; best = k; }
            }
            if (best >= 0)
            {
                _crystals[best].Act = 1;
                StartSpark(best, 1);
                StartSpark(best, 0.9);
            }
        }
    }

    // ───────────────────────────── Szene (Silhouetten) ─────────────────────────────

    double GroundY(double x)
    {
        double H = _size.Height;
        return H - Math.Max(42, H * 0.065) - 8 * Math.Sin(x * 0.005 + 0.4) - 4 * Math.Sin(x * 0.015 + 1.7);
    }

    double FarHillY(double x)
    {
        double H = _size.Height;
        return H - Math.Max(92, H * 0.15) - 22 * Math.Sin(x * 0.0034 + 1.1) - 10 * Math.Sin(x * 0.0098 + 2.3);
    }

    Point MoonCenter()
    {
        double W = _size.Width, H = _size.Height;
        double rise = IntroRunning ? (1 - Smooth(_introT / 1.8)) * 46 : 0;
        return new Point(W * 0.8 + _parallax.X * 3, H * 0.19 + rise + _parallax.Y * 2);
    }

    double MoonRadius => Math.Clamp(Math.Min(_size.Width, _size.Height) * 0.06, 24, 54);

    // Aufhängung der Kugel: am Zweig oben links, genau über der Graph-Linie der Projektliste
    Point BallAnchor => new(22, 30);

    /// <summary>Silhouetten einmal pro Größe erzeugen – fester Zufall, damit die Landschaft beim Vergrößern nicht springt.</summary>
    void BuildScene()
    {
        double W = _size.Width, H = _size.Height;
        _lights = Array.Empty<(Point, int, double, double, double)>();
        _windows = Array.Empty<Rect>();
        _treeStar = _chimney = new Point(double.NaN, double.NaN);
        if (W <= 0 || H <= 0) return;
        var rng = new Random(24);
        using var dc = _sceneVisual.RenderOpen();
        double x0 = -12, x1 = W + 12;
        var lights = new List<(Point, int, double, double, double)>();

        // ferne, verschneite Hügel
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
        // Mondlicht auf dem Schnee der Hügelkämme
        var farCrest = new StreamGeometry();
        using (var ctx = farCrest.Open())
        {
            ctx.BeginFigure(new Point(x0, FarHillY(x0)), true, true);
            var pts = new List<Point>();
            for (double x = x0; x <= x1; x += 8) pts.Add(new Point(x, FarHillY(x)));
            for (double x = x1; x >= x0; x -= 8) pts.Add(new Point(x, FarHillY(x) + 5 + 3 * Math.Sin(x * 0.03)));
            ctx.PolyLineTo(pts, true, true);
        }
        farCrest.Freeze();
        dc.DrawGeometry(FarSnowBrush, null, farCrest);

        // Wald auf den fernen Hügeln: kleine Tannen in Reihen
        var forest = new StreamGeometry();
        var forestSnow = new StreamGeometry();
        using (var ctx = forest.Open())
        using (var sctx = forestSnow.Open())
        {
            for (double x = x0; x <= x1; x += 9 + rng.NextDouble() * 16)
            {
                if (rng.NextDouble() < 0.25) { x += 30 + rng.NextDouble() * 60; continue; }
                double h = 14 + rng.NextDouble() * 18;
                Fir(ctx, sctx, new Point(x, FarHillY(x) + 4), h, h * 0.42, 3);
            }
        }
        forest.Freeze();
        forestSnow.Freeze();
        dc.DrawGeometry(ForestBrush, null, forest);
        dc.DrawGeometry(Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0xC8, 0xD6, 0xF0))), null, forestSnow);

        // naher Schneeboden
        var near = new StreamGeometry();
        using (var ctx = near.Open())
        {
            ctx.BeginFigure(new Point(x0, H + 10), true, true);
            var pts = new List<Point>();
            for (double x = x0; x <= x1; x += 6) pts.Add(new Point(x, GroundY(x)));
            pts.Add(new Point(x1, H + 10));
            ctx.PolyLineTo(pts, true, true);
        }
        near.Freeze();
        dc.DrawGeometry(NearGroundBrush, null, near);
        var crest = new StreamGeometry();
        using (var ctx = crest.Open())
        {
            ctx.BeginFigure(new Point(x0, GroundY(x0)), false, false);
            var pts = new List<Point>();
            for (double x = x0 + 6; x <= x1; x += 6) pts.Add(new Point(x, GroundY(x)));
            ctx.PolyLineTo(pts, true, true);
        }
        crest.Freeze();
        dc.DrawGeometry(null, SnowCrestPen, crest);

        // Hütte rechts neben der Seitenleiste (deren Hinweistext unten sonst über ihr läge)
        double cabinX = Math.Min(300, W * 0.24);
        BuildCabin(dc, new Point(cabinX, GroundY(cabinX) + 4), Math.Clamp(H * 0.05, 28, 42), lights);

        // nahe Tannen verteilt, mit Schneehauben
        double treeX = W > 640 ? W * 0.915 : double.NaN;
        var firs = new StreamGeometry();
        var firSnow = new StreamGeometry();
        using (var ctx = firs.Open())
        using (var sctx = firSnow.Open())
        {
            int n = (int)Math.Clamp(W / 170, 4, 11);
            for (int i = 0; i < n; i++)
            {
                double x = W * (0.03 + rng.NextDouble() * 0.94);
                if (!double.IsNaN(treeX) && Math.Abs(x - treeX) < 70) continue;
                if (Math.Abs(x - cabinX) < 60) continue;
                double h = 30 + rng.NextDouble() * 34;
                Fir(ctx, sctx, new Point(x, GroundY(x) + 5), h, h * 0.4, 4);
            }
        }
        firs.Freeze();
        firSnow.Freeze();
        dc.DrawGeometry(ForestBrush, null, firs);
        dc.DrawGeometry(TreeSnowBrush, null, firSnow);

        // der Christbaum am rechten Rand, mit Lichterkette in Spiralen
        if (!double.IsNaN(treeX)) BuildTree(dc, new Point(treeX, GroundY(treeX) + 6), Math.Clamp(H * 0.2, 110, 190), rng, lights);

        // Tannenzweig mit Schleife in der Ecke oben links, daran hängt die Kugel
        BuildCornerBough(dc, rng);

        _lights = lights.ToArray();
    }

    static void Poly(StreamGeometryContext ctx, params Point[] pts)
    {
        ctx.BeginFigure(pts[0], true, true);
        ctx.PolyLineTo(pts[1..], true, false);
    }

    /// <summary>Tanne aus gestuften Etagen, auf jeder Etage eine Schneehaube.</summary>
    static void Fir(StreamGeometryContext ctx, StreamGeometryContext snow, Point basePt, double h, double halfW, int tiers)
    {
        double trunk = Math.Max(1.5, halfW * 0.12);
        Poly(ctx, new Point(basePt.X - trunk, basePt.Y), new Point(basePt.X - trunk, basePt.Y - h * 0.16),
            new Point(basePt.X + trunk, basePt.Y - h * 0.16), new Point(basePt.X + trunk, basePt.Y));
        for (int t = 0; t < tiers; t++)
        {
            double f = (double)t / tiers;
            double bottom = basePt.Y - h * (0.12 + f * 0.72);
            double top = basePt.Y - h * (0.12 + (f + 1.0 / tiers) * 0.72 + 0.16);
            double w = halfW * (1 - f * 0.68);
            double tipY = t == tiers - 1 ? basePt.Y - h : top;
            Poly(ctx, new Point(basePt.X - w, bottom), new Point(basePt.X - w * 0.35, bottom - h * 0.02),
                new Point(basePt.X, tipY), new Point(basePt.X + w * 0.35, bottom - h * 0.02), new Point(basePt.X + w, bottom));
            // Schnee: schmale Haube unter der Spitze der Etage
            double sy = tipY + (bottom - tipY) * 0.42;
            double sw = w * 0.42;
            Poly(snow, new Point(basePt.X, tipY + 1), new Point(basePt.X + sw * 0.9, sy), new Point(basePt.X + sw * 0.4, sy - 1.2),
                new Point(basePt.X, sy + 1), new Point(basePt.X - sw * 0.5, sy - 1.4), new Point(basePt.X - sw, sy + 0.6));
        }
    }

    void BuildCabin(DrawingContext dc, Point basePt, double s, List<(Point, int, double, double, double)> lights)
    {
        double w = s * 1.5, wallH = s * 0.78, roofH = s * 0.62;
        double l = basePt.X - w / 2, r = basePt.X + w / 2, wallTop = basePt.Y - wallH;
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            Poly(ctx, new Point(l, basePt.Y), new Point(l, wallTop), new Point(r, wallTop), new Point(r, basePt.Y));
            // Dach mit Überstand
            Poly(ctx, new Point(l - s * 0.14, wallTop + 1), new Point(basePt.X, wallTop - roofH), new Point(r + s * 0.14, wallTop + 1));
            // Schornstein
            double cx = basePt.X + w * 0.24;
            Poly(ctx, new Point(cx - s * 0.08, wallTop - roofH * 0.3), new Point(cx - s * 0.08, wallTop - roofH * 0.95),
                new Point(cx + s * 0.08, wallTop - roofH * 0.95), new Point(cx + s * 0.08, wallTop - roofH * 0.42));
            _chimney = new Point(cx, wallTop - roofH * 0.98);
        }
        body.Freeze();
        dc.DrawGeometry(CabinBrush, null, body);
        // Schnee auf dem Dach
        var roofSnow = new StreamGeometry();
        using (var ctx = roofSnow.Open())
        {
            var pts = new List<Point> { new(basePt.X, wallTop - roofH - 2.4) };
            int bumps = 6;
            for (int i = 0; i <= bumps; i++)
            {
                double u = (double)i / bumps;
                pts.Add(new Point(basePt.X + (r + s * 0.16 - basePt.X) * u, wallTop - roofH + (roofH + 1) * u - 2.6 + (i % 2) * 1.2));
            }
            for (int i = bumps; i >= 0; i--)
            {
                double u = (double)i / bumps;
                pts.Add(new Point(basePt.X + (r + s * 0.16 - basePt.X) * u, wallTop - roofH + (roofH + 1) * u + 1.4 + (i % 2) * 1.6));
            }
            Poly(ctx, pts.ToArray());
            pts.Clear();
            for (int i = 0; i <= bumps; i++)
            {
                double u = (double)i / bumps;
                pts.Add(new Point(basePt.X - (basePt.X - l + s * 0.16) * u, wallTop - roofH + (roofH + 1) * u - 2.6 + (i % 2) * 1.2));
            }
            for (int i = bumps; i >= 0; i--)
            {
                double u = (double)i / bumps;
                pts.Add(new Point(basePt.X - (basePt.X - l + s * 0.16) * u, wallTop - roofH + (roofH + 1) * u + 1.4 + (i % 2) * 1.6));
            }
            Poly(ctx, pts.ToArray());
        }
        roofSnow.Freeze();
        dc.DrawGeometry(RoofSnowBrush, null, roofSnow);

        // zwei Fenster mit Sprossen (das Licht kommt im langsamen Takt dazu) und die Tür
        double fw = s * 0.3, fh = s * 0.28, fy = wallTop + wallH * 0.28;
        _windows = new[]
        {
            new Rect(l + w * 0.12, fy, fw, fh),
            new Rect(r - w * 0.12 - fw, fy, fw, fh),
        };
        // Lichterkette an der Dachkante
        for (int i = 0; i <= 8; i++)
        {
            double u = i / 8.0;
            var p = u <= 0.5
                ? new Point(l - s * 0.1 + (basePt.X - l + s * 0.1) * u * 2, wallTop + 2 - roofH * u * 2 + 3)
                : new Point(basePt.X + (r + s * 0.1 - basePt.X) * (u - 0.5) * 2, wallTop - roofH + roofH * (u - 0.5) * 2 + 5);
            lights.Add((p, i % LightColors.Length, Ph(), Freq(1.6, 4), u * 0.4));
        }
    }

    void BuildTree(DrawingContext dc, Point basePt, double h, Random rng, List<(Point, int, double, double, double)> lights)
    {
        double halfW = h * 0.36;
        var tree = new StreamGeometry();
        using (var ctx = tree.Open())
        {
            int tiers = 5;
            for (int t = 0; t < tiers; t++)
            {
                double f = (double)t / tiers;
                double bottom = basePt.Y - h * (0.1 + f * 0.7);
                double tipY = basePt.Y - h * (0.1 + (f + 1.0 / tiers) * 0.7 + 0.2);
                if (t == tiers - 1) tipY = basePt.Y - h;
                double w = halfW * (1 - f * 0.7);
                // Etage mit gezacktem Saum
                var pts = new List<Point> { new(basePt.X - w, bottom) };
                int teeth = 5;
                for (int k = 1; k <= teeth; k++)
                {
                    double u = (double)k / teeth;
                    pts.Add(new Point(basePt.X - w + w * 2 * (u - 0.5 / teeth), bottom - h * 0.025));
                    pts.Add(new Point(basePt.X - w + w * 2 * u, bottom + (k == teeth ? 0 : h * 0.012)));
                }
                pts.Add(new Point(basePt.X, tipY));
                Poly(ctx, pts.ToArray());
            }
        }
        tree.Freeze();
        dc.DrawRectangle(TrunkBrush, null, new Rect(basePt.X - h * 0.035, basePt.Y - h * 0.12, h * 0.07, h * 0.12));
        dc.DrawGeometry(BigTreeBrush, null, tree);
        // Schnee auf den Etagen
        var snow = new StreamGeometry();
        using (var ctx = snow.Open())
            for (int t = 0; t < 5; t++)
            {
                double f = t / 5.0;
                double bottom = basePt.Y - h * (0.1 + f * 0.7);
                double w = halfW * (1 - f * 0.7);
                Poly(ctx, new Point(basePt.X - w * 0.92, bottom - 1), new Point(basePt.X - w * 0.3, bottom - h * 0.05),
                    new Point(basePt.X + w * 0.2, bottom - h * 0.035), new Point(basePt.X + w * 0.85, bottom - 1.5),
                    new Point(basePt.X + w * 0.3, bottom - h * 0.02), new Point(basePt.X - w * 0.3, bottom - h * 0.03));
            }
        snow.Freeze();
        dc.DrawGeometry(TreeSnowBrush, null, snow);

        _treeStar = new Point(basePt.X, basePt.Y - h - 2);

        // Lichterkette in drei Bögen um den Baum, Lichter gleichmäßig auf der Kette
        int idx = 0;
        for (int loop = 0; loop < 4; loop++)
        {
            double y0 = basePt.Y - h * (0.16 + loop * 0.18), y1 = y0 - h * 0.11;
            int n = 7 - loop;
            for (int i = 0; i < n; i++)
            {
                double u = (i + 0.5) / n;
                double y = y0 + (y1 - y0) * u;
                double rel = (basePt.Y - y) / h;
                double w = halfW * Math.Max(0.12, 1 - (rel - 0.1) / 0.9) * 0.82;
                // vorn sichtbare Hälfte der Spirale: von links nach rechts mit leichtem Durchhang
                double x = basePt.X - w + w * 2 * u;
                y += Math.Sin(u * Math.PI) * h * 0.03;
                lights.Add((new Point(x, y), idx++ % LightColors.Length, Ph(), Freq(1.2, 3.5), 0.4 + rel * 0.5));
            }
        }
    }

    void BuildCornerBough(DrawingContext dc, Random rng)
    {
        var stem = new StreamGeometry();
        var needles = new StreamGeometry();
        var pts = new List<Point>();
        using (var ctx = stem.Open())
        using (var nctx = needles.Open())
        {
            // Zweig hängt von links oben schräg herein
            var a = new Point(-10, 6);
            var b = new Point(70, 30);
            var c = new Point(120, 18);
            ctx.BeginFigure(a, false, false);
            ctx.QuadraticBezierTo(new Point(30, 30), b, true, true);
            ctx.QuadraticBezierTo(new Point(96, 30), c, true, true);
            for (double u = 0; u <= 1.0001; u += 0.035)
            {
                Point p;
                double heading;
                if (u < 0.6)
                {
                    double v = u / 0.6;
                    p = Quad(a, new Point(30, 30), b, v);
                    var q = Quad(a, new Point(30, 30), b, Math.Min(1, v + 0.02));
                    heading = Math.Atan2(q.Y - p.Y, q.X - p.X);
                }
                else
                {
                    double v = (u - 0.6) / 0.4;
                    p = Quad(b, new Point(96, 30), c, v);
                    var q = Quad(b, new Point(96, 30), c, Math.Min(1, v + 0.02));
                    heading = Math.Atan2(q.Y - p.Y, q.X - p.X);
                }
                double len = 9 * (1 - u * 0.45);
                for (int side = -1; side <= 1; side += 2)
                {
                    double ang = heading + side * (0.95 + rng.NextDouble() * 0.2);
                    nctx.BeginFigure(p, false, false);
                    nctx.LineTo(new Point(p.X + Math.Cos(ang) * len, p.Y + Math.Sin(ang) * len), true, false);
                }
            }
        }
        stem.Freeze();
        needles.Freeze();
        var needlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x34, 0x24)), 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var needleLight = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x8A, 0xD8, 0xA8)), 0.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var stemPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0C)), 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        dc.DrawGeometry(null, needlePen, needles);
        dc.DrawGeometry(null, stemPen, stem);
        dc.DrawGeometry(null, needleLight, needles);
        // Schleife am Ansatz der Kugel
        var bowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA8, 0x16, 0x28)));
        var anchor = BallAnchor;
        var bow = new StreamGeometry();
        using (var ctx = bow.Open())
        {
            Poly(ctx, anchor, new Point(anchor.X - 8, anchor.Y - 5), new Point(anchor.X - 8, anchor.Y + 4));
            Poly(ctx, anchor, new Point(anchor.X + 8, anchor.Y - 5), new Point(anchor.X + 8, anchor.Y + 4));
            Poly(ctx, anchor, new Point(anchor.X - 4, anchor.Y + 10), new Point(anchor.X - 1, anchor.Y + 10));
            Poly(ctx, anchor, new Point(anchor.X + 4, anchor.Y + 10), new Point(anchor.X + 1, anchor.Y + 10));
        }
        bow.Freeze();
        dc.DrawGeometry(bowBrush, null, bow);
        dc.DrawEllipse(bowBrush, null, anchor, 2.2, 2.2);
    }

    static Point Quad(Point a, Point c, Point b, double u)
    {
        double v = 1 - u;
        return new Point(v * v * a.X + 2 * v * u * c.X + u * u * b.X, v * v * a.Y + 2 * v * u * c.Y + u * u * b.Y);
    }

    // ───────────────────────────── Zeichnen ─────────────────────────────

    // Farbschlüssel: 1 Haupt, 2 Zweit, 3 Lichter, 4 Mond, 5 Sterne, 6 Schnee, 7 Nordlicht A, 8 Nordlicht B, 9 Faden,
    // 10 Fenster, 11 Rauch, 12 Tanne, 13 Beere, 14 Kugel, 15 Nadeln, 20+ Lichterkette
    readonly Dictionary<long, Pen> _pens = new();
    readonly Dictionary<int, Brush> _solids = new();
    readonly Dictionary<int, Brush> _glows = new();
    (Color, Color, Color, Color) _cacheColors;
    Color _auroraA, _auroraB;

    Color ColorOf(int key) => key switch
    {
        2 => _secondary, 3 => _highlight, 4 => MoonColor, 5 => StarColor, 6 => SnowColor, 7 => _auroraA, 8 => _auroraB,
        9 => ThreadColor, 10 => WindowColor, 11 => SmokeColor, 12 => PineColor, 13 => BerryColor, 14 => BallRed,
        15 => NeedleColor,
        >= 20 and < 30 => LightColors[key - 20],
        _ => _primary,
    };

    void ValidateCaches()
    {
        // Farbe des Nordlichts in Stufen, damit die Pinsel-Caches beim Umfärben nicht überlaufen
        double a = Math.Round(_alarm * 24) / 24;
        _auroraA = MoodPalette.Mix(AuroraGreen, AuroraRed, a);
        _auroraB = MoodPalette.Mix(AuroraTeal, AuroraRose, a);
        var now = (_primary, _secondary, _highlight, _auroraA);
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

    static readonly Brush MoonDisk = Frozen(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.38, 0.36),
        Center = new Point(0.45, 0.45),
        RadiusX = 0.62,
        RadiusY = 0.62,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0),
            new GradientStop(MoonColor, 0.6),
            new GradientStop(Color.FromRgb(0xA8, 0xB8, 0xD8), 1),
        },
    });

    static readonly Brush MoonCrater = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x8A, 0x98, 0xB8)));

    static readonly string Perf = Environment.GetEnvironmentVariable("AXON_PERF") ?? "";

    void DrawSky()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _skyVisual.RenderOpen();
        dc.DrawRectangle(SkyBrush, null, new Rect(0, 0, W, H));
        if (Perf.Contains("xm-nosky")) return;

        var moon = MoonCenter();
        double mr = MoonRadius;
        double intro = IntroRunning ? Smooth(_introT / 1.6) : 1;
        foreach (var (nx, ny, z, p, f) in _stars)
        {
            double x = nx * W + _parallax.X * 2 * z, y = ny * H + _parallax.Y * 1.5 * z;
            double dmx = x - moon.X, dmy = y - moon.Y;
            if (dmx * dmx + dmy * dmy < mr * mr * 5) continue;
            double tw = 0.55 + 0.45 * Math.Sin(_t * f + p);
            double a = (0.2 + 0.6 * z) * tw * intro;
            double r = 0.5 + z * 0.9;
            dc.DrawEllipse(SolidFor(5, a), null, new Point(x, y), r, r);
            if (z > 0.8)
            {
                // helle Sterne mit vier Strahlen
                dc.DrawEllipse(GlowFor(5, a * 0.5), null, new Point(x, y), r * 5, r * 5);
                var pen = PenFor(5, a * 0.45, 0.6);
                double ray = r * 4.5 * (0.8 + 0.2 * tw);
                dc.DrawLine(pen, new Point(x - ray, y), new Point(x + ray, y));
                dc.DrawLine(pen, new Point(x, y - ray), new Point(x, y + ray));
            }
        }

        // Mond: kühl und klar
        dc.PushOpacity(intro);
        dc.DrawEllipse(MoonDisk, null, moon, mr, mr);
        dc.DrawEllipse(MoonCrater, null, new Point(moon.X - mr * 0.3, moon.Y - mr * 0.18), mr * 0.2, mr * 0.17);
        dc.DrawEllipse(MoonCrater, null, new Point(moon.X + mr * 0.25, moon.Y + mr * 0.28), mr * 0.26, mr * 0.22);
        dc.DrawEllipse(MoonCrater, null, new Point(moon.X + mr * 0.36, moon.Y - mr * 0.32), mr * 0.12, mr * 0.11);
        dc.Pop();
    }

    /// <summary>Weiche Ebene: Nordlicht, Mondhof, Wolkenschleier – in halber Auflösung gerastert.</summary>
    void DrawMist()
    {
        double W = _size.Width, H = _size.Height;
        using var dc = _mistVisual.RenderOpen();
        if (Perf.Contains("nonebula")) return;
        double big = Math.Max(W, H);
        double intro = IntroRunning ? Smooth((_introT - 0.2) / 1.6) : 1;

        // Nordlicht: zwei Vorhänge aus eng stehenden, senkrecht gestreckten Lichtern, die sich zu einem wehenden Band
        // überlagern; jedes Licht atmet für sich, die Wellenform wandert langsam
        double strength = (Mood == Mood.Working ? 1.3 : 1) * intro;
        if (!Perf.Contains("xm-noaurora"))
            for (int band = 0; band < 2; band++)
            {
                int n = 18;
                double baseY = H * (band == 0 ? 0.19 : 0.29);
                double drift = _t * (0.03 + band * 0.012);
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / (n - 1);
                    double x = W * (-0.06 + u * 1.12) + Math.Sin(drift * 2 + u * 5 + band) * W * 0.02;
                    double y = baseY + Math.Sin(u * 4.4 + drift * 3 + band * 1.7) * H * 0.08;
                    double pulse = 0.5 + 0.5 * Math.Sin(_t * (0.25 + i * 0.017) + i * 1.3 + band * 2.2);
                    // zu den Rändern des Bandes hin schwächer, damit es nicht wie ein Balken aussieht
                    double reach = 0.55 + 0.45 * Math.Sin(u * Math.PI * (band == 0 ? 1.4 : 1.1) + band);
                    double a = (band == 0 ? 0.2 : 0.14) * (0.35 + 0.65 * pulse) * Math.Max(0.15, reach) * strength;
                    dc.DrawEllipse(GlowFor(band == 0 ? 7 : 8, a), null, new Point(x, y), W * 0.05, H * (0.15 + 0.07 * pulse));
                }
            }

        // ganz zarte Schleier in Gold und Eisblau
        for (int i = 0; i < 2; i++)
        {
            double cx = W * (0.5 + 0.38 * Math.Sin(_t * (0.017 + i * 0.007) + i * 2.1));
            double cy = H * (0.5 + 0.25 * Math.Cos(_t * (0.015 + i * 0.006) + i * 1.3));
            dc.DrawEllipse(GlowFor(i == 0 ? 1 : 2, 0.035), null, new Point(cx, cy), big * 0.5, big * 0.38);
        }

        // Mondhof
        var moon = MoonCenter();
        double mr = MoonRadius;
        double moonIntro = IntroRunning ? Smooth(_introT / 1.6) : 1;
        double breath = 0.92 + 0.08 * Math.Sin(_t * 0.35);
        dc.DrawEllipse(GlowFor(4, 0.16 * breath * moonIntro), null, moon, mr * 5, mr * 5);
        dc.DrawEllipse(GlowFor(4, 0.26 * breath * moonIntro), null, moon, mr * 2.1, mr * 2.1);

        // Schimmer über dem Schnee vor den Hügeln
        for (int i = 0; i < 2; i++)
        {
            double cx = W * (0.5 + 0.45 * Math.Sin(_t * (0.025 + i * 0.01) + i * 2.4));
            double cy = H - Math.Max(46, H * 0.07) + i * 8;
            dc.DrawEllipse(GlowFor(6, 0.05), null, new Point(cx, cy), W * (0.3 + i * 0.08), 30 + i * 8);
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
        if (Perf.Contains("xm-noslow")) return;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H)));

        // Szene-Lichter: Fenster, Rauch, Lichterkette, Stern – mit der Szene verschoben
        dc.PushTransform(_groundShift);
        DrawSceneLights(dc);
        dc.Pop();

        // Tannenzweige: jeder in eigener zwischengespeicherter Ebene. Neu gerastert wird ein Zweig nur, wenn er neue
        // Nadeln/Punkte bekommen hat (höchstens ~8/s); Ein- und Ausblenden läuft über die Deckkraft der Ebene, und die
        // wachsende Spitze zeichnet der Bildtakt stufenlos (DrawTips).
        double boughIntro = IntroRunning ? Smooth((_introT - 1.3) / 1.0) : 1;
        foreach (var v in _boughs)
        {
            double op = Perf.Contains("nodend") ? 0 : v.Alpha * boughIntro;
            if (Math.Abs(v.Visual.Opacity - op) > 0.002) v.Visual.Opacity = op;
            v.RedrawAcc += dt;
            long sig = BoughSignature(v);
            if (sig != v.Sig && (v.RedrawAcc >= 0.12 || dt <= 0 || v.Sig < 0))
            {
                v.RedrawAcc = 0;
                v.Sig = sig;
                DrawBough(v);
            }
        }

        // Eiskristalle
        foreach (var c in _crystals)
        {
            double vis = Visible(c);
            if (vis <= 0) continue;
            double pop = vis >= 1 ? 1 : 1 + 0.7 * Math.Sin(vis * Math.PI) * (1 - vis * 0.5);
            double glowA = Math.Min(1, (0.1 + 0.2 * c.Z) * c.Twinkle + 0.7 * c.Act) * vis;
            double gr = c.R * (5 + 6 * c.Act) * pop;
            double rr = c.R * vis * pop;
            var p = new Point(c.X, c.Y);
            int key = c.Tint > 0 ? 2 : 1;
            if (!Perf.Contains("noglow")) dc.DrawEllipse(GlowFor(key, glowA), null, p, gr, gr);
            double core = (0.45 + 0.4 * c.Z * c.Twinkle + 0.4 * c.Act) * vis;
            dc.DrawEllipse(SolidFor(3, core), null, p, rr * 0.55, rr * 0.55);
            // große Kristalle zeigen sechs feine Strahlen, die sich langsam drehen
            if (c.Z > 0.55)
            {
                double len = rr * (2.4 + 1.6 * c.Act) * (0.9 + 0.1 * c.Twinkle);
                var pen = PenFor(3, core * 0.55, 0.7);
                for (int k = 0; k < 3; k++)
                {
                    double a = c.Spin + k * Math.PI / 3;
                    double dx = Math.Cos(a) * len, dy = Math.Sin(a) * len;
                    dc.DrawLine(pen, new Point(p.X - dx, p.Y - dy), new Point(p.X + dx, p.Y + dy));
                }
            }
        }

        // Kugel – schwingt langsam, der 30/s-Takt genügt. Schnee und Sternenstaub laufen im Bildtakt (DrawFast),
        // nur ohne Animation stehen sie hier still.
        if (!Perf.Contains("xm-noball")) DrawBall(dc);
        if (Level == AnimationLevel.Off)
        {
            DrawSnow(dc);
            DrawGlitter(dc);
        }

        dc.Pop();
    }

    static long BoughSignature(Bough v)
    {
        long s = v.Branches.Count;
        foreach (var b in v.Branches) s = s * 31 + b.Pts.Count * 4099L + b.Needles.Count * 7L + b.Berries;
        return s;
    }

    /// <summary>Alle Zweige neu rastern lassen (nach Größen- oder Stimmungswechsel).</summary>
    void InvalidateBoughs()
    {
        foreach (var v in _boughs) v.Sig = -1;
    }

    /// <summary>Einen Zweig in seine Ebene zeichnen – volle Deckkraft, ohne die wachsende Spitze.</summary>
    void DrawBough(Bough v)
    {
        using var dc = v.Visual.RenderOpen();
        // Fester, fensterfüllender Rahmen: Ohne ihn hängt die Ausrichtung des Zwischenspeichers an den Umrissen des
        // Zweigs – wächst er, verschiebt sich das Bild bei jedem Neuzeichnen um Bruchteile eines Pixels (Zweig wackelt).
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, Math.Max(1, _size.Width), Math.Max(1, _size.Height)));
        const double va = 1;
        {
            foreach (var b in v.Branches)
            {
                b.DrawnPts = b.Pts.Count;
                if (b.Pts.Count < 2) continue;
                if (b.Geo == null || b.GeoCount != b.Pts.Count)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(b.Pts[0], false, false);
                        var pts = new List<Point>(b.Pts.Count);
                        for (int k = 1; k < b.Pts.Count; k++) pts.Add(b.Pts[k]);
                        ctx.PolyLineTo(pts, true, true);
                    }
                    g.Freeze();
                    b.Geo = g;
                    b.GeoCount = b.Pts.Count;
                }
                if (b.NeedleGeo == null || b.NeedleCount != b.Needles.Count)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                        for (int k = 0; k + 2 < b.Needles.Count; k += 3) Poly(ctx, b.Needles[k], b.Needles[k + 1], b.Needles[k + 2]);
                    g.Freeze();
                    b.NeedleGeo = g;
                    b.NeedleCount = b.Needles.Count;
                }
                // dunkle Nadeln mit einem Hauch Raureif (eine Füllung, kein Strich)
                dc.DrawGeometry(SolidFor(15, 0.95 * va), null, b.NeedleGeo);
                dc.DrawGeometry(null, PenFor(12, 0.95 * va, b.Width), b.Geo);
                dc.DrawGeometry(null, PenFor(1, (0.18 - 0.03 * b.Depth) * va, Math.Max(0.5, b.Width * 0.3)), b.Geo);
                if (b.Berries > 0)
                {
                    for (int k = 0; k < b.Berries; k++)
                    {
                        double ang = b.BerryAngle + k * 2.1;
                        var bp = new Point(b.Tip.X + Math.Cos(ang) * 3, b.Tip.Y + Math.Sin(ang) * 3);
                        dc.DrawEllipse(SolidFor(13, 0.95 * va), null, bp, 2.6, 2.6);
                        dc.DrawEllipse(SolidFor(3, 0.55 * va), null, new Point(bp.X - 0.8, bp.Y - 0.8), 0.7, 0.7);
                    }
                    dc.DrawEllipse(GlowFor(13, 0.2 * va), null, b.Tip, 12, 12);
                }
            }
        }
    }

    void DrawSceneLights(DrawingContext dc)
    {
        // Fensterlicht flackert warm wie ein Kaminfeuer
        double fire = 0.82 + 0.1 * Math.Sin(_t * 5.3) + 0.05 * Math.Sin(_t * 11.7 + 1) + 0.03 * Math.Sin(_t * 2.1);
        double win = Math.Clamp(LightOn(0) * fire * (Mood == Mood.Working ? 1.1 : 1) + _litFlash * 0.2, 0, 1.3);
        foreach (var r in _windows)
        {
            if (win <= 0.02) break;
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            dc.DrawEllipse(GlowFor(10, 0.3 * win), null, c, r.Width * 2.6, r.Height * 2.4);
            dc.DrawRectangle(SolidFor(10, 0.85 * win), null, r);
            // Fensterkreuz
            dc.DrawLine(WindowPen, new Point(c.X, r.Top), new Point(c.X, r.Bottom));
            dc.DrawLine(WindowPen, new Point(r.Left, c.Y), new Point(r.Right, c.Y));
        }

        // Rauch aus dem Schornstein
        foreach (var p in _puffs)
        {
            double life = p.Age / p.Life;
            double a = Smooth(life / 0.15) * (1 - life) * 0.16;
            double s = p.Size * (1 + life * 2.6);
            dc.DrawEllipse(GlowFor(11, a), null, new Point(p.X, p.Y), s * 1.8, s * 1.5);
        }

        // Lichterkette: jedes Licht pulsiert in seinem Takt
        bool working = Mood == Mood.Working;
        foreach (var (at, color, phase, freq, order) in _lights)
        {
            double on = LightOn(order);
            if (on <= 0.01) continue;
            double tw = 0.7 + 0.3 * Math.Sin(_t * freq * (working ? 1.6 : 1) + phase);
            double l = Math.Min(1.2, on * tw + _litFlash * 0.4);
            dc.DrawEllipse(GlowFor(20 + color, 0.42 * l), null, at, 7, 7);
            dc.DrawEllipse(SolidFor(20 + color, Math.Min(1, 0.95 * l)), null, at, 1.5, 1.5);
        }

        // Stern auf der Baumspitze
        if (!double.IsNaN(_treeStar.X))
        {
            double on = LightOn(0.95);
            if (on > 0.01)
            {
                double pulse = 0.85 + 0.15 * Math.Sin(_t * 0.9);
                double l = on * pulse + _starFlash * 0.9;
                dc.DrawEllipse(GlowFor(20, Math.Min(1, 0.4 * l)), null, _treeStar, 20 + 14 * _starFlash, 20 + 14 * _starFlash);
                dc.DrawGeometry(SolidFor(20, Math.Min(1, 0.95 * on)), null, StarShape(_treeStar, 7 + 2 * _starFlash));
                dc.DrawEllipse(SolidFor(3, Math.Min(1, 0.8 * l)), null, _treeStar, 1.8, 1.8);
                if (_starFlash > 0.05)
                {
                    var pen = PenFor(3, _starFlash * 0.6, 0.9);
                    double ray = 26 * _starFlash + 8;
                    dc.DrawLine(pen, new Point(_treeStar.X - ray, _treeStar.Y), new Point(_treeStar.X + ray, _treeStar.Y));
                    dc.DrawLine(pen, new Point(_treeStar.X, _treeStar.Y - ray), new Point(_treeStar.X, _treeStar.Y + ray));
                }
            }
        }
    }

    static readonly Pen WindowPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x12)), 1.1));

    readonly Dictionary<int, Geometry> _starShapes = new();

    /// <summary>Fünfzackiger Stern – nach Größe zwischengespeichert (die Spitze bewegt sich nicht).</summary>
    Geometry StarShape(Point c, double r)
    {
        int key = (int)Math.Round(r * 4);
        if (!_starShapes.TryGetValue(key, out var g) || _starShapes.Count > 40)
        {
            if (_starShapes.Count > 40) _starShapes.Clear();
            var s = new StreamGeometry();
            using (var ctx = s.Open())
            {
                var pts = new Point[10];
                for (int i = 0; i < 10; i++)
                {
                    double a = -Math.PI / 2 + i * Math.PI / 5;
                    double rr = (i % 2 == 0 ? 1 : 0.42) * key / 4.0;
                    pts[i] = new Point(Math.Cos(a) * rr, Math.Sin(a) * rr);
                }
                Poly(ctx, pts);
            }
            s.Freeze();
            g = s;
            _starShapes[key] = g;
        }
        // Verschiebung über eine eigene Kopie – die Form selbst bleibt eingefroren und wiederverwendbar
        var moved = new GeometryGroup { Transform = new TranslateTransform(c.X, c.Y), Children = { g } };
        moved.Freeze();
        return moved;
    }

    void DrawSnow(DrawingContext dc)
    {
        double intro = IntroRunning ? Smooth((_introT - 0.4) / 1.2) : 1;
        foreach (var f in _flakes)
        {
            double a = (0.28 + 0.55 * f.Z) * intro;
            var c = new Point(f.X, f.Y);
            dc.DrawEllipse(SolidFor(6, a), null, c, f.Size, f.Size);
            if (f.Z > 0.75) dc.DrawEllipse(GlowFor(6, a * 0.35), null, c, f.Size * 3.5, f.Size * 3.5);
        }
    }

    void DrawGlitter(DrawingContext dc)
    {
        foreach (var g in _glitter)
        {
            double life = g.Age / g.Life;
            double a = Math.Sin(Math.Min(1, life * 6) * Math.PI / 2) * (1 - life) * (0.65 + 0.35 * Math.Sin(g.Age * 16 + g.X));
            var c = new Point(g.X, g.Y);
            dc.DrawEllipse(GlowFor(g.Alt ? 3 : 20, a * 0.6), null, c, g.Size * 4, g.Size * 4);
            dc.DrawEllipse(SolidFor(3, a), null, c, g.Size * 0.6, g.Size * 0.6);
        }
    }

    void DrawBall(DrawingContext dc)
    {
        var a = BallAnchor;
        double intro = IntroRunning ? Smooth((_introT - 0.8) / 1.0) : 1;
        if (intro <= 0) return;
        double len = Math.Max(10, _ballLen);
        double sw = Math.Clamp(_ballSwing, -0.6, 0.6);
        var center = new Point(a.X + Math.Sin(sw) * len, a.Y + Math.Cos(sw) * len);
        const double r = 9;
        dc.PushOpacity(intro);
        dc.DrawLine(PenFor(9, 0.4, 0.8), a, new Point(center.X - Math.Sin(sw) * (r + 3), center.Y - Math.Cos(sw) * (r + 3)));
        // beim Warten leuchtet die Kugel in der Stimmungsfarbe
        bool calling = Mood is Mood.Attention or Mood.Error;
        double pulse = calling ? 0.6 + 0.4 * Math.Sin(_t * 3.2) : 0.5;
        dc.DrawEllipse(GlowFor(calling ? 2 : 14, (calling ? 0.5 : 0.25) * pulse), null, center, 30, 30);
        dc.PushTransform(new RotateTransform(-sw * 57.3, center.X, center.Y));
        // Kappe
        dc.DrawRectangle(SolidFor(20, 0.95), null, new Rect(center.X - 2.6, center.Y - r - 3.2, 5.2, 3.6));
        dc.DrawEllipse(BallBrush, null, center, r, r);
        // Streifen und Glanzlicht
        dc.DrawEllipse(null, PenFor(20, 0.55, 1.1), center, r * 0.98, r * 0.32);
        dc.DrawEllipse(SolidFor(3, 0.55), null, new Point(center.X - r * 0.38, center.Y - r * 0.4), r * 0.2, r * 0.14);
        dc.DrawEllipse(GlowFor(3, 0.4), null, new Point(center.X - r * 0.35, center.Y - r * 0.35), r * 0.7, r * 0.7);
        if (calling) dc.DrawEllipse(null, PenFor(2, 0.5 * pulse, 1), center, r + 1, r + 1);
        dc.Pop();
        dc.Pop();
    }

    static readonly Brush BallBrush = Frozen(new RadialGradientBrush
    {
        GradientOrigin = new Point(0.35, 0.32),
        Center = new Point(0.45, 0.45),
        RadiusX = 0.6,
        RadiusY = 0.6,
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xFF, 0x6A, 0x78), 0),
            new GradientStop(BallRed, 0.55),
            new GradientStop(Color.FromRgb(0x5A, 0x08, 0x14), 1),
        },
    });

    sealed class FastLayer
    {
        public readonly DrawingVisual Visual = new();
        bool _empty = true;

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

    /// <summary>Schnelle Ebenen, jedes Bild: Zweigspitzen, Schnee + Sternenstaub, Funken, Sternschnuppen + Schlitten, Wellen.</summary>
    void DrawFast()
    {
        if (Perf.Contains("xm-nofast")) return;
        bool tips = false;
        if (!Perf.Contains("nodend"))
            foreach (var v in _boughs)
                foreach (var b in v.Branches)
                    tips |= b.Growing || b.DrawnPts < b.Pts.Count;
        using (var dc = _tipLayer.Open(tips)) if (dc != null) DrawTips(dc);
        using (var dc = _snowLayer.Open((_flakes.Count > 0 || _glitter.Count > 0) && !Perf.Contains("xm-nosnow")))
            if (dc != null)
            {
                DrawSnow(dc);
                DrawGlitter(dc);
            }
        using (var dc = _sparkLayer.Open((_sparks.Count > 0 || _bpulses.Count > 0) && !Perf.Contains("xm-nosparks"))) if (dc != null) DrawSparks(dc);
        using (var dc = _skyFxLayer.Open((_shooters.Count > 0 || _sleighs.Count > 0) && !Perf.Contains("xm-nosky-fx"))) if (dc != null) DrawSkyFx(dc);
        using (var dc = _waveLayer.Open(_ripples.Count > 0)) if (dc != null) DrawWaves(dc);
    }

    void ClearFast()
    {
        foreach (var l in FastLayers) l.Clear();
    }

    /// <summary>
    /// Wachsende Zweigspitzen im Bildtakt: das Stück vom zuletzt zwischengespeicherten Punkt bis zur Spitze, die seit dem
    /// letzten langsamen Schritt stufenlos weitergerechnet wird – so gleitet das Wachstum, statt in Sprüngen zu ruckeln.
    /// </summary>
    void DrawTips(DrawingContext dc)
    {
        double boughIntro = IntroRunning ? Smooth((_introT - 1.3) / 1.0) : 1;
        double ahead = Math.Min(_sinceStep, 0.06);
        var pts = new List<Point>(8);
        foreach (var v in _boughs)
        {
            double va = v.Alpha * boughIntro;
            if (va <= 0.01) continue;
            foreach (var b in v.Branches)
            {
                if (!b.Growing && b.DrawnPts >= b.Pts.Count) continue;
                pts.Clear();
                for (int k = Math.Max(0, b.DrawnPts - 1); k < b.Pts.Count; k++) pts.Add(b.Pts[k]);
                var tip = b.Tip;
                if (b.Growing)
                {
                    double step = v.Speed * (1 - 0.18 * b.Depth) * ahead;
                    tip = new Point(b.Tip.X + Math.Cos(b.Heading) * step, b.Tip.Y + Math.Sin(b.Heading) * step);
                    pts.Add(tip);
                }
                if (pts.Count >= 2)
                {
                    var g = new StreamGeometry();
                    using (var ctx = g.Open())
                    {
                        ctx.BeginFigure(pts[0], false, false);
                        ctx.PolyLineTo(pts.GetRange(1, pts.Count - 1), true, true);
                    }
                    g.Freeze();
                    dc.DrawGeometry(null, PenFor(12, 0.95 * va, b.Width), g);
                    dc.DrawGeometry(null, PenFor(1, (0.18 - 0.03 * b.Depth) * va, Math.Max(0.5, b.Width * 0.3)), g);
                }
                if (b.Growing) dc.DrawEllipse(GlowFor(3, 0.45 * va), null, tip, 6, 6);
            }
        }
    }

    void DrawSparks(DrawingContext dc)
    {
        // Sternfunken im flachen Bogen, mit glitzernder Spur
        foreach (var s in _sparks)
        {
            double t = Math.Clamp(s.P, 0, 1);
            int key = s.Alt ? 2 : 3;
            var head = SparkAt(s, t);
            var prev = head;
            for (int k = 1; k <= 3; k++)
            {
                double u = Math.Max(0, t - k * 0.07);
                var pt = SparkAt(s, u);
                double fade = 1 - k / 4.0;
                dc.DrawLine(PenFor(key, 0.75 * fade * s.Energy, 2.2 * fade + 0.5), prev, pt);
                prev = pt;
                if (u <= 0) break;
            }
            dc.DrawEllipse(GlowFor(key, Math.Min(1, 0.35 + 0.65 * s.Energy)), null, head, 5 + 6 * s.Energy, 5 + 6 * s.Energy);
        }

        foreach (var p in _bpulses)
            dc.DrawEllipse(GlowFor(1, 0.95 * p.V.Alpha), null, PointOnPolyline(p.B.Pts, p.S), 10, 10);
    }

    void DrawSkyFx(DrawingContext dc)
    {
        foreach (var s in _shooters)
        {
            double life = s.Age / s.Life;
            double a = Math.Sin(Math.Min(1, life * 4) * Math.PI / 2) * (1 - life);
            var head = new Point(s.X, s.Y);
            double sp = Math.Sqrt(s.VX * s.VX + s.VY * s.VY);
            var dir = new Vector(s.VX / sp, s.VY / sp);
            var prev = head;
            for (int k = 1; k <= 4; k++)
            {
                var pt = head - dir * (k * 22);
                double fade = 1 - k / 5.0;
                dc.DrawLine(PenFor(5, 0.8 * a * fade, 1.8 * fade + 0.4), prev, pt);
                prev = pt;
            }
            dc.DrawEllipse(GlowFor(5, 0.8 * a), null, head, 9, 9);
        }

        var images = SleighImages();
        foreach (var s in _sleighs)
        {
            int frame = (int)(s.Gallop % 1 * SleighFrames) % SleighFrames;
            // vor dem Mond blendet der Schlitten weich ein und aus, damit er nicht abrupt am Rand auftaucht
            double edge = Math.Min(s.X + SleighBoxW, _size.Width + SleighBoxW - s.X) / (SleighBoxW * 1.5);
            dc.PushOpacity(Math.Clamp(edge, 0, 1));
            dc.DrawImage(images[frame + (s.Dir > 0 ? 0 : SleighFrames)], new Rect(s.X - SleighBoxW / 2, s.Y - SleighBoxH / 2, SleighBoxW, SleighBoxH));
            dc.Pop();
        }
    }

    void DrawWaves(DrawingContext dc)
    {
        foreach (var r in _ripples)
        {
            double s = 1 - r.R / r.Max;
            dc.DrawEllipse(null, PenFor(1, 0.24 * s, 1.5), r.C, r.R, r.R);
            dc.DrawEllipse(null, PenFor(2, 0.07 * s, 14), r.C, r.R, r.R);
        }
    }

    // Schlitten mit zwei Rentieren als vorgerenderte Bilder (wie die Fledermäuse im Halloween-Thema):
    // 8 Galoppstufen × 2 Richtungen, einmal berechnet.
    const int SleighFrames = 8;
    const double SleighBoxW = 150, SleighBoxH = 50;
    static ImageSource[]? _sleighImages;

    static ImageSource[] SleighImages()
    {
        if (_sleighImages != null) return _sleighImages;
        var body = Frozen(new SolidColorBrush(Color.FromArgb(0xF0, 0x0C, 0x10, 0x1C)));
        var line = Frozen(new Pen(body, 1.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var rein = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x0C, 0x10, 0x1C)), 0.7));
        var glow = Frozen(new RadialGradientBrush(Color.FromArgb(0x22, 0xC8, 0xD8, 0xFF), Color.FromArgb(0, 0xC8, 0xD8, 0xFF)));
        var nose = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4A, 0x4A)));
        var images = new ImageSource[SleighFrames * 2];
        for (int i = 0; i < images.Length; i++)
        {
            double phase = Math.PI * 2 * (i % SleighFrames) / SleighFrames;
            double dir = i < SleighFrames ? 1 : -1;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawEllipse(glow, null, new Point(SleighBoxW / 2, SleighBoxH / 2), SleighBoxW * 0.55, SleighBoxH * 0.6);
                // Zeichnung nach rechts fliegend; für links gespiegelt
                dc.PushTransform(new MatrixTransform(dir, 0, 0, 1, dir > 0 ? 0 : SleighBoxW, 0));
                double y = 26;
                // Schlitten hinten
                var sl = new StreamGeometry();
                using (var ctx = sl.Open())
                {
                    Poly(ctx, new Point(8, y - 10), new Point(12, y - 4), new Point(34, y - 4), new Point(38, y - 12), new Point(34, y - 13),
                        new Point(31, y - 7), new Point(14, y - 7), new Point(11, y - 12));
                    // Kufe
                    Poly(ctx, new Point(6, y + 1), new Point(36, y + 1), new Point(41, y - 3), new Point(39, y - 3.6), new Point(35.4, y - 0.6), new Point(6, y - 0.6));
                }
                sl.Freeze();
                dc.DrawGeometry(body, null, sl);
                dc.DrawLine(line, new Point(14, y - 4), new Point(14, y));
                dc.DrawLine(line, new Point(30, y - 4), new Point(30, y));
                // Sack auf dem Schlitten
                dc.DrawEllipse(body, null, new Point(17, y - 13), 6, 5.5);
                // zwei Rentiere
                for (int r = 0; r < 2; r++)
                {
                    double rx = 62 + r * 38, ph = phase + r * 1.6;
                    double ry = y - 8 + Math.Sin(ph) * 1.6;
                    dc.DrawLine(rein, new Point(37, y - 9), new Point(rx - 9, ry - 1));
                    var deer = new StreamGeometry();
                    using (var ctx = deer.Open())
                    {
                        // Rumpf, Hals, Kopf
                        ctx.BeginFigure(new Point(rx - 11, ry), true, true);
                        ctx.QuadraticBezierTo(new Point(rx - 11, ry - 5.5), new Point(rx - 2, ry - 5.2), true, false);
                        ctx.LineTo(new Point(rx + 7, ry - 5), true, false);
                        ctx.LineTo(new Point(rx + 10, ry - 12), true, false);
                        ctx.LineTo(new Point(rx + 15, ry - 12.5), true, false);
                        ctx.LineTo(new Point(rx + 16, ry - 10.8), true, false);
                        ctx.LineTo(new Point(rx + 12, ry - 9.5), true, false);
                        ctx.LineTo(new Point(rx + 9, ry - 2), true, false);
                        ctx.QuadraticBezierTo(new Point(rx, ry + 2), new Point(rx - 11, ry), true, false);
                    }
                    deer.Freeze();
                    dc.DrawGeometry(body, null, deer);
                    // Geweih
                    dc.DrawLine(line, new Point(rx + 11, ry - 12), new Point(rx + 9, ry - 18));
                    dc.DrawLine(line, new Point(rx + 9.6, ry - 16), new Point(rx + 6.5, ry - 18.5));
                    dc.DrawLine(line, new Point(rx + 10.2, ry - 14.4), new Point(rx + 13, ry - 18));
                    // Beine im Galopp: vorne und hinten gegenläufig
                    double fa = Math.Sin(ph) * 0.7, ba = -Math.Sin(ph) * 0.7;
                    foreach (var (hx, sw) in new[] { (rx + 5, fa), (rx + 3, fa * 0.6), (rx - 8, ba), (rx - 6, ba * 0.6) })
                    {
                        var hip = new Point(hx, ry - 1);
                        var knee = new Point(hip.X + Math.Sin(sw) * 4.5, hip.Y + Math.Cos(sw) * 4.5);
                        var hoof = new Point(knee.X + Math.Sin(sw * 1.6 + 0.3) * 4, knee.Y + Math.Cos(sw * 1.6 + 0.3) * 4);
                        dc.DrawLine(line, hip, knee);
                        dc.DrawLine(line, knee, hoof);
                    }
                    // das vordere Rentier mit leuchtender Nase
                    if (r == 1) dc.DrawEllipse(nose, null, new Point(rx + 16, ry - 11), 1.4, 1.4);
                }
                dc.Pop();
            }
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(SleighBoxW * 2), (int)(SleighBoxH * 2), 192, 192, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            images[i] = bmp;
        }
        return _sleighImages = images;
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
