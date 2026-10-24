using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Axon.Services;

public static class AppPaths
{
    /// <summary>Datenordner. Für Tests per AXON_DATA umlenkbar.</summary>
    public static string DataDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("AXON_DATA") is { Length: > 0 } d
                ? d
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Axon");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HooksFile => Path.Combine(DataDir, "claude-hooks.json");
    public static string LogFile => Path.Combine(DataDir, "axon.log");

    /// <summary>Nur für Tests: lenkt Installation, Verknüpfungen und Registry in einen Testordner bzw. -zweig um.</summary>
    public static string? InstallTestRoot => Environment.GetEnvironmentVariable("AXON_INSTALL_ROOT") is { Length: > 0 } r ? r : null;

    public static string InstallDir => InstallTestRoot is { } t
        ? Path.Combine(t, "Programs", "Axon")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Axon");
    public static string InstalledExe => Path.Combine(InstallDir, "Axon.exe");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Axon.exe");

    /// <summary>Instanzname (für Tests per AXON_INSTANCE getrennt von einem normal laufenden Axon).</summary>
    public static string InstanceKey => Environment.GetEnvironmentVariable("AXON_INSTANCE") is { Length: > 0 } k ? k : "main";

    public static string PipeName
    {
        get
        {
            string raw = $"Axon-{InstanceKey}-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
            return new string(raw.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        }
    }

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}"); } catch { }
    }
}
