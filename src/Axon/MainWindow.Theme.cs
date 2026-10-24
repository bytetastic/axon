using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Axon.Services;
using Axon.Terminal;
using Axon.Visuals;

namespace Axon;

/// <summary>Themen: Hintergrund austauschen, Pinsel wechseln, weich überblenden – ohne die laufenden Sitzungen anzufassen.</summary>
public partial class MainWindow
{
    /// <summary>Animierter Hintergrund des aktuellen Themas.</summary>
    Backdrop Field = null!;

    void InstallBackdrop()
    {
        Field = ThemeManager.Current.CreateBackdrop();
        Field.Ignited += OnIgnited;
        BackdropHost.Children.Add(Field);
        WelcomeSubtitle.Text = ThemeManager.Current.Subtitle;
    }

    void WireTheme()
    {
        ThemeNeural.Checked += (_, _) => SetTheme(AxonTheme.Neural.Id);
        ThemeHalloween.Checked += (_, _) => SetTheme(AxonTheme.Halloween.Id);
    }

    /// <summary>Nur für Tests: <c>AXON_TEST_THEME=&lt;id&gt;</c> schaltet 1,5 s nach dem Start um (prüft die Überblendung).</summary>
    void ScheduleTestThemeSwitch()
    {
        if (Environment.GetEnvironmentVariable("AXON_TEST_THEME") is not { Length: > 0 } id) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            SetTheme(id);
        };
        timer.Start();
    }

    void SyncThemeButtons()
    {
        ThemeNeural.IsChecked = ThemeManager.Current == AxonTheme.Neural;
        ThemeHalloween.IsChecked = ThemeManager.Current == AxonTheme.Halloween;
    }

    void SetTheme(string id)
    {
        var theme = AxonTheme.ById(id);
        if (theme == ThemeManager.Current) return;
        _settings.Theme = theme.Id;
        _settings.Save();

        // Standbild des alten Looks über alles legen, darunter umschalten, dann das Standbild ausblenden
        bool fade = _settings.Animation != "aus" && Root.ActualWidth > 0 && IsLoaded;
        if (fade)
        {
            var dpi = VisualTreeHelper.GetDpi(Root);
            var bmp = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY)),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            ThemeFade.Visibility = System.Windows.Visibility.Collapsed;
            bmp.Render(Root);
            bmp.Freeze();
            ThemeFade.Source = bmp;
            ThemeFade.BeginAnimation(OpacityProperty, null);
            ThemeFade.Opacity = 1;
            ThemeFade.Visibility = System.Windows.Visibility.Visible;
        }

        ThemeManager.Apply(theme);
        TerminalTheme.Refresh();

        var old = Field;
        old.Ignited -= OnIgnited;
        BackdropHost.Children.Remove(old);
        InstallBackdrop();
        ApplyAnimationLevel();
        UpdateMood();
        UpdateFocusPoint();
        foreach (var s in _sessions) s.View.SetFont(_settings.FontFamily, _settings.FontSize); // Zeilen mit neuen Farben neu rastern
        UpdateStatus();
        AppPaths.Log($"Thema: {theme.Name}");

        if (!fade) return;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1100))
        {
            BeginTime = TimeSpan.FromMilliseconds(80),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        anim.Completed += (_, _) =>
        {
            if (ThemeFade.Opacity > 0.001) return; // inzwischen erneut umgeschaltet
            ThemeFade.Visibility = System.Windows.Visibility.Collapsed;
            ThemeFade.Source = null;
        };
        ThemeFade.BeginAnimation(OpacityProperty, anim);
    }
}
