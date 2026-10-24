using System;
using System.Windows;

namespace Axon.Visuals;

/// <summary>
/// Animierter Fensterhintergrund eines Themas. Das Fenster spricht nur diese Schnittstelle an,
/// damit sich der Hintergrund beim Themenwechsel austauschen lässt.
/// </summary>
public abstract class Backdrop : FrameworkElement
{
    public abstract Mood Mood { get; set; }
    public abstract AnimationLevel Level { get; set; }
    public abstract bool IntroRunning { get; }

    /// <summary>Höhepunkt der Startanimation – Moment für weitere Effekte (Hintergrund hellt auf, Logo leuchtet).</summary>
    public event Action? Ignited;
    protected void RaiseIgnited() => Ignited?.Invoke();

    /// <summary>Startanimation abspielen. <paramref name="speed"/> &gt; 1 = kürzer (z. B. bei „dezent“).</summary>
    public abstract void PlayIntro(double speed = 1);
    /// <summary>Mittelpunkt des Terminals – Ursprung der „Fertig“-Effekte.</summary>
    public abstract void SetFocusPoint(Point p);
    public abstract void SetPointer(Point p);
    /// <summary>Benutzer hat etwas abgeschickt.</summary>
    public abstract void FireInput();
    /// <summary>Neuer Tab: etwas wächst vom Rand herein.</summary>
    public abstract void GrowDendrite();
}
