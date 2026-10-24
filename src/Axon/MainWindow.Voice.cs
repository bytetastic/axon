using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Axon.Services;
using Axon.Visuals;

namespace Axon;

/// <summary>Spracheingabe: Knopf an → sprechen → Knopf aus → Whisper erkennt lokal → Text steht in der Eingabezeile (nicht abgeschickt).</summary>
public partial class MainWindow
{
    enum VoiceState { Idle, Downloading, Recording, Transcribing }

    readonly VoiceRecorder _voice = new();
    readonly DispatcherTimer _voiceClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    VoiceState _voiceState;
    ClaudeSession? _voiceTarget;
    CancellationTokenSource? _voiceDownload;
    string _voiceDevice = "";
    double _voiceProgress;

    void WireVoice()
    {
        MicButton.Click += (_, _) => ToggleVoice();
        MicMeter.LevelSource = () => _voice.Level;
        VoiceWave.LevelSource = () => _voice.Level;
        VoicePill.MouseLeftButtonUp += (_, _) => { if (_voiceState == VoiceState.Recording) ToggleVoice(); };
        _voiceClock.Tick += (_, _) => VoiceTime.Text = TimeSpan.FromSeconds(_voice.Seconds).ToString(@"m\:ss");
        _voice.Ended += () => Dispatcher.BeginInvoke(() => { if (_voiceState == VoiceState.Recording) _ = FinishVoice(); });
        VoiceSmall.Checked += (_, _) => SetVoiceModel("small");
        VoiceTurbo.Checked += (_, _) => SetVoiceModel("turbo");
    }

    async void ToggleVoice()
    {
        switch (_voiceState)
        {
            case VoiceState.Recording: await FinishVoice(); return;
            case VoiceState.Transcribing: ShowToast("Text wird noch erkannt …"); return;
            case VoiceState.Downloading: ShowToast($"Sprachmodell wird geladen … {_voiceProgress * 100:0} %"); return;
        }
        if (_current == null) { ShowToast("Erst einen Tab öffnen"); return; }

        var model = Dictation.ModelFor(_settings.VoiceModel);
        if (!Dictation.IsDownloaded(model))
        {
            if (!await DownloadVoiceModel(model)) return;
            ShowToast("Spracheingabe bereit – Knopf drücken und sprechen");
            return;
        }

        var mic = Microphones.Resolve(_settings.VoiceDevice, out string? note);
        if (note != null) ShowToast(note);
        if (mic == null && Environment.GetEnvironmentVariable("AXON_TEST_VOICE") is not { Length: > 0 })
        {
            if (note == null) ShowToast("Kein Mikrofon gefunden");
            return;
        }
        try { _voice.Start(mic?.Id ?? 0); }
        catch (Exception ex)
        {
            AppPaths.Log("Mikrofon: " + ex.Message);
            ShowToast(ex.Message);
            return;
        }
        _voiceDevice = DemoContent.Enabled ? "Mikrofon (USB-Headset)" : mic?.Name ?? "Testdatei";
        AppPaths.Log("Aufnahme mit " + _voiceDevice);
        _voiceTarget = _current;
        SetVoiceState(VoiceState.Recording);
    }

    async Task FinishVoice()
    {
        var samples = _voice.Stop();
        float peak = _voice.Peak;
        var target = _voiceTarget;
        SetVoiceState(VoiceState.Transcribing);
        try
        {
            if (peak == 0)
            {
                ShowToast("Vom Mikrofon kommt kein Ton – Windows-Einstellungen › Datenschutz › Mikrofon prüfen");
                return;
            }
            var model = Dictation.ModelFor(_settings.VoiceModel);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string text = await Dictation.Transcribe(samples, model);
            AppPaths.Log($"Spracheingabe: {samples.Length / (double)VoiceRecorder.SampleRate:0.0} s Ton, {model.Key}, {sw.ElapsedMilliseconds} ms, {text.Length} Zeichen");
            if (text.Length == 0) { ShowToast("Nichts verstanden"); return; }
            if (target == null || !_sessions.Contains(target)) { ShowToast("Der Tab ist inzwischen geschlossen"); return; }
            target.View.PasteText(text + " ");
            if (target == _current) FocusTerminal();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Spracheingabe fehlgeschlagen: " + ex);
            ShowToast("Spracherkennung fehlgeschlagen: " + ex.Message);
        }
        finally
        {
            _voiceTarget = null;
            SetVoiceState(VoiceState.Idle);
        }
    }

    // ───────────── Modell-Download ─────────────

    async Task<bool> DownloadVoiceModel(VoiceModel model)
    {
        int r = await ShowDialog("Spracheingabe einrichten?",
            $"Axon erkennt Sprache komplett auf diesem Rechner (Whisper). Dafür wird einmalig das Sprachmodell „{model.Label}“ " +
            $"von Hugging Face geladen ({model.SizeText}). Danach verlässt beim Diktieren nichts den Rechner.",
            null, "Laden", "Abbrechen");
        if (r != 0) return false;

        _voiceDownload = new CancellationTokenSource();
        _voiceProgress = 0;
        SetVoiceState(VoiceState.Downloading);
        try
        {
            var progress = new Progress<double>(p => { _voiceProgress = p; MicMeter.Progress = p; });
            await Dictation.Download(model, progress, _voiceDownload.Token);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppPaths.Log("Modell-Download fehlgeschlagen: " + ex);
            ShowToast("Download fehlgeschlagen: " + ex.Message);
            return false;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            _voiceDownload.Dispose();
            _voiceDownload = null;
            SetVoiceState(VoiceState.Idle);
            UpdateVoiceSettings();
        }
    }

