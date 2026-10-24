using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Axon.Services;

public sealed class RecentFolder
{
    public string Path { get; set; } = "";
    public DateTime LastUsed { get; set; }
}

public sealed class AppSettings
{
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Cascadia Mono";
    /// <summary>"voll", "dezent" oder "aus".</summary>
    public string Animation { get; set; } = "voll";
    public bool Notifications { get; set; } = true;
    public bool StartAnimation { get; set; } = true;
    /// <summary>Beim Start bei GitHub nach einer neueren Version fragen.</summary>
    public bool UpdateCheck { get; set; } = true;
    public string? ScreenshotFolder { get; set; }
    /// <summary>Whisper-Modell der Spracheingabe: "small" oder "turbo".</summary>
    public string VoiceModel { get; set; } = "small";
    /// <summary>Gewähltes Mikrofon (voller Name); null = immer das Windows-Standardmikrofon.</summary>
    public string? VoiceDevice { get; set; }
    public string? ClaudePath { get; set; }
    public bool SidebarOpen { get; set; } = true;
    /// <summary>Was die Seitenleiste zeigt: "projekte" oder "chats".</summary>
    public string SidebarView { get; set; } = "projekte";
    public bool InstallPromptDismissed { get; set; }

    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public bool WinMaximized { get; set; }

    public List<RecentFolder> Recent { get; set; } = new();
    /// <summary>Aus der Projektliste entfernte Ordner mit Zeitpunkt. Erst wenn der Ordner in Axon wieder geöffnet wird, erscheint er erneut.</summary>
    public Dictionary<string, DateTime> HiddenProjects { get; set; } = new();
    public List<string> LastTabs { get; set; } = new();

    /// <summary>Letzter bekannter Stand der Abo-Limits (gilt fürs ganze Konto, nicht pro Tab).</summary>
    public RateWindow? LastFiveHour { get; set; }
    public RateWindow? LastWeek { get; set; }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string EffectiveScreenshotFolder =>
        string.IsNullOrWhiteSpace(ScreenshotFolder)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots")
            : ScreenshotFolder!;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Einstellungen nicht lesbar: " + ex.Message);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Einstellungen nicht speicherbar: " + ex.Message);
        }
    }

    public void TouchRecent(string folder)
    {
        Recent.RemoveAll(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, new RecentFolder { Path = folder, LastUsed = DateTime.Now });
        foreach (var key in HiddenProjects.Keys.Where(k => string.Equals(k, folder, StringComparison.OrdinalIgnoreCase)).ToList())
            HiddenProjects.Remove(key);
        if (Recent.Count > 30) Recent = Recent.Take(30).ToList();
    }

    public void HideProject(string folder)
    {
        Recent.RemoveAll(r => string.Equals(r.Path, folder, StringComparison.OrdinalIgnoreCase));
        foreach (var key in HiddenProjects.Keys.Where(k => string.Equals(k, folder, StringComparison.OrdinalIgnoreCase)).ToList())
            HiddenProjects.Remove(key);
        HiddenProjects[folder] = DateTime.Now;
        LastTabs.RemoveAll(t => string.Equals(t, folder, StringComparison.OrdinalIgnoreCase));
    }
}
