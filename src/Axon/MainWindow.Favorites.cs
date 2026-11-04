using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Axon.Services;

namespace Axon;

/// <summary>Favoriten: angeheftete Chats oben in der Seitenleiste (in beiden Ansichten), auf- und zuklappbar.
/// Gespeichert werden nur die Gesprächs-Ids (<see cref="AppSettings.PinnedChats"/>) und der Klappzustand.</summary>
public partial class MainWindow
{
    readonly ObservableCollection<ChatEntry> _favorites = new();

    void InitFavorites()
    {
        FavList.ItemsSource = _favorites;
        SetFavoritesOpen(_settings.FavoritesOpen, animate: false);
    }

    /// <summary>Nach dem Laden der Chats: Bereich nur zeigen, wenn es Favoriten gibt.</summary>
    void UpdateFavoritesHeader()
    {
        Favorites.Visibility = _favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FavCount.Text = _favorites.Count.ToString();
    }

    void TogglePin(ChatEntry c)
    {
        bool pin = _settings.PinnedChats.RemoveAll(id => id == c.SessionId) == 0;
        if (pin)
        {
            _settings.PinnedChats.Add(c.SessionId);
            // gleich sichtbar machen, wo der Chat gelandet ist
            if (!_settings.FavoritesOpen) SetFavoritesOpen(true, animate: true);
        }
        _settings.Save();
        AppPaths.Log((pin ? "Favorit angeheftet: " : "Favorit gelöst: ") + c.SessionId);

        // sofort umsortieren, das Neuladen im Hintergrund ordnet danach die Zeitgruppen neu
        var moved = new ChatEntry
        {
            SessionId = c.SessionId, Title = c.Title, ProjectPath = c.ProjectPath, ProjectName = c.ProjectName,
            LastUsed = c.LastUsed, IsOpen = c.IsOpen, IsPinned = pin,
        };
        if (pin)
        {
            _chats.Remove(c);
            _favorites.Add(moved);
        }
        else
        {
            _favorites.Remove(c);
        }
        UpdateFavoritesHeader();
        RefreshChats();
    }

    void ChatPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ChatEntry c) TogglePin(c);
        e.Handled = true;
    }

    void ChatMenuPin_Click(object sender, RoutedEventArgs e)
    {
        if (MenuChat(sender) is { } c) TogglePin(c);
    }

    void FavHeader_Click(object sender, MouseButtonEventArgs e) => SetFavoritesOpen(!_settings.FavoritesOpen, animate: true);

    /// <summary>Klappt die Favoriten auf/zu: Höhe gleitet weich, der Pfeil dreht sich mit (einmalige Animation, kein Dauertakt).</summary>
    void SetFavoritesOpen(bool open, bool animate)
    {
        if (_settings.FavoritesOpen != open)
        {
            _settings.FavoritesOpen = open;
            _settings.Save();
        }
        FavHeader.ToolTip = open ? "Favoriten zuklappen" : "Favoriten aufklappen";
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(280);

        FavChevronTurn.BeginAnimation(RotateTransform.AngleProperty, null);
        FavBody.BeginAnimation(HeightProperty, null);
        FavBody.BeginAnimation(OpacityProperty, null);
        if (!animate || !IsLoaded)
        {
            FavChevronTurn.Angle = open ? 90 : 0;
            FavBody.Height = open ? double.NaN : 0;
            FavBody.Opacity = open ? 1 : 0;
            return;
        }

        FavChevronTurn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 90 : 0, duration) { EasingFunction = ease });

        double from = FavBody.ActualHeight;
        double to = 0;
        if (open && FavBody.Child is UIElement content)
        {
            content.Measure(new Size(FavBody.ActualWidth > 0 ? FavBody.ActualWidth : 236, double.PositiveInfinity));
            to = content.DesiredSize.Height;
        }
        var grow = new DoubleAnimation(from, to, duration) { EasingFunction = ease };
        grow.Completed += (_, _) =>
        {
            if (_settings.FavoritesOpen != open) return;   // inzwischen wieder umgeschaltet
            FavBody.BeginAnimation(HeightProperty, null);
            // offen: Höhe wieder frei, damit neue Favoriten Platz bekommen
            FavBody.Height = open ? double.NaN : 0;
        };
        FavBody.BeginAnimation(HeightProperty, grow);
        FavBody.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 320 : 200)) { EasingFunction = ease });
    }
}
