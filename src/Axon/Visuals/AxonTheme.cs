using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Axon.Visuals;

/// <summary>
/// Ein Farb- und Stimmungsthema. Die Oberfläche holt ihre Pinsel per <c>DynamicResource</c> aus den App-Ressourcen;
/// <see cref="ThemeManager.Apply"/> tauscht sie aus, ohne das Fenster neu aufzubauen.
/// </summary>
public sealed class AxonTheme
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Subtitle { get; init; } = "";

    // Grundfarben der Stimmungen (Zustandspunkte, Ringe, Licht)
    public Color Mint { get; init; }
    public Color Emerald { get; init; }
    public Color Teal { get; init; }
    public Color Amber { get; init; }
    public Color Coral { get; init; }
    /// <summary>Statuspunkt „bereit“ bzw. „beendet“.</summary>
    public Color Ready { get; init; }
    public Color Exited { get; init; }
    /// <summary>Unteres Ende des Logo-Verlaufs und die helle Spitze (oben ist „Mint“).</summary>
    public Color LogoBottom { get; init; }
    public Color LogoApex { get; init; }

    /// <summary>(Hauptfarbe, Zweitfarbe, Lichter) je Stimmung – treibt Hintergrund und Terminalrand.</summary>
    public Func<Mood, (Color primary, Color secondary, Color highlight)> Moods { get; init; } = _ => default;

    // Terminal
    public Color TermForeground { get; init; }
    public Color TermBackground { get; init; }
    public Color TermCursor { get; init; }
    public Color TermSelection { get; init; }
    public Color TermLink { get; init; }
    public Color TermBlack { get; init; }

    /// <summary>Einfarbige Pinsel nach Ressourcenschlüssel.</summary>
    public Dictionary<string, Color> Solids { get; init; } = new();
    /// <summary>Verläufe und das Titelleisten-Logo.</summary>
    public Func<Dictionary<string, object>> Extras { get; init; } = () => new();
    /// <summary>Hintergrund-Animation.</summary>
    public Func<Backdrop> CreateBackdrop { get; init; } = () => new NeuralField();

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>Akzent mit Deckkraft – die Abstufungen, die die Oberfläche benutzt (Schlüssel „Accent14Brush“ = Deckkraft 0x14).</summary>
    static readonly byte[] AccentAlphas = { 0x0D, 0x0E, 0x0F, 0x12, 0x14, 0x1C, 0x1F, 0x20, 0x24, 0x26, 0x2A, 0x2E, 0x3A, 0x40, 0x44, 0x55, 0x66, 0x88 };

    static Dictionary<string, Color> Build(Color accent, params (string key, string hex)[] solids)
    {
        var d = solids.ToDictionary(s => s.key, s => C(s.hex));
        foreach (var a in AccentAlphas) d[$"Accent{a:X2}Brush"] = Color.FromArgb(a, accent.R, accent.G, accent.B);
        return d;
    }

    static RadialGradientBrush Radial(Color c, byte a) => new(Color.FromArgb(a, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));

    static LinearGradientBrush Linear(Point start, Point end, params (Color c, double at)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (c, at) in stops) b.GradientStops.Add(new GradientStop(c, at));
        return b;
    }

    /// <summary>Titelleisten-Logo: ein „A“ aus Knoten, Farben je Thema.</summary>
    static DrawingImage Logo(Color halo, Color top, Color bottom, Color nodes, Color apex)
    {
        var lines = new GeometryGroup
        {
            Children =
            {
                new LineGeometry(new Point(50, 14), new Point(18, 86)),
                new LineGeometry(new Point(50, 14), new Point(82, 86)),
                new LineGeometry(new Point(32, 58), new Point(68, 58)),
            },
        };
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
        g.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(halo), 16) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, lines));
        g.Children.Add(new GeometryDrawing(null, new Pen(new LinearGradientBrush(top, bottom, 90), 7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, lines));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(nodes), null, new GeometryGroup
        {
            Children =
            {
                new EllipseGeometry(new Point(18, 86), 8, 8), new EllipseGeometry(new Point(82, 86), 8, 8),
                new EllipseGeometry(new Point(32, 58), 7, 7), new EllipseGeometry(new Point(68, 58), 7, 7),
            },
        }));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(apex), null, new EllipseGeometry(new Point(50, 14), 9, 9)));
        var img = new DrawingImage(g);
        img.Freeze();
        return img;
    }

    static Color Mix(Color a, Color b, double t) => MoodPalette.Mix(a, b, t);

    // ───────────────────────────── Neural (Standard) ─────────────────────────────

    public static readonly AxonTheme Neural = CreateNeural();

    static AxonTheme CreateNeural()
    {
        Color mint = C("#5CFFB0"), emerald = C("#3DDC97"), teal = C("#2BB3A3"), amber = C("#FFB547"), coral = C("#FF6B7A");
        return new AxonTheme
        {
            Id = "neural",
            Name = "Neural",
            Subtitle = "Claude Code, neu verdrahtet.",
            Mint = mint, Emerald = emerald, Teal = teal, Amber = amber, Coral = coral,
            Ready = C("#2E9E72"), Exited = C("#4C665A"), LogoBottom = C("#1FBF7A"), LogoApex = C("#D6FFE9"),
            Moods = m => m switch
            {
                Mood.Working => (C("#4FF0A8"), C("#39D6C8"), C("#E0FFF0")),
                Mood.Attention => (Mix(emerald, amber, 0.35), amber, C("#FFE2A8")),
                Mood.Done => (C("#45E6A0"), teal, C("#D8FFEC")),
                Mood.Error => (Mix(emerald, coral, 0.3), coral, C("#FFC8CE")),
                _ => (emerald, teal, C("#B8FFD9")),
            },
            TermForeground = C("#D6ECE1"), TermBackground = C("#08100D"), TermCursor = mint,
            TermSelection = C("#553DDC97"), TermLink = C("#7DF5C4"), TermBlack = C("#101C17"),
            Solids = Build(emerald,
                ("Bg0Brush", "#050B08"), ("PanelBrush", "#E00A1511"), ("CardBrush", "#F20B1813"), ("LineBrush", "#1C3A2D"),
                ("TextBrush", "#D6ECE1"), ("TextDimBrush", "#7E9C8E"), ("TextFaintBrush", "#4C665A"),
                ("MintBrush", "#5CFFB0"), ("EmeraldBrush", "#3DDC97"), ("AmberBrush", "#FFB547"), ("CoralBrush", "#FF6B7A"),
                ("HoverBrush", "#143DDC97"), ("PressBrush", "#243DDC97"),
                ("EdgeBrush", "#2C4A3C"), ("TrackBrush", "#1A2E25"), ("SeparatorBrush", "#1E3A2E"),
                ("TooltipBrush", "#F20D1A15"), ("MenuBrush", "#F70C1713"), ("PopupBrush", "#FA0B1612"), ("OnPrimaryBrush", "#04140D"),
                ("NodeDimBrush", "#2F6E55"), ("RecentCardBrush", "#B00A1511"), ("RecentCardHoverBrush", "#D00C1A14"),
                ("OverlayBrush", "#B8030806"), ("CoralEdgeBrush", "#55FF6B7A")),
            Extras = () => new()
            {
                ["PanelEdgeBrush"] = Linear(new Point(0, 0), new Point(1, 1), (C("#553DDC97"), 0), (C("#1A2BB3A3"), 0.45), (C("#332BB3A3"), 1)),
                ["PrimaryButtonBrush"] = Linear(new Point(0, 0), new Point(1, 1), (mint, 0), (C("#2BC78A"), 1)),
                ["NodeGlowBrush"] = Radial(mint, 0x99),
                ["WelcomeGlowBrush"] = Radial(emerald, 0x40),
                ["PanelSheenBrush"] = Linear(new Point(0, 0), new Point(0.6, 1), (C("#0E5CFFB0"), 0), (C("#005CFFB0"), 0.4)),
                ["LogoImage"] = Logo(C("#335CFFB0"), mint, C("#1FBF7A"), mint, C("#D6FFE9")),
            },
            CreateBackdrop = () => new NeuralField(),
        };
    }

    // ───────────────────────────── Halloween ─────────────────────────────

    public static readonly AxonTheme Halloween = CreateHalloween();

    static AxonTheme CreateHalloween()
    {
        // Kürbis-Orange als Akzent, Violett als Zweitfarbe, Giftgrün für „wartet auf dich“, Blutrot für Fehler
        Color flame = C("#FFA040"), pumpkin = C("#FF7A1F"), violet = C("#9B6BFF"), slime = C("#B6FF4D"), blood = C("#FF4057");
        return new AxonTheme
        {
            Id = "halloween",
            Name = "Halloween",
            Subtitle = "Claude Code, heute Nacht verhext.",
            Mint = flame, Emerald = pumpkin, Teal = violet, Amber = slime, Coral = blood,
            Ready = C("#C2601E"), Exited = C("#6B5566"), LogoBottom = C("#E8500C"), LogoApex = C("#FFF0C8"),
            Moods = m => m switch
            {
                Mood.Working => (C("#FF9030"), C("#B07CFF"), C("#FFF0D0")),
                Mood.Attention => (Mix(pumpkin, slime, 0.4), slime, C("#E8FFB8")),
                Mood.Done => (C("#FF8A3A"), C("#C08CFF"), C("#FFE6F8")),
                Mood.Error => (Mix(pumpkin, blood, 0.6), blood, C("#FFC4C8")),
                _ => (pumpkin, violet, C("#FFD9A0")),
            },
            TermForeground = C("#F2E6DA"), TermBackground = C("#100A12"), TermCursor = flame,
            TermSelection = C("#55FF7A1F"), TermLink = C("#FFB870"), TermBlack = C("#1A1220"),
            Solids = Build(pumpkin,
                ("Bg0Brush", "#08050C"), ("PanelBrush", "#E00E0912"), ("CardBrush", "#F2130C18"), ("LineBrush", "#3A2340"),
                ("TextBrush", "#F2E6DA"), ("TextDimBrush", "#A8929E"), ("TextFaintBrush", "#6B5566"),
                ("MintBrush", "#FFA040"), ("EmeraldBrush", "#FF7A1F"), ("AmberBrush", "#B6FF4D"), ("CoralBrush", "#FF4057"),
                ("HoverBrush", "#14FF7A1F"), ("PressBrush", "#24FF7A1F"),
                ("EdgeBrush", "#4A2D44"), ("TrackBrush", "#2A1A26"), ("SeparatorBrush", "#3A2236"),
                ("TooltipBrush", "#F2140C17"), ("MenuBrush", "#F7120A15"), ("PopupBrush", "#FA110A14"), ("OnPrimaryBrush", "#1A0A00"),
                ("NodeDimBrush", "#7A3E1E"), ("RecentCardBrush", "#B00E0912"), ("RecentCardHoverBrush", "#D0150D19"),
                ("OverlayBrush", "#B8060309"), ("CoralEdgeBrush", "#55FF4057")),
            Extras = () => new()
            {
                ["PanelEdgeBrush"] = Linear(new Point(0, 0), new Point(1, 1), (C("#55FF7A1F"), 0), (C("#1A9B6BFF"), 0.45), (C("#339B6BFF"), 1)),
                ["PrimaryButtonBrush"] = Linear(new Point(0, 0), new Point(1, 1), (C("#FFB050"), 0), (C("#FF6A10"), 1)),
                ["NodeGlowBrush"] = Radial(flame, 0x99),
                ["WelcomeGlowBrush"] = Radial(pumpkin, 0x48),
                ["PanelSheenBrush"] = Linear(new Point(0, 0), new Point(0.6, 1), (C("#10FFA040"), 0), (C("#00FFA040"), 0.4)),
                ["LogoImage"] = Logo(C("#40FF8A2A"), C("#FFC060"), C("#E8500C"), flame, C("#FFF0C8")),
            },
            CreateBackdrop = () => new HalloweenField(),
        };
    }

    public static readonly AxonTheme[] All = { Neural, Halloween };

    public static AxonTheme ById(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Neural;
}

public static class ThemeManager
{
    public static AxonTheme Current { get; private set; } = AxonTheme.Neural;

    /// <summary>Nach dem Austausch der Pinsel (Visuals mit eigenen Caches zeichnen dann neu).</summary>
    public static event Action? Changed;

    /// <summary>Setzt alle Pinsel des Themas in die App-Ressourcen. Ohne laufende App (Tests) nur die Farben.</summary>
    public static void Apply(AxonTheme theme)
    {
        Current = theme;
        var res = Application.Current?.Resources;
        if (res != null)
        {
            foreach (var (key, color) in theme.Solids)
            {
                var b = new SolidColorBrush(color);
                b.Freeze();
                res[key] = b;
            }
            foreach (var (key, value) in theme.Extras())
            {
                if (value is Freezable f && f.CanFreeze) f.Freeze();
                res[key] = value;
            }
        }
        Changed?.Invoke();
    }
}
