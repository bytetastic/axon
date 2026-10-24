using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Axon.Services;
using Axon.Visuals;

namespace Axon;

/// <summary>Übersichtskarte „Nutzung“ – erscheint beim Darüberfahren über den Anzeiger unten rechts.</summary>
public sealed class UsageCard : Border
{
    readonly TextBlock _model = Text(11.5, dim: true);
    readonly UsageBar _ctxBar = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _ctxMain = Text(12.5);
    readonly TextBlock _ctxDetail = Text(11, dim: true);
    readonly UsageBar _fiveBar = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _fiveMain = Text(12.5);
    readonly TextBlock _fiveReset = Text(11, dim: true);
    readonly UsageBar _weekBar = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly TextBlock _weekMain = Text(12.5);
    readonly TextBlock _weekReset = Text(11, dim: true);
    readonly TextBlock _limitNote = Text(11, dim: true);
    readonly StackPanel _limits = new();
    readonly TextBlock _session = Text(12);
    readonly TextBlock _cost = Text(11, dim: true);
    readonly TextBlock _footer = Text(10.5, dim: true);
    readonly StackPanel _ctxPanel = new();
    readonly TextBlock _ctxEmpty = Text(11.5, dim: true);

    static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x7E, 0x9C, 0x8E));
    static readonly Brush FaintBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x66, 0x5A));

    public UsageCard()
    {
        Width = 330;
        Padding = new Thickness(6, 4, 6, 4);
        var root = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock { Text = "Nutzung", FontFamily = new FontFamily("Bahnschrift"), FontSize = 16 });
        _model.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_model, 1);
        head.Children.Add(_model);
        root.Children.Add(head);

        root.Children.Add(Section("KONTEXT"));
        _ctxPanel.Children.Add(_ctxMain);
        _ctxPanel.Children.Add(_ctxBar);
        _ctxDetail.Margin = new Thickness(0, 6, 0, 0);
        _ctxDetail.TextWrapping = TextWrapping.Wrap;
        _ctxPanel.Children.Add(_ctxDetail);
        root.Children.Add(_ctxPanel);
        _ctxEmpty.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_ctxEmpty);

        root.Children.Add(Section("ABO-LIMITS"));
        _limits.Children.Add(_fiveMain);
        _limits.Children.Add(_fiveBar);
        _fiveReset.Margin = new Thickness(0, 4, 0, 10);
        _limits.Children.Add(_fiveReset);
        _limits.Children.Add(_weekMain);
        _limits.Children.Add(_weekBar);
        _weekReset.Margin = new Thickness(0, 4, 0, 0);
        _limits.Children.Add(_weekReset);
        root.Children.Add(_limits);
        _limitNote.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_limitNote);

        root.Children.Add(Section("SITZUNG"));
        _session.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_session);
        _cost.Margin = new Thickness(0, 3, 0, 0);
        _cost.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(_cost);

        _footer.Margin = new Thickness(0, 12, 0, 0);
        _footer.Foreground = FaintBrush;
        root.Children.Add(_footer);
        Child = root;
    }

    static TextBlock Text(double size, bool dim = false) => new()
    {
        FontSize = size,
        Foreground = dim ? DimBrush : new SolidColorBrush(Color.FromRgb(0xD6, 0xEC, 0xE1)),
    };

    static TextBlock Section(string title) => new()
    {
        Text = title,
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 10.5,
        Foreground = FaintBrush,
        Margin = new Thickness(0, 14, 0, 6),
    };

    /// <param name="u">Daten des aktuellen Tabs (oder null)</param>
    /// <param name="five">letzter bekannter Stand des 5-Stunden-Fensters (kontoweit)</param>
    /// <param name="week">letzter bekannter Stand des Wochenfensters (kontoweit)</param>
    /// <param name="statusLineBlocked">eigene Statusline des Benutzers aktiv – Axon bekommt dann keine Daten</param>
    public void Update(UsageInfo? u, RateWindow? five, RateWindow? week, bool statusLineBlocked)
    {
        _model.Text = string.IsNullOrEmpty(u?.Model) ? "" : u!.Model;

        // Kontext
        if (u is { HasContext: true } && u.ContextPercent is { } pct)
        {
            _ctxPanel.Visibility = Visibility.Visible;
            _ctxEmpty.Visibility = Visibility.Collapsed;
            _ctxMain.Text = $"{UsageInfo.Tokens(u.ContextUsed)} von {UsageInfo.Tokens(u.ContextSize)} Token  ·  {UsageInfo.Percent(pct)}";
            _ctxBar.Value = pct;
            _ctxDetail.Text = $"Neu {UsageInfo.Tokens(u.InputTokens)}  ·  Cache gelesen {UsageInfo.Tokens(u.CacheRead)}  ·  " +
                              $"Cache geschrieben {UsageInfo.Tokens(u.CacheWrite)}  ·  Antwort {UsageInfo.Tokens(u.OutputTokens)}";
        }
        else
        {
            _ctxPanel.Visibility = Visibility.Collapsed;
            _ctxEmpty.Visibility = Visibility.Visible;
            _ctxEmpty.Text = statusLineBlocked
                ? "Du hast eine eigene Statusline eingerichtet – Axon überschreibt sie nicht und bekommt deshalb keine Nutzungsdaten."
                : u == null ? "Erscheint, sobald Claude im aktuellen Tab geantwortet hat." : "Noch keine Antwort in dieser Sitzung (oder gerade komprimiert).";
        }

        // Abo-Limits (gelten fürs ganze Konto)
        bool any = five != null || week != null;
        _limits.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SetWindow(five, "5-Stunden-Fenster", _fiveMain, _fiveBar, _fiveReset);
        SetWindow(week, "Woche", _weekMain, _weekBar, _weekReset);
        if (!any)
        {
            _limitNote.Visibility = Visibility.Visible;
            _limitNote.Text = "Noch kein Stand – Claude meldet die Limits mit der ersten Antwort (nur bei Pro- und Max-Abo).";
        }
        else
        {
            var seen = five?.Seen ?? week!.Seen;
            bool old = (DateTime.Now - seen).TotalMinutes > 10;
            _limitNote.Visibility = old ? Visibility.Visible : Visibility.Collapsed;
            _limitNote.Text = old ? $"Stand von {seen:HH:mm} Uhr – aktualisiert sich mit der nächsten Antwort." : "";
            _limitNote.Margin = new Thickness(0, 6, 0, 0);
        }

        // Sitzung
        if (u != null)
        {
            string lines = u.LinesAdded + u.LinesRemoved > 0 ? $"  ·  Zeilen +{u.LinesAdded} / −{u.LinesRemoved}" : "";
            _session.Text = $"Dauer {UsageInfo.Duration(u.DurationMs)}  ·  davon Claude {UsageInfo.Duration(u.ApiDurationMs)}{lines}";
            _cost.Text = u.CostUsd is { } c
                ? $"Rechnerischer Wert: {UsageInfo.Money(c)} zum Listenpreis – beim Pro-/Max-Abo nicht berechnet."
                : "";
            _footer.Text = $"Aktualisiert {Ago(u.Received)}";
        }
        else
        {
            _session.Text = "Noch keine Daten für diesen Tab.";
            _cost.Text = "";
            _footer.Text = "";
        }
    }

    static void SetWindow(RateWindow? w, string label, TextBlock main, UsageBar bar, TextBlock reset)
    {
        bool visible = w != null;
        main.Visibility = bar.Visibility = reset.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (w == null) return;
        if (w.HasReset)
        {
            main.Text = $"{label}  ·  0 %";
            bar.Value = 0;
            reset.Text = $"seit {w.ResetsAt:HH:mm} Uhr zurückgesetzt";
            return;
        }
        main.Text = $"{label}  ·  {UsageInfo.Percent(w.UsedPercent)} verbraucht";
        bar.Value = w.UsedPercent;
        reset.Text = UsageInfo.ResetText(w.ResetsAt);
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 5) return "gerade eben";
        if (d.TotalSeconds < 60) return $"vor {(int)d.TotalSeconds} s";
        if (d.TotalMinutes < 60) return $"vor {(int)d.TotalMinutes} min";
        return $"um {t:HH:mm} Uhr";
    }
}
