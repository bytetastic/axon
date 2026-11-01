using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Axon;

/// <summary>Zwischen Claudes Antworten springen (Pfeile rechts im Terminal) und Tabs für die Sitzung umbenennen.</summary>
public partial class MainWindow
{
    const double AnswerNavIdle = 0.6;

    void WireAnswers()
    {
        AnswerUpButton.Click += (_, _) => JumpAnswer(-1);
        AnswerDownButton.Click += (_, _) => JumpAnswer(+1);
        AnswerNav.MouseEnter += (_, _) => FadeAnswerNav(1);
        AnswerNav.MouseLeave += (_, _) => FadeAnswerNav(AnswerNavIdle);
    }

    void JumpAnswer(int dir)
    {
        if (_current == null) return;
        _current.View.JumpToAnswer(dir);
        FocusTerminal();
    }

    /// <summary>Pfeile an den aktuellen Tab anpassen: nur sichtbar, wenn es Antworten gibt.</summary>
    void UpdateAnswerNav()
    {
        var (any, up, down) = _current?.View.AnswerNav ?? default;
        AnswerUpButton.IsEnabled = up;
        AnswerDownButton.IsEnabled = down;
        if (any && AnswerNav.Visibility != Visibility.Visible)
        {
            AnswerNav.Visibility = Visibility.Visible;
            FadeAnswerNav(AnswerNav.IsMouseOver ? 1 : AnswerNavIdle);
        }
        else if (!any && AnswerNav.Visibility == Visibility.Visible)
        {
            AnswerNav.BeginAnimation(OpacityProperty, null);
            AnswerNav.Opacity = 0;
            AnswerNav.Visibility = Visibility.Collapsed;
        }
    }

    void FadeAnswerNav(double to)
    {
        if (_settings.Animation == "aus") { AnswerNav.BeginAnimation(OpacityProperty, null); AnswerNav.Opacity = to; return; }
        AnswerNav.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ───────────────────────────── Tab umbenennen ─────────────────────────────

    void BeginRename(ClaudeSession s)
    {
        foreach (var x in _sessions) if (x != s) x.IsRenaming = false;
        s.IsRenaming = true;
    }

    void EndRename(ClaudeSession s, string? text, bool commit)
    {
        if (!s.IsRenaming) return;
        s.IsRenaming = false;
        if (commit) s.CustomTitle = text;
        if (s == _current) Title = "Axon – " + s.TabTitle;
        FocusTerminal();
    }

    void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not ClaudeSession s || !box.IsVisible) return;
        box.Text = s.TabTitle;
        // nach dem Fokuswechsel aus Select() (läuft ebenfalls mit Input-Priorität), sonst holt sich das Terminal den Fokus zurück
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }));
    }

    void RenameBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.Tag is not ClaudeSession s) return;
        if (e.Key == Key.Enter) { EndRename(s, box.Text, true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(s, null, false); e.Handled = true; }
    }

    void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Klick woanders hin übernimmt den Namen (wie im Explorer)
        if (sender is TextBox box && box.Tag is ClaudeSession s && s.IsRenaming && box.IsVisible) EndRename(s, box.Text, true);
    }

    static ClaudeSession? MenuSession(object sender) => (sender as FrameworkElement)?.DataContext as ClaudeSession;

    void TabMenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is { } s) { Select(s); BeginRename(s); }
    }

    void TabMenuResetName_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is not { } s) return;
        s.CustomTitle = null;
        if (s == _current) Title = "Axon – " + s.TabTitle;
    }

    void TabMenuClose_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is { } s) _ = CloseSession(s);
    }
}
