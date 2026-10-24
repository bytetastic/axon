using System;
using System.Windows.Media;

namespace Axon.Visuals;

public enum Mood { Calm, Working, Attention, Done, Error }

public enum AnimationLevel { Full, Subtle, Off }

/// <summary>Farbstimmungen des aktuellen Themas. Der Grundton bleibt, Akzente wechseln mit dem Zustand.</summary>
public static class MoodPalette
{
    public static Color Mint => ThemeManager.Current.Mint;
    public static Color Emerald => ThemeManager.Current.Emerald;
    public static Color Teal => ThemeManager.Current.Teal;
    public static Color Amber => ThemeManager.Current.Amber;
    public static Color Coral => ThemeManager.Current.Coral;

    public static (Color primary, Color secondary, Color highlight) For(Mood m) => ThemeManager.Current.Moods(m);

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
