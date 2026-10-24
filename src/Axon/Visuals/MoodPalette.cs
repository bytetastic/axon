using System;
using System.Windows.Media;

namespace Axon.Visuals;

public enum Mood { Calm, Working, Attention, Done, Error }

public enum AnimationLevel { Full, Subtle, Off }

/// <summary>Farbstimmungen. Grundton bleibt immer grün; Akzente wechseln mit dem Zustand.</summary>
public static class MoodPalette
{
    public static readonly Color Mint = Color.FromRgb(0x5C, 0xFF, 0xB0);
    public static readonly Color Emerald = Color.FromRgb(0x3D, 0xDC, 0x97);
    public static readonly Color Teal = Color.FromRgb(0x2B, 0xB3, 0xA3);
    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB5, 0x47);
    public static readonly Color Coral = Color.FromRgb(0xFF, 0x6B, 0x7A);

    public static (Color primary, Color secondary, Color highlight) For(Mood m) => m switch
    {
        Mood.Working => (Color.FromRgb(0x4F, 0xF0, 0xA8), Color.FromRgb(0x39, 0xD6, 0xC8), Color.FromRgb(0xE0, 0xFF, 0xF0)),
        Mood.Attention => (Mix(Emerald, Amber, 0.35), Amber, Color.FromRgb(0xFF, 0xE2, 0xA8)),
        Mood.Done => (Color.FromRgb(0x45, 0xE6, 0xA0), Teal, Color.FromRgb(0xD8, 0xFF, 0xEC)),
        Mood.Error => (Mix(Emerald, Coral, 0.3), Coral, Color.FromRgb(0xFF, 0xC8, 0xCE)),
        _ => (Emerald, Teal, Color.FromRgb(0xB8, 0xFF, 0xD9)),
    };

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color WithAlpha(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Farbe weich an Ziel annähern (für fließende Stimmungswechsel).</summary>
    public static Color Approach(Color current, Color target, double k)
    {
        double R(byte a, byte b) => a + (b - a) * k;
        var c = Color.FromRgb((byte)Math.Round(R(current.R, target.R)), (byte)Math.Round(R(current.G, target.G)), (byte)Math.Round(R(current.B, target.B)));
        // nie hängen bleiben, wenn die Rundung die letzte Stufe verschluckt
        if (c == current && current != target)
            c = Color.FromRgb(Step(current.R, target.R), Step(current.G, target.G), Step(current.B, target.B));
        return c;
    }

    static byte Step(byte a, byte b) => a == b ? a : (byte)(a + Math.Sign(b - a));
}
