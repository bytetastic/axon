using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using Axon.Native;
using Axon.Services;
using Axon.Terminal;
using Axon.Visuals;

namespace Axon;

public partial class MainWindow : Window
{
    readonly AppSettings _settings;
    readonly ObservableCollection<ClaudeSession> _sessions = new();
    readonly ObservableCollection<ProjectEntry> _projects = new();
    readonly ObservableCollection<ProjectEntry> _recent = new();
    readonly IpcServer _ipc;
    readonly Notifier _notifier;
    readonly DispatcherTimer _clock;
    readonly string? _initialFolder;
    readonly string? _snapshotPath;
    readonly int _snapshotDelay;
    readonly Dictionary<string, DateTime> _lastNotified = new();
    ClaudeSession? _current;
    bool _closingConfirmed;
    Mood? _demoMood;

    public MainWindow(AppSettings settings, string? initialFolder, string? snapshotPath, int snapshotDelay)
    {
        _settings = settings;
        _initialFolder = initialFolder;
        _snapshotPath = snapshotPath;
        _snapshotDelay = snapshotDelay;
        InitializeComponent();

        TabList.ItemsSource = _sessions;
        ProjectList.ItemsSource = _projects;
        RecentCards.ItemsSource = _recent;
        StatusVersion.Text = "v" + AppPaths.Version;

        RestoreWindowBounds();
        WireButtons();
        ApplyAnimationLevel();
        ApplySidebar(false);
        Field.Ignited += OnIgnited;
        PrepareIntro();

        _ipc = new IpcServer(AppPaths.PipeName);
        _ipc.HookReceived += (tab, json) => Dispatcher.BeginInvoke(() => OnHook(tab, json));
        _ipc.StatusReceived += (tab, json) => Dispatcher.BeginInvoke(() => _sessions.FirstOrDefault(x => x.Id == tab)?.HandleStatus(json));
        _ipc.OpenRequested += folder => Dispatcher.BeginInvoke(() => OnOpenRequest(folder));
        _ipc.UpdateOffered += path => Dispatcher.BeginInvoke(async () => await OnUpdateOffered(path));
        _ipc.Start();

        _notifier = new Notifier();
        _notifier.Activated += tab => Dispatcher.BeginInvoke(() =>
        {
            BringToFront();
            var s = _sessions.FirstOrDefault(x => x.Id == tab);
            if (s != null) Select(s);
        });

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            UpdateStatus();
            if (UsageTip.IsOpen) UpdateUsage();
        };
        _clock.Start();