    void SetVoiceState(VoiceState s)
    {
        _voiceState = s;
        MicMeter.Mode = s switch
        {
            VoiceState.Recording => VoiceMode.Recording,
            VoiceState.Transcribing => VoiceMode.Busy,
            VoiceState.Downloading => VoiceMode.Progress,
            _ => VoiceMode.Idle,
        };
        if (s == VoiceState.Recording) MicButton.Foreground = new SolidColorBrush(MoodPalette.Coral);
        else MicButton.ClearValue(ForegroundProperty);
        MicButton.Content = s == VoiceState.Recording ? "" : ""; // Stopp-Quadrat bzw. Mikrofon
        MicButton.ToolTip = s switch
        {
            VoiceState.Recording => $"Aufnahme läuft · {_voiceDevice} – klicken zum Beenden",
            VoiceState.Transcribing => "Text wird erkannt …",
            VoiceState.Downloading => "Sprachmodell wird geladen …",
            _ => "Spracheingabe – klicken, sprechen, nochmal klicken (Strg+Umschalt+Leertaste)",
        };
        UpdateVoicePill();
    }

    // ───────────── Klangkurve ─────────────

    void UpdateVoicePill()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        switch (_voiceState)
        {
            case VoiceState.Recording:
                VoiceTime.Text = "0:00";
                VoiceDot.Fill = new SolidColorBrush(MoodPalette.Coral);
                VoicePill.BorderBrush = new SolidColorBrush(MoodPalette.WithAlpha(MoodPalette.Coral, 0.35));
                VoicePill.ToolTip = $"{_voiceDevice} · klicken beendet die Aufnahme";
                VoiceWave.Run(true);
                _voiceClock.Start();
                if (VoicePill.Visibility != Visibility.Visible || VoicePill.Opacity < 1)
                {
                    VoicePill.Visibility = Visibility.Visible;
                    VoicePill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
                    VoicePillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
                }
                break;
            case VoiceState.Transcribing:
                _voiceClock.Stop();
                VoiceTime.Text = "erkenne …";
                VoiceDot.Fill = new SolidColorBrush(MoodPalette.Mint);
                VoicePill.BorderBrush = new SolidColorBrush(MoodPalette.WithAlpha(MoodPalette.Emerald, 0.35));
                VoicePill.ToolTip = null;
                VoiceWave.Run(false);
                break;
            default:
                _voiceClock.Stop();
                if (VoicePill.Visibility != Visibility.Visible) return;
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(420));
                fade.Completed += (_, _) =>
                {
                    if (_voiceState == VoiceState.Recording) return; // inzwischen neu gestartet
                    VoicePill.Visibility = Visibility.Collapsed;
                    VoiceWave.Stop();
                };
                VoicePill.BeginAnimation(OpacityProperty, fade);
                VoicePillShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, TimeSpan.FromMilliseconds(420)));
                break;
        }
    }

    // ───────────── Einstellungen ─────────────

    void SetVoiceModel(string key)
    {
        if (_settings.VoiceModel == key) return;
        _settings.VoiceModel = key;
        _settings.Save();
        Dictation.Unload();
        UpdateVoiceSettings();
    }

    void UpdateVoiceSettings()
    {
        VoiceSmall.IsChecked = _settings.VoiceModel == "small";
        VoiceTurbo.IsChecked = _settings.VoiceModel == "turbo";
        var m = Dictation.ModelFor(_settings.VoiceModel);
        VoiceStatus.Text = Dictation.IsDownloaded(m)
            ? $"Modell geladen ({m.SizeText}) · läuft lokal, nichts verlässt den Rechner"
            : $"Wird beim ersten Diktieren geladen ({m.SizeText})";

        // Mikrofone: oben immer „Windows-Standard“; ein gewähltes, gerade fehlendes Gerät bleibt sichtbar
        VoiceDevices.Children.Clear();
        if (DemoContent.Enabled)
        {
            // Screenshots: keine echten Geräte des Rechners zeigen
            VoiceStatus.Text = $"Modell geladen ({m.SizeText}) · läuft lokal, nichts verlässt den Rechner";
            AddDeviceOption(null, "Windows-Standard · Mikrofon (USB-Headset)");
            AddDeviceOption("demo-headset", "Mikrofon (USB-Headset)");
            AddDeviceOption("demo-array", "Mikrofonarray (integriert)");
            return;
        }
        var def = Microphones.Default();
        AddDeviceOption(null, "Windows-Standard" + (def != null ? " · " + def.Name : ""));
        var mics = Microphones.List();
        foreach (var mic in mics) AddDeviceOption(mic.Name, mic.Name);
        if (_settings.VoiceDevice != null && mics.All(x => x.Name != _settings.VoiceDevice))
            AddDeviceOption(_settings.VoiceDevice, _settings.VoiceDevice + " (nicht angeschlossen)");
    }

    void AddDeviceOption(string? name, string label)
    {
        var rb = new RadioButton
        {
            Style = (Style)FindResource("SegmentButton"),
            GroupName = "mic",
            Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
            ToolTip = label,
            IsChecked = _settings.VoiceDevice == name,
        };
        rb.Checked += (_, _) =>
        {
            if (_settings.VoiceDevice == name || DemoContent.Enabled) return;
            _settings.VoiceDevice = name;
            _settings.Save();
        };
        VoiceDevices.Children.Add(rb);
    }

    void ShutdownVoice()
    {
        _voiceDownload?.Cancel();
        _voiceClock.Stop();
        if (_voice.IsRecording) _voice.Stop();
        VoiceWave.Stop();
        Dictation.Unload();
    }
}