        // Fenster verhüllen, bis WPF das erste Bild gezeichnet hat – sonst blitzt kurz eine graugrüne Fläche auf
        SourceInitialized += (_, _) => SetCloak(true);
        ContentRendered += (_, _) => SetCloak(false);
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => OnWindowStateChanged();
        Activated += (_, _) => { UpdateTickerRate(); MarkCurrentSeen(); };
        Deactivated += (_, _) => UpdateTickerRate();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += (_, e) => Field.SetPointer(e.GetPosition(Root));
        Drop += OnWindowDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        PanelHost.SizeChanged += (_, _) => UpdateFocusPoint();
    }

    // ───────────────────────────── Start ─────────────────────────────

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowFrame();
        StartIntro();
        RefreshProjects();
        UpdateRestoreButton();

        if (DemoContent.Enabled) SetupDemo();
        else if (_initialFolder != null) OpenSession(_initialFolder);
        else ShowWelcome(true);

        // nur für Tests (Bilder, Messungen): Stimmung vorgeben
        if (Enum.TryParse<Mood>(Environment.GetEnvironmentVariable("AXON_DEMO_MOOD"), out var demo))
        {
            _demoMood = demo;
            UpdateMood();
        }

        if (_snapshotPath != null)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_snapshotDelay) };
            t.Tick += (_, _) => { t.Stop(); SaveSnapshot(_snapshotPath); };
            t.Start();
            // Testbilder von Dialog bzw. Einstellungen (Popup liegt in eigenem Fenster und wird separat gerendert)
            string extra = Environment.GetEnvironmentVariable("AXON_SNAPSHOT_EXTRA") ?? "";
            if (extra == "dialog")
                _ = ShowDialog("Axon installieren?",
                    "Axon läuft gerade direkt aus dieser Datei. Mit der Installation landet Axon im Startmenü und unter „Installierte Apps“ – nur für dich, ohne Adminrechte.",
                    new UIElement[]
                    {
                        new CheckBox { Style = (Style)FindResource("SwitchBox"), Content = "Verknüpfung auf dem Desktop", IsChecked = true },
                        new CheckBox { Style = (Style)FindResource("SwitchBox"), Content = "„In Axon öffnen“ im Explorer-Kontextmenü", IsChecked = false, Margin = new Thickness(0, 10, 0, 0) },
                    }, "Installieren", "Nur ausprobieren");
            if (extra == "usage")
            {
                var tu = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, _snapshotDelay - 1500)) };
                tu.Tick += (_, _) =>
                {
                    tu.Stop();
                    UpdateUsage();
                    UsageTip.PlacementTarget = UsageChip;
                    UsageTip.IsOpen = true;
                    // die ganze ToolTip mit ihrem dunklen Rahmen rendern, nicht nur den Inhalt
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        SaveElement(UsageTip, Path.ChangeExtension(_snapshotPath, null) + "-usage.png")));
                };
                tu.Start();
            }
            if (extra == "settings")
            {
                var ts = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, _snapshotDelay - 1500)) };
                ts.Tick += (_, _) =>
                {
                    ts.Stop();
                    OpenSettings();
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (SettingsPopup.Child is FrameworkElement child) SaveElement(child, Path.ChangeExtension(_snapshotPath, null) + "-settings.png");
                    }));
                };
                ts.Start();
            }
            // Spracheingabe durchspielen: mit AXON_TEST_VOICE=<wav> statt Mikrofon; endet die Datei, wird automatisch erkannt
            if (extra == "voice")
            {
                var tv = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                tv.Tick += (_, _) => { tv.Stop(); ToggleVoice(); };
                tv.Start();
            }
            if (Environment.GetEnvironmentVariable("AXON_TEST_INPUT") is { Length: > 0 } script)
            {
                var ti = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(500, _snapshotDelay - 3500)) };
                ti.Tick += (_, _) => { ti.Stop(); RunTestInput(script); };
                ti.Start();
            }
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(700);
            await MaybeOfferInstall();
        }));
        _ = CheckForUpdate();
    }

    // ───────────────────────────── Startanimation ─────────────────────────────

    bool _introOn;
    double _introScale = 1;

    UIElement[] IntroElements => new UIElement[]
    {
        TitleBar, Sidebar, StatusBar, PanelHost,
        WelcomeLogo, WelcomeTitle, WelcomeSubtitle, WelcomeButtons, RecentLabel, RecentCards, WelcomeHint,
    };

    /// <summary>Vor dem ersten Bild: alles unsichtbar, das Netz beginnt im Dunkeln.</summary>
    void PrepareIntro()
    {
        _introOn = _settings.StartAnimation && _settings.Animation != "aus" && Environment.GetEnvironmentVariable("AXON_NO_INTRO") != "1";
        if (!_introOn) return;
        _introScale = _settings.Animation == "dezent" ? 1 / 1.5 : 1;
        foreach (var el in IntroElements) el.Opacity = 0;
        WelcomeMark.Progress = 0;
        WelcomeGlow.Opacity = 0;
        Field.PlayIntro(1 / _introScale);
    }

    /// <summary>
    /// Ablauf (Sekunden ab dem ersten Bild): Logo erscheint 0,55 · zeichnet sich 0,65–1,95 · Zündung ≈ 1,95 ·
    /// Titelleiste, Seitenleiste, Statusleiste und Texte gleiten ab 1,9 nacheinander herein.
    /// </summary>
    void StartIntro()
    {
        if (!_introOn) return;
        double k = _introScale;
        IntroIn(WelcomeLogo, 0.55 * k, 0.45, 0, 6);
        WelcomeMark.BeginAnimation(LogoMark.ProgressProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.3 * k)) { BeginTime = TimeSpan.FromSeconds(0.65 * k) });
        IntroIn(PanelHost, 1.6 * k, 0.7, 0, 10);
        IntroIn(TitleBar, 1.9 * k, 0.6, 0, -8);
        IntroIn(Sidebar, 2.0 * k, 0.7, -16, 0);
        IntroIn(StatusBar, 2.05 * k, 0.6, 0, 8);
        IntroIn(WelcomeTitle, 2.0 * k, 0.6, 0, 12);
        IntroIn(WelcomeSubtitle, 2.12 * k, 0.6, 0, 12);
        IntroIn(WelcomeButtons, 2.24 * k, 0.6, 0, 12);
        IntroIn(RecentLabel, 2.36 * k, 0.6, 0, 12);
        IntroIn(RecentCards, 2.42 * k, 0.6, 0, 12);
        IntroIn(WelcomeHint, 2.55 * k, 0.6, 0, 8);
    }

    static void IntroIn(UIElement el, double at, double dur, double dx, double dy)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromSeconds(at);
        var duration = TimeSpan.FromSeconds(dur);
        var tt = new TranslateTransform(dx, dy);
        el.RenderTransform = tt;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { BeginTime = begin, EasingFunction = ease });
        if (dx != 0) tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromSeconds(dur * 1.3)) { BeginTime = begin, EasingFunction = ease });
        if (dy != 0) tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromSeconds(dur * 1.3)) { BeginTime = begin, EasingFunction = ease });
    }

    /// <summary>Das Netz zündet: Hintergrund hellt kurz auf, der Lichthof hinter dem Logo blüht auf.</summary>
    void OnIgnited()
    {
        Ambient.Flash(0.85);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        WelcomeGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
        var st = new ScaleTransform(0.55, 0.55);
        WelcomeGlow.RenderTransformOrigin = new Point(0.5, 0.5);
        WelcomeGlow.RenderTransform = st;
        var bloom = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
        bloom.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), ease));
        bloom.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)), new SineEase { EasingMode = EasingMode.EaseInOut }));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, bloom);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, bloom);
    }

    /// <summary>
    /// Demo für Screenshots (AXON_DEMO=1, Szene per AXON_DEMO_SCENE = session | welcome | attention):
    /// Beispielprojekte und nachgestellte Sitzungen – nichts Echtes vom Rechner.
    /// </summary>
    void SetupDemo()
    {
        var u = DemoContent.Usage();
        _settings.LastFiveHour = u.FiveHour;
        _settings.LastWeek = u.Week;
        string scene = Environment.GetEnvironmentVariable("AXON_DEMO_SCENE") ?? "session";
        if (scene == "welcome")
        {
            ShowWelcome(true);
            return;
        }
        var tabs = DemoContent.Tabs.Select(f => OpenSession(f)).ToList();
        Select(tabs[0]);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (scene == "attention") tabs[0].SetDemoState(SessionState.Attention, "Freigabe nötig · Befehl");
            else tabs[0].SetDemoState(SessionState.Working, "Befehl");
            tabs[1].SetDemoState(SessionState.Done);
            tabs[1].Unseen = true;
            tabs[2].SetDemoState(SessionState.Ready);
        };
        t.Start();
    }

    void SetCloak(bool cloaked)
    {
        int v = cloaked ? 1 : 0;
        Win32.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 13, ref v, sizeof(int)); // DWMWA_CLOAK
    }

    void ApplyWindowFrame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        Win32.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        int dark = 1;
        Win32.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int border = 0x002D3A1C; // BGR von #1C3A2D
        Win32.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
    }

    void RestoreWindowBounds()
    {
        if (_settings.WinWidth is > 400 && _settings.WinHeight is > 300 && _settings.WinLeft is { } l && _settings.WinTop is { } t)
        {
            var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (area.IntersectsWith(new Rect(l, t, _settings.WinWidth.Value, _settings.WinHeight.Value)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l;
                Top = t;
                Width = _settings.WinWidth.Value;
                Height = _settings.WinHeight.Value;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.WinMaximized) WindowState = WindowState.Maximized;
    }

    void OnWindowStateChanged()
    {
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Wiederherstellen" : "Maximieren";
    }

    void WireButtons()
    {
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();
        SidebarButton.Click += (_, _) => ToggleSidebar();
        NewTabButton.Click += (_, _) => PickFolderAndOpen();
        OpenFolderButton.Click += (_, _) => PickFolderAndOpen();
        WelcomeOpenButton.Click += (_, _) => PickFolderAndOpen();
        WelcomeHomeButton.Click += (_, _) => OpenSession(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        RestoreButton.Click += (_, _) => RestoreLastTabs();
        ScreenshotButton.Click += (_, _) => InsertScreenshot();
        SettingsButton.Click += (_, _) => OpenSettings();
        AnimButton.Click += (_, _) => CycleAnimation();

        ExitContinueButton.Click += (_, _) => _current?.Restart(true);
        ExitNewButton.Click += (_, _) => _current?.Restart(false);
        ExitCloseButton.Click += (_, _) => { if (_current != null) _ = CloseSession(_current); };

        FontSmaller.Click += (_, _) => ChangeFont(-1);
        FontLarger.Click += (_, _) => ChangeFont(+1);
        AnimFull.Checked += (_, _) => SetAnimation("voll");
        AnimSubtle.Checked += (_, _) => SetAnimation("dezent");
        AnimOff.Checked += (_, _) => SetAnimation("aus");
        UpdateBox.Checked += (_, _) => { _settings.UpdateCheck = true; _settings.Save(); };
        UpdateBox.Unchecked += (_, _) => { _settings.UpdateCheck = false; _settings.Save(); };
        UpdateButton.Click += async (_, _) => await OfferUpdate();
        IntroBox.Checked += (_, _) => { _settings.StartAnimation = true; _settings.Save(); };
        IntroBox.Unchecked += (_, _) => { _settings.StartAnimation = false; _settings.Save(); };
        NotifyBox.Checked += (_, _) => { _settings.Notifications = true; _settings.Save(); };
        NotifyBox.Unchecked += (_, _) => { _settings.Notifications = false; _settings.Save(); };
        ShotFolderButton.Click += (_, _) => PickScreenshotFolder();
        InstallButton.Click += (_, _) => InstallFromSettings();
        UninstallButton.Click += async (_, _) => await UninstallFromSettings();
        DesktopBox.Click += (_, _) => ApplyInstallOptions();
        ContextBox.Click += (_, _) => ApplyInstallOptions();
        WireVoice();
    }

    // ───────────────────────────── Tabs ─────────────────────────────

    public ClaudeSession OpenSession(string folder, params string[] args)
    {
        var s = new ClaudeSession(folder, args, _settings, Dispatcher);
        s.StateChanged += OnSessionStateChanged;
        s.Message += OnSessionMessage;
        s.InputSubmitted += x =>
        {
            if (x != _current) return;
            Field.FireInput();
            Ambient.Flash(0.45);
        };
        s.PropertyChanged += (_, e) => { if (s == _current && e.PropertyName is nameof(ClaudeSession.StateText) or nameof(ClaudeSession.ClaudeTitle)) UpdateStatus(); };
        s.UsageChanged += OnUsageChanged;
        s.View.Notice += t => ShowToast(t);
        s.View.ScreenshotRequested += InsertScreenshot;
        s.View.GridSizeChanged += (c, r) => { if (s == _current) StatusGrid.Text = $"{c} × {r}"; };
        s.View.Visibility = Visibility.Collapsed;
        TerminalHost.Children.Add(s.View);
        _sessions.Add(s);

        _settings.TouchRecent(folder);
        _settings.Save();
        RefreshProjects();

        Select(s);
        Field.GrowDendrite();
        return s;
    }

    void Select(ClaudeSession s)
    {
        if (_current == s && PanelHost.Visibility == Visibility.Visible) { FocusTerminal(); return; }
        _current = s;
        foreach (var x in _sessions)
        {
            x.IsSelected = x == s;
            x.View.Visibility = x == s ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowWelcome(false);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        s.View.BeginAnimation(OpacityProperty, fade);
        Title = "Axon – " + s.FolderName;
        StatusGrid.Text = s.View.Cols > 0 ? $"{s.View.Cols} × {s.View.RowsCount}" : "";
        UpdateStatus();
        UpdateMood();
        UpdateExitCard();
        UpdateUsage();
        FocusTerminal();
    }

    void FocusTerminal()
    {
        var v = _current?.View;
        if (v == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { v.Focus(); Keyboard.Focus(v); }));
    }

    async Task CloseSession(ClaudeSession s)
    {
        if (s.State is SessionState.Working or SessionState.Attention)
        {
            int r = await ShowDialog("Tab schließen?",
                $"Claude arbeitet in „{s.FolderName}“ noch. Wenn du den Tab schließt, wird die Sitzung beendet.\nDas Gespräch bleibt gespeichert und lässt sich später fortsetzen.",
                null, "Schließen", "Abbrechen");
            if (r != 0) return;
        }
        int idx = _sessions.IndexOf(s);
        s.Close();
        TerminalHost.Children.Remove(s.View);
        _sessions.Remove(s);
        _lastNotified.Remove(s.Id);
        if (_current == s)
        {
            _current = null;
            if (_sessions.Count > 0) Select(_sessions[Math.Clamp(idx, 0, _sessions.Count - 1)]);
            else ShowWelcome(true);
        }
        UpdateTaskbar();
    }

    void ShowWelcome(bool show)
    {
        if (show)
        {
            _current = null;
            PanelHost.Visibility = Visibility.Collapsed;
            Welcome.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Welcome.BeginAnimation(OpacityProperty, fade);
            Title = "Axon";
            StatusText.Text = "Bereit";
            StatusDot.State = SessionState.Ready;
            StatusFolder.Text = "";
            StatusGrid.Text = "";
            Field.Mood = Mood.Calm;
            Aura.Mood = Mood.Calm;
            UpdateRestoreButton();
            UpdateUsage();
        }
        else
        {
            Welcome.Visibility = Visibility.Collapsed;
            PanelHost.Visibility = Visibility.Visible;
        }
    }

    void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClaudeSession s) Select(s);
    }

    void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.Tag is ClaudeSession s)
        {
            _ = CloseSession(s);
            e.Handled = true;
        }
    }

    void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ClaudeSession s) _ = CloseSession(s);
        e.Handled = true;
    }

    void CycleTabs(int dir)
    {
        if (_sessions.Count == 0) return;
        int i = _current == null ? 0 : _sessions.IndexOf(_current);
        i = (i + dir + _sessions.Count) % _sessions.Count;
        Select(_sessions[i]);
    }

    void PickFolderAndOpen()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Projektordner für Claude Code wählen",
            InitialDirectory = _current?.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog(this) == true) OpenSession(dlg.FolderName);
    }

    void OnOpenRequest(string folder)
    {
        BringToFront();
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)) OpenSession(folder);
    }

    /// <summary>Eine neuere Axon.exe wurde gestartet: fragen, dann übergeben – die neue Datei installiert sich und startet Axon neu.</summary>
    async Task OnUpdateOffered(string newExe)
    {
        BringToFront();
        if (!File.Exists(newExe)) return;
        string newVersion;
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(newExe);
            newVersion = $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
        }
        catch { newVersion = "neu"; }
        // AXON_TEST_ACCEPT_UPDATE=1: nur für den automatischen Test des Update-Ablaufs
        int r = Environment.GetEnvironmentVariable("AXON_TEST_ACCEPT_UPDATE") == "1" ? 0 : await ShowDialog("Update verfügbar",
            $"Axon {newVersion} liegt bereit (du nutzt {AppPaths.Version}). Beim Aktualisieren startet Axon kurz neu.{RunningSessionsNote()}",
            null, "Jetzt aktualisieren", "Später");
        if (r != 0) return;
        await HandOverToUpdate(newExe);
    }

    string RunningSessionsNote()
    {
        int running = _sessions.Count(s => s.IsRunning);
        return running == 0 ? "" :
            running == 1 ? "\n\nDer offene Claude-Tab wird dabei beendet – das Gespräch bleibt gespeichert und lässt sich danach fortsetzen."
                         : $"\n\nDie {running} offenen Claude-Tabs werden dabei beendet – die Gespräche bleiben gespeichert und lassen sich danach fortsetzen.";
    }

    /// <summary>Startet die neue Datei mit --apply-update und beendet dieses Axon; die neue installiert sich und startet neu.</summary>
    async Task<bool> HandOverToUpdate(string newExe)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(newExe, $"--apply-update {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            await ShowDialog("Update fehlgeschlagen", ex.Message, null, "OK");
            return false;
        }
        SaveState();
        _closingConfirmed = true;
        foreach (var s in _sessions) s.Close();
        Close();
        return true;
    }

    // ───────────────────────────── Update-Prüfung (GitHub) ─────────────────────────────

    UpdateInfo? _update;
    bool _updating;

    /// <summary>Einmal beim Öffnen: bei GitHub nach einer neueren Version fragen. Ohne Netz oder bei Fehlern passiert nichts.</summary>
    async Task CheckForUpdate()
    {
        bool test = Environment.GetEnvironmentVariable("AXON_UPDATE_TEST") == "1";
        if (!_settings.UpdateCheck || (AppPaths.InstanceKey != "main" && !test) || _snapshotPath != null) return;
        await Task.Delay(TimeSpan.FromSeconds(test ? 1 : 3)); // Start nicht ausbremsen
        var info = await UpdateChecker.CheckAsync();
        if (info == null) return;
        _update = info;
        AppPaths.Log($"Update verfügbar: {info.Version}");
        UpdateButton.Content = $"Update {info.Version}";
        UpdateButton.Visibility = Visibility.Visible;
        UpdateButton.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
        ShowToast($"Axon {info.Version} ist verfügbar");
        if (Environment.GetEnvironmentVariable("AXON_TEST_ACCEPT_UPDATE") == "1") await OfferUpdate();
    }

    async Task OfferUpdate()
    {
        var info = _update;
        if (info == null || _updating) return;
        bool autoAccept = Environment.GetEnvironmentVariable("AXON_TEST_ACCEPT_UPDATE") == "1";

        if (!Installer.IsRunningInstalled)
        {
            // Nicht installierte Kopie: nicht selbst ersetzen, sondern zur Download-Seite schicken
            int choice = await ShowDialog($"Axon {info.Version} ist verfügbar",
                "Diese Kopie von Axon ist nicht installiert. Lade die neue Version von GitHub herunter und starte sie – sie bietet dann die Installation an.",
                null, "Download-Seite öffnen", "Später");
            if (choice == 0) OpenUrl(info.PageUrl);
            return;
        }

        int r = autoAccept ? 0 : await ShowDialog($"Axon {info.Version} ist verfügbar",
            $"Du nutzt Version {AppPaths.Version}.{NotesExcerpt(info.Notes)}\n\nBeim Aktualisieren lädt Axon die neue Version (≈ {info.Size / 1_048_576.0:0} MB) von GitHub und startet kurz neu.{RunningSessionsNote()}",
            null, "Jetzt aktualisieren", "Später");
        if (r != 0) return;

        _updating = true;
        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "Lädt … 0 %";
        try
        {
            var progress = new Progress<double>(p => UpdateButton.Content = $"Lädt … {p * 100:0} %");
            string exe = await UpdateChecker.DownloadAsync(info, progress);
            UpdateButton.Content = "Startet neu …";
            if (!await HandOverToUpdate(exe)) ResetUpdateButton();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update-Download: " + ex);
            ResetUpdateButton();
            await ShowDialog("Update fehlgeschlagen", ex.Message + "\n\nDu kannst die neue Version auch von der GitHub-Seite herunterladen.", null, "OK");
        }
    }

    void ResetUpdateButton()
    {
        _updating = false;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = _update != null ? $"Update {_update.Version}" : "Update";
    }

    /// <summary>Die ersten Zeilen der Release-Notizen, ohne Markdown-Zeichen.</summary>
    static string NotesExcerpt(string notes)
    {
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '-', '*', ' ').Replace("**", "").Replace("`", ""))
            .Where(l => l.Length > 0)
            .Take(6)
            .ToList();
        return lines.Count == 0 ? "" : "\n\n" + string.Join("\n", lines);
    }

    static void OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = _settings.WinMaximized ? WindowState.Maximized : WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] items)
        {
            foreach (var p in items.Where(Directory.Exists).Take(6)) OpenSession(p);
            var files = items.Where(File.Exists).ToArray();
            if (files.Length > 0 && _current != null) _current.View.PasteText(string.Join(" ", files.Select(TerminalView.QuotePath)));
        }
    }

    void RestoreLastTabs()
    {
        var folders = _settings.LastTabs.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in folders) OpenSession(f, "--continue");
    }

    void UpdateRestoreButton()
    {
        int n = _settings.LastTabs.Count(Directory.Exists);
        RestoreButton.Visibility = n > 0 && _sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RestoreButton.Content = n == 1 ? "Letzten Tab fortsetzen" : $"{n} Tabs wiederherstellen";
    }

    // ───────────────────────────── Projekte ─────────────────────────────

    void RefreshProjects()
    {
        var recent = _settings.Recent.ToList();
        var hidden = new Dictionary<string, DateTime>(_settings.HiddenProjects);
        Task.Run(() => ProjectHistory.Load(recent, hidden)).ContinueWith(t =>
        {
            if (t.Exception != null) return;
            _projects.Clear();
            foreach (var p in t.Result) _projects.Add(p);
            _recent.Clear();
            foreach (var p in t.Result.Take(6)) _recent.Add(p);
            RecentLabel.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Project_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectEntry p) OpenSession(p.Path);
    }

    void ProjectContinue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectEntry p) OpenSession(p.Path, "--continue");
        e.Handled = true;
    }

    void ProjectRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ProjectEntry p) RemoveProject(p);
        e.Handled = true;
    }

    static ProjectEntry? MenuProject(object sender) => (sender as FrameworkElement)?.DataContext as ProjectEntry;

    void ProjectMenuOpen_Click(object sender, RoutedEventArgs e) { if (MenuProject(sender) is { } p) OpenSession(p.Path); }
    void ProjectMenuContinue_Click(object sender, RoutedEventArgs e) { if (MenuProject(sender) is { } p) OpenSession(p.Path, "--continue"); }
    void ProjectMenuRemove_Click(object sender, RoutedEventArgs e) { if (MenuProject(sender) is { } p) RemoveProject(p); }

    void ProjectMenuExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (MenuProject(sender) is not { } p || !Directory.Exists(p.Path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{p.Path}\"") { UseShellExecute = true }); } catch { }
    }

    /// <summary>Blendet ein Projekt aus der Liste aus. Ordner und Claudes Verlauf bleiben unberührt;
    /// sobald der Ordner in Axon wieder geöffnet wird, erscheint er erneut.</summary>
    void RemoveProject(ProjectEntry p)
    {
        _settings.HideProject(p.Path);
        _settings.Save();
        _projects.Remove(p);
        foreach (var r in _recent.Where(r => string.Equals(r.Path, p.Path, StringComparison.OrdinalIgnoreCase)).ToList()) _recent.Remove(r);
        RecentLabel.Visibility = _recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRestoreButton();
        ShowToast($"„{p.Name}“ aus der Liste entfernt");
        RefreshProjects();
    }

    void ToggleSidebar()
    {
        _settings.SidebarOpen = !_settings.SidebarOpen;
        _settings.Save();
        ApplySidebar(true);
    }

    void ApplySidebar(bool animate)
    {
        double target = _settings.SidebarOpen ? 250 : 0;
        if (!animate)
        {
            Sidebar.Width = target;
            ContentArea.Margin = new Thickness(_settings.SidebarOpen ? 2 : 14, 2, 14, 2);
            return;
        }
        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        Sidebar.BeginAnimation(WidthProperty, anim);
        var m = new ThicknessAnimation(new Thickness(_settings.SidebarOpen ? 2 : 14, 2, 14, 2), TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        ContentArea.BeginAnimation(MarginProperty, m);
    }

    // ───────────────────────────── Zustände ─────────────────────────────

    void OnHook(string tabId, string json)
    {
        var s = _sessions.FirstOrDefault(x => x.Id == tabId);
        s?.HandleHook(json);
    }

    void OnSessionStateChanged(ClaudeSession s, SessionState old, SessionState now)
    {
        if (s == _current)
        {
            UpdateStatus();
            UpdateMood();
            UpdateExitCard();
            if (now == SessionState.Done && old == SessionState.Working) Ambient.Flash(0.8);
        }
        UpdateTaskbar();

        bool away = !IsActive || s != _current;
        if (now == SessionState.Done && old == SessionState.Working)
        {
            if (away) s.Unseen = true;
            Notify(s, "Claude ist fertig", s.LastTurn is { } t ? $"{s.FolderName} · {FormatSeconds(t)}" : s.FolderName, away);
        }
        else if (now == SessionState.Attention)
        {
            if (away) s.Unseen = true;
            Notify(s, "Claude braucht dich", $"{s.FolderName} · {s.Detail}", away);
        }
        else if (now == SessionState.Error)
        {
            Notify(s, "Claude wurde unterbrochen", $"{s.FolderName} · {s.Detail}", away);
        }
    }

    void OnSessionMessage(ClaudeSession s, string text)
    {
        bool away = !IsActive || s != _current;
        Notify(s, "Claude", $"{s.FolderName} · {text}", away);
    }

    void Notify(ClaudeSession s, string title, string text, bool away)
    {
        if (!away || DemoContent.Enabled) return;
        if (_lastNotified.TryGetValue(s.Id, out var last) && (DateTime.Now - last).TotalSeconds < 4) return;
        _lastNotified[s.Id] = DateTime.Now;
        if (!IsActive) Win32.Flash(new WindowInteropHelper(this).Handle);
        if (_settings.Notifications) _notifier.Show(title, text, s.Id);
    }

    static string FormatSeconds(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    void MarkCurrentSeen()
    {
        if (_current != null) _current.Unseen = false;
    }

    void UpdateStatus()
    {
        var s = _current;
        if (s == null) return;
        StatusDot.State = s.State;
        string text = s.StateText;
        if (s.State == SessionState.Working && s.TurnStart is { } ts)
        {
            var el = DateTime.Now - ts;
            if (el.TotalSeconds >= 2) text += " · " + FormatSeconds(el);
        }
        StatusText.Text = text;
        StatusText.Foreground = s.State switch
        {
            SessionState.Attention => (Brush)FindResource("AmberBrush"),
            SessionState.Error => (Brush)FindResource("CoralBrush"),
            SessionState.Working => (Brush)FindResource("MintBrush"),
            _ => (Brush)FindResource("TextDimBrush"),
        };
        StatusFolder.Text = s.Folder + (string.IsNullOrEmpty(s.ClaudeTitle) || s.ClaudeTitle == "Claude Code" ? "" : "  ·  " + s.ClaudeTitle);
    }

    // ───────────────────────────── Nutzung ─────────────────────────────

    readonly bool _statusLineBlocked = ClaudeLauncher.UserHasStatusLine();

    void OnUsageChanged(ClaudeSession s)
    {
        var u = s.Usage;
        if (u == null) return;
        // Abo-Limits gelten fürs ganze Konto: den neuesten Stand merken, egal aus welchem Tab
        bool changed = false;
        if (u.FiveHour != null && (_settings.LastFiveHour == null || _settings.LastFiveHour.UsedPercent != u.FiveHour.UsedPercent || _settings.LastFiveHour.ResetsAt != u.FiveHour.ResetsAt))
        {
            _settings.LastFiveHour = u.FiveHour;
            changed = true;
        }
        if (u.Week != null && (_settings.LastWeek == null || _settings.LastWeek.UsedPercent != u.Week.UsedPercent || _settings.LastWeek.ResetsAt != u.Week.ResetsAt))
        {
            _settings.LastWeek = u.Week;
            changed = true;
        }
        if (changed) _settings.Save();
        UpdateUsage();
    }

    void UpdateUsage()
    {
        var u = _current?.Usage;
        var five = _settings.LastFiveHour;
        var week = _settings.LastWeek;
        UsageChip.Visibility = _current != null || five != null || week != null ? Visibility.Visible : Visibility.Collapsed;

        CtxRing.Value = u?.ContextPercent ?? double.NaN;
        CtxText.Text = u?.ContextPercent is { } p ? "Kontext " + UsageInfo.Percent(p) : "Kontext –";
        CtxRing.Visibility = CtxText.Visibility = _current != null ? Visibility.Visible : Visibility.Collapsed;

        if (five == null)
        {
            LimitRing.Value = double.NaN;
            LimitText.Text = "5 h –";
        }
        else
        {
            double v = five.HasReset ? 0 : five.UsedPercent;
            LimitRing.Value = v;
            LimitText.Text = "5 h " + UsageInfo.Percent(v);
        }
        LimitRing.Margin = new Thickness(_current != null ? 12 : 0, 0, 0, 0);
        UsageCardView.Update(u, five, week, _statusLineBlocked);
    }

    void UpdateMood()
    {
        var m = _current?.State switch
        {
            SessionState.Working => Mood.Working,
            SessionState.Attention => Mood.Attention,
            SessionState.Done => Mood.Done,
            SessionState.Error => Mood.Error,
            _ => Mood.Calm,
        };
        if (_demoMood is { } demo) m = demo;
        Field.Mood = m;
        Aura.Mood = m;
        Ambient.Mood = m;
    }

    void UpdateTaskbar()
    {
        if (_sessions.Any(s => s.State == SessionState.Attention))
        {
            Taskbar.ProgressState = TaskbarItemProgressState.Paused;
            Taskbar.ProgressValue = 1;
        }
        else if (_sessions.Any(s => s.State == SessionState.Working))
            Taskbar.ProgressState = TaskbarItemProgressState.Indeterminate;
        else
            Taskbar.ProgressState = TaskbarItemProgressState.None;

        int working = _sessions.Count(s => s.State == SessionState.Working);
        _notifier.SetTooltip(working > 0 ? $"Axon – Claude arbeitet ({working})" : "Axon");
    }

    void UpdateExitCard()
    {
        var s = _current;
        if (s != null && s.State == SessionState.Exited)
        {
            ExitText.Text = $"Claude Code wurde beendet (Code {s.ExitCode}). Das Gespräch ist gespeichert.";
            ExitCard.Visibility = Visibility.Visible;
            ExitCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        }
        else ExitCard.Visibility = Visibility.Collapsed;
    }

    void UpdateFocusPoint()
    {
        if (!PanelHost.IsVisible) return;
        try
        {
            var p = PanelHost.TransformToAncestor(Root).Transform(new Point(PanelHost.ActualWidth / 2, PanelHost.ActualHeight / 2));
            Field.SetFocusPoint(p);
            Ambient.SetFocusPoint(p);
        }
        catch (InvalidOperationException) { }
    }

    // ───────────────────────────── Tastatur ─────────────────────────────

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DialogLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { _dialogResult?.TrySetResult(-1); e.Handled = true; }
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;
        if (!ctrl || alt) return;

        // Alle App-Kürzel mit Strg+Umschalt, damit Claude Codes eigene Strg-Kürzel (Strg+T, Strg+O, Strg+B …) frei bleiben.
        bool handled = true;
        if (shift)
        {
            switch (key)
            {
                case Key.T: PickFolderAndOpen(); break;
                case Key.D: if (_current != null) OpenSession(_current.Folder); break;
                case Key.W: if (_current != null) _ = CloseSession(_current); break;
                case Key.O: PickFolderAndOpen(); break;
                case Key.B: ToggleSidebar(); break;
                case Key.S: InsertScreenshot(); break;
                case Key.Space: ToggleVoice(); break;
                case Key.Tab: CycleTabs(-1); break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (key)
            {
                case Key.Tab: CycleTabs(+1); break;
                case Key.OemPlus: case Key.Add: ChangeFont(+1); break;
                case Key.OemMinus: case Key.Subtract: ChangeFont(-1); break;
                case Key.D0: case Key.NumPad0: ChangeFont(0); break;
                case Key.OemComma: OpenSettings(); break;
                default: handled = false; break;
            }
        }
        if (handled) e.Handled = true;
    }

    // ───────────────────────────── Screenshot ─────────────────────────────

    void InsertScreenshot()
    {
        if (_current == null) { ShowToast("Erst einen Tab öffnen"); return; }
        string folder = _settings.EffectiveScreenshotFolder;
        FileInfo? newest = null;
        try
        {
            if (Directory.Exists(folder))
                newest = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif")
                    .OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        }
        catch { }
        if (newest == null)
        {
            ShowToast($"Kein Screenshot in {folder}");
            return;
        }
        _current.View.PasteText(TerminalView.QuotePath(newest.FullName));
        ImageSource? thumb = null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 96;
            bmp.UriSource = new Uri(newest.FullName);
            bmp.EndInit();
            bmp.Freeze();
            thumb = bmp;
        }
        catch { }
        string age = (DateTime.Now - newest.LastWriteTime).TotalMinutes < 1 ? "gerade eben" : newest.LastWriteTime.ToString("HH:mm");
        ShowToast($"Screenshot eingefügt · {age}", thumb);
        FocusTerminal();
    }

    // ───────────────────────────── Toasts ─────────────────────────────

    void ShowToast(string text, ImageSource? image = null)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (image != null)
            panel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new Image { Source = image, Height = 44, Stretch = Stretch.Uniform },
            });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });
        var shift = new TranslateTransform(0, 12);
        var toast = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0xDC, 0x97)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(12, 9, 14, 9),
            Margin = new Thickness(0, 8, 0, 0),
            Child = panel,
            Opacity = 0,
            RenderTransform = shift,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        while (ToastHost.Children.Count >= 3) ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(toast);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(image != null ? 3.2 : 2.2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
            fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    // ───────────────────────────── Einstellungen ─────────────────────────────

    void OpenSettings()
    {
        FontSizeText.Text = $"{_settings.FontSize:0} pt";
        AnimFull.IsChecked = _settings.Animation == "voll";
        AnimSubtle.IsChecked = _settings.Animation == "dezent";
        AnimOff.IsChecked = _settings.Animation == "aus";
        NotifyBox.IsChecked = _settings.Notifications;
        IntroBox.IsChecked = _settings.StartAnimation;
        UpdateBox.IsChecked = _settings.UpdateCheck;
        ShotFolderText.Text = _settings.EffectiveScreenshotFolder;
        ShotFolderText.ToolTip = _settings.EffectiveScreenshotFolder;
        ClaudePathText.Text = ClaudeLauncher.Find(_settings) is { } p ? p : "nicht gefunden – bitte Claude Code installieren";
        if (DemoContent.Enabled)
        {
            // Screenshots: keine echten Pfade (Benutzername!) zeigen
            ShotFolderText.Text = @"%USERPROFILE%\Pictures\Screenshots";
            ClaudePathText.Text = @"%USERPROFILE%\.local\bin\claude.exe";
        }
        UpdateInstallSection();
        UpdateVoiceSettings();
        SettingsPopup.IsOpen = true;
    }

    void UpdateInstallSection()
    {
        bool installed = Installer.IsInstalled;
        bool runningInstalled = Installer.IsRunningInstalled;
        if (runningInstalled)
            InstallStatus.Text = $"Installiert in {AppPaths.InstallDir}";
        else if (installed)
            InstallStatus.Text = Installer.RunningIsNewer
                ? $"Installiert ist Version {Installer.InstalledVersion}, diese Datei ist neuer ({AppPaths.Version})."
                : $"Axon ist installiert (Version {Installer.InstalledVersion}). Diese Kopie läuft separat.";
        else
            InstallStatus.Text = "Axon läuft direkt aus dieser Datei. Installieren legt es ins Startmenü – ohne Adminrechte.";

        InstallButton.Content = installed && !runningInstalled && Installer.RunningIsNewer ? "Aktualisieren" : "Axon installieren";
        InstallButton.Visibility = !runningInstalled && (!installed || Installer.RunningIsNewer) ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        InstallOptions.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        DesktopBox.IsChecked = Installer.HasDesktopLink;
        ContextBox.IsChecked = Installer.HasContextMenu;
        bool testInstance = AppPaths.InstanceKey != "main";
        InstallButton.IsEnabled = UninstallButton.IsEnabled = DesktopBox.IsEnabled = ContextBox.IsEnabled = !testInstance;
        if (DemoContent.Enabled)
        {
            // Screenshots: nicht den echten Installationszustand des Rechners zeigen
            InstallStatus.Text = @"Installiert in %LOCALAPPDATA%\Programs\Axon";
            InstallButton.Visibility = Visibility.Collapsed;
            UninstallButton.Visibility = InstallOptions.Visibility = Visibility.Visible;
            DesktopBox.IsChecked = true;
            ContextBox.IsChecked = true;
            UninstallButton.IsEnabled = DesktopBox.IsEnabled = ContextBox.IsEnabled = true;
        }
    }

    void ApplyInstallOptions()
    {
        if (!Installer.IsInstalled) return;
        try
        {
            Installer.Install(DesktopBox.IsChecked == true, ContextBox.IsChecked == true);
        }
        catch (Exception ex)
        {
            ShowToast("Konnte Verknüpfung nicht ändern: " + ex.Message);
        }
    }

    void InstallFromSettings()
    {
        SettingsPopup.IsOpen = false;
        _ = RunInstall(true, true);
    }

    async Task UninstallFromSettings()
    {
        SettingsPopup.IsOpen = false;
        int r = await ShowDialog("Axon deinstallieren?",
            "Entfernt werden das Programm, die Verknüpfungen und das Explorer-Kontextmenü. Deine Einstellungen unter %APPDATA%\\Axon bleiben erhalten.\n\nAxon wird danach geschlossen.",
            null, "Deinstallieren", "Abbrechen");
        if (r != 0) return;
        try
        {
            Installer.Uninstall();
        }
        catch (Exception ex)
        {
            await ShowDialog("Deinstallation fehlgeschlagen", ex.Message, null, "OK");
            return;
        }
        _closingConfirmed = true;
        Close();
    }

    async Task MaybeOfferInstall()
    {
        if (AppPaths.InstanceKey != "main" || Installer.IsRunningInstalled) return;
        bool update = Installer.IsInstalled && Installer.RunningIsNewer;
        if (!update && (Installer.IsInstalled || _settings.InstallPromptDismissed)) return;

        var desktop = new CheckBox { Style = (Style)FindResource("SwitchBox"), Content = "Verknüpfung auf dem Desktop", IsChecked = true };
        var context = new CheckBox { Style = (Style)FindResource("SwitchBox"), Content = "„In Axon öffnen“ im Explorer-Kontextmenü", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        int r = update
            ? await ShowDialog("Update installieren?",
                $"Installiert ist Axon {Installer.InstalledVersion}, diese Datei enthält Version {AppPaths.Version}.", null, "Aktualisieren", "Später")
            : await ShowDialog("Axon installieren?",
                "Axon läuft gerade direkt aus dieser Datei. Mit der Installation landet Axon im Startmenü und unter „Installierte Apps“ – nur für dich, ohne Adminrechte.",
                new UIElement[] { desktop, context }, "Installieren", "Nur ausprobieren");
        if (r != 0)
        {
            if (!update) { _settings.InstallPromptDismissed = true; _settings.Save(); }
            return;
        }
        await RunInstall(update ? Installer.HasDesktopLink : desktop.IsChecked == true, update ? Installer.HasContextMenu : context.IsChecked == true);
    }

    async Task RunInstall(bool desktopLink, bool contextMenu)
    {
        try
        {
            Installer.Install(desktopLink, contextMenu);
        }
        catch (Exception ex)
        {
            await ShowDialog("Installation fehlgeschlagen", ex.Message, null, "OK");
            return;
        }
        _settings.InstallPromptDismissed = true;
        _settings.Save();
        int r = await ShowDialog("Installiert",
            "Axon ist jetzt im Startmenü zu finden" + (desktopLink ? " und auf dem Desktop" : "") + ".\nSoll die installierte Version jetzt gestartet werden? Diese Kopie wird dann geschlossen.",
            null, "Jetzt wechseln", "Hier weitermachen");
        if (r == 0)
        {
            SaveState();
            _closingConfirmed = true;
            foreach (var s in _sessions) s.Close();
            _ipc.Dispose();
            Installer.LaunchInstalled();
            Close();
        }
    }

    void ChangeFont(int delta)
    {
        double size = delta == 0 ? 14 : Math.Clamp(_settings.FontSize + delta, 9, 28);
        _settings.FontSize = size;
        _settings.Save();
        foreach (var s in _sessions) s.View.SetFont(_settings.FontFamily, size);
        FontSizeText.Text = $"{size:0} pt";
        ShowToast($"Schriftgröße {size:0} pt");
    }

    void SetAnimation(string level)
    {
        if (_settings.Animation == level) return;
        _settings.Animation = level;
        _settings.Save();
        ApplyAnimationLevel();
    }

    void CycleAnimation()
    {
        string next = _settings.Animation switch { "voll" => "dezent", "dezent" => "aus", _ => "voll" };
        SetAnimation(next);
        ShowToast("Animationen: " + next);
    }

    void ApplyAnimationLevel()
    {
        var level = _settings.Animation switch { "dezent" => AnimationLevel.Subtle, "aus" => AnimationLevel.Off, _ => AnimationLevel.Full };
        string perf = Environment.GetEnvironmentVariable("AXON_PERF") ?? "";
        Field.Level = perf.Contains("nofield") ? AnimationLevel.Off : level;
        Aura.Level = perf.Contains("noaura") ? AnimationLevel.Off : level;
        Ambient.Level = perf.Contains("noambient") ? AnimationLevel.Off : level;
        TerminalView.CursorBreathing = level != AnimationLevel.Off;
        UpdateTickerRate();
        AnimButton.ToolTip = "Animationen: " + _settings.Animation + " (klicken zum Wechseln)";
        AnimButton.Opacity = level == AnimationLevel.Off ? 0.45 : 1;
    }

    /// <summary>
    /// Takte: Schnelle Bewegung läuft im aktiven Fenster mit ~70 Bildern/s (bei 144 Hz jedes 2. Bild, bei 60 Hz jedes Bild),
    /// das träge Netz mit 30 (dezent 24) Bildern/s.
    /// Liegt Axon im Hintergrund, genügen 30 bzw. 12 Bilder/s.
    /// </summary>
    void UpdateTickerRate()
    {
        // AXON_ACTIVE=1: für Messungen so tun, als hätte das Fenster den Fokus
        bool active = IsActive || Environment.GetEnvironmentVariable("AXON_ACTIVE") == "1";
        AnimationTicker.FrameFps = active ? 70 : 30;
        AnimationTicker.Fps = !active ? 12 : _settings.Animation == "dezent" ? 24 : 30;
        // für Messungen: feste Bildrate unabhängig vom Fensterfokus
        if (double.TryParse(Environment.GetEnvironmentVariable("AXON_FPS"), out var fps) && fps > 0) AnimationTicker.Fps = fps;
    }

    void PickScreenshotFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Screenshot-Ordner wählen", InitialDirectory = _settings.EffectiveScreenshotFolder };
        SettingsPopup.IsOpen = false;
        if (dlg.ShowDialog(this) == true)
        {
            _settings.ScreenshotFolder = dlg.FolderName;
            _settings.Save();
            ShowToast("Screenshot-Ordner geändert");
        }
    }

    // ───────────────────────────── Dialoge ─────────────────────────────

    TaskCompletionSource<int>? _dialogResult;

    /// <summary>Zeigt einen Dialog im Fenster. Ergebnis: Index der Schaltfläche, -1 bei Esc.</summary>
    Task<int> ShowDialog(string title, string text, IEnumerable<UIElement>? extra, params string[] buttons)
    {
        _dialogResult?.TrySetResult(-1);
        var tcs = new TaskCompletionSource<int>();
        _dialogResult = tcs;
        DialogTitle.Text = title;
        DialogText.Text = text;
        DialogExtra.Children.Clear();
        if (extra != null) foreach (var x in extra) DialogExtra.Children.Add(x);
        DialogExtra.Visibility = DialogExtra.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DialogButtons.Children.Clear();
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource(i == 0 ? "PrimaryButton" : "GhostButton"),
                Margin = new Thickness(8, 0, 0, 0),
            };
            b.Click += (_, _) => tcs.TrySetResult(idx);
            DialogButtons.Children.Add(b);
        }
        DialogLayer.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        if (DialogButtons.Children.Count > 0) Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ((Button)DialogButtons.Children[^1]).Focus()));
        return tcs.Task.ContinueWith(t =>
        {
            if (_dialogResult == tcs)
            {
                DialogLayer.Visibility = Visibility.Collapsed;
                _dialogResult = null;
                FocusTerminal();
            }
            return t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ───────────────────────────── Schließen ─────────────────────────────

    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingConfirmed)
        {
            int busy = _sessions.Count(s => s.State is SessionState.Working or SessionState.Attention);
            if (busy > 0)
            {
                e.Cancel = true;
                int r = await ShowDialog("Axon schließen?",
                    busy == 1 ? "Claude arbeitet in einem Tab noch. Beim Schließen wird die Sitzung beendet – das Gespräch bleibt gespeichert."
                              : $"Claude arbeitet noch in {busy} Tabs. Beim Schließen werden die Sitzungen beendet – die Gespräche bleiben gespeichert.",
                    null, "Schließen", "Abbrechen");
                if (r == 0)
                {
                    _closingConfirmed = true;
                    Close();
                }
                return;
            }
        }
        SaveState();
        foreach (var s in _sessions) s.Close();
        ShutdownVoice();
        _clock.Stop();
        _ipc.Dispose();
        _notifier.Dispose();
    }

    void SaveState()
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WinLeft = Left;
            _settings.WinTop = Top;
            _settings.WinWidth = Width;
            _settings.WinHeight = Height;
        }
        else if (WindowState == WindowState.Maximized)
        {
            _settings.WinLeft = RestoreBounds.Left;
            _settings.WinTop = RestoreBounds.Top;
            _settings.WinWidth = RestoreBounds.Width;
            _settings.WinHeight = RestoreBounds.Height;
        }
        _settings.WinMaximized = WindowState == WindowState.Maximized;
        _settings.LastTabs = _sessions.Select(s => s.Folder).ToList();
        _settings.Save();
    }

    // ───────────────────────────── Test-Schnappschuss ─────────────────────────────

    /// <summary>Testmodus: Text und {Tasten} wie {Left}, {End}, {Shift+Enter} an den aktuellen Tab – ohne echte Tastatur.</summary>
    void RunTestInput(string script)
    {
        var view = _current?.View;
        if (view == null) return;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(script, @"\{([^}]+)\}|[^{]+"))
        {
            if (!m.Groups[1].Success) { view.TypeText(m.Value); continue; }
            var mods = ModifierKeys.None;
            string name = m.Groups[1].Value;
            foreach (var part in name.Split('+').SkipLast(1))
                mods |= part switch { "Shift" => ModifierKeys.Shift, "Ctrl" => ModifierKeys.Control, "Alt" => ModifierKeys.Alt, _ => ModifierKeys.None };
            if (Enum.TryParse<Key>(name.Split('+').Last(), out var key)) view.HandleKey(key, mods);
        }
    }

    void SaveSnapshot(string path)
    {
        SaveElement(Root, path);
        // AXON_SNAPSHOT_SERIES=n: weitere n Bilder im Abstand von 575 ms (Atemzug sichtbar machen)
        if (int.TryParse(Environment.GetEnvironmentVariable("AXON_SNAPSHOT_SERIES"), out int n) && n > 0)
        {
            int i = 0;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(575) };
            t.Tick += (_, _) =>
            {
                i++;
                SaveElement(Root, Path.ChangeExtension(path, null) + $"-{i}.png");
                if (i < n) return;
                t.Stop();
                _closingConfirmed = true;
                Close();
            };
            t.Start();
            return;
        }
        _closingConfirmed = true;
        Close();
    }

    void SaveElement(FrameworkElement element, string path)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var rtb = new RenderTargetBitmap((int)(element.ActualWidth * dpi.DpiScaleX), (int)(element.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(element);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Schnappschuss: " + ex);
        }
    }
}
