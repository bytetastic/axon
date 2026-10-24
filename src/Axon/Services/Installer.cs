using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Axon.Services;

/// <summary>
/// Installation ohne Setup-Programm: Axon.exe kopiert sich nach %LOCALAPPDATA%\Programs\Axon,
/// legt Verknüpfungen an und trägt sich unter „Installierte Apps“ ein (nur für den aktuellen Benutzer, ohne Adminrechte).
/// </summary>
public static class Installer
{
    static bool Test => AppPaths.InstallTestRoot != null;
    static string UninstallKey => Test ? @"Software\AxonTest\Uninstall\Axon" : @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Axon";
    static string DirShellKey => Test ? @"Software\AxonTest\Directory\shell\Axon" : @"Software\Classes\Directory\shell\Axon";
    static string DirBgShellKey => Test ? @"Software\AxonTest\Directory\Background\shell\Axon" : @"Software\Classes\Directory\Background\shell\Axon";

    static string StartMenuLink => Test
        ? Path.Combine(AppPaths.InstallTestRoot!, "StartMenu", "Axon.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Axon.lnk");
    static string DesktopLink => Test
        ? Path.Combine(AppPaths.InstallTestRoot!, "Desktop", "Axon.lnk")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Axon.lnk");

    public static bool IsRunningInstalled =>
        string.Equals(Path.GetFullPath(AppPaths.ExePath), Path.GetFullPath(AppPaths.InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static bool IsInstalled => File.Exists(AppPaths.InstalledExe);

    public static bool HasDesktopLink => File.Exists(DesktopLink);

    public static bool HasContextMenu
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(DirShellKey);
            return k != null;
        }
    }

    public static string? InstalledVersion
    {
        get
        {
            if (!IsInstalled) return null;
            try
            {
                var v = FileVersionInfo.GetVersionInfo(AppPaths.InstalledExe);
                return $"{v.FileMajorPart}.{v.FileMinorPart}.{v.FileBuildPart}";
            }
            catch { return null; }
        }
    }

    /// <summary>Ist die gerade laufende Datei neuer als die installierte?</summary>
    public static bool RunningIsNewer
    {
        get
        {
            var inst = InstalledVersion;
            if (inst == null) return false;
            return System.Version.TryParse(AppPaths.Version, out var a) && System.Version.TryParse(inst, out var b) && a > b;
        }
    }

    public static void Install(bool desktopLink, bool contextMenu)
    {
        Directory.CreateDirectory(AppPaths.InstallDir);
        File.Copy(AppPaths.ExePath, AppPaths.InstalledExe, true);
        string exe = AppPaths.InstalledExe;
        // Die Markierung „aus dem Internet heruntergeladen“ (Zone.Identifier) kopiert Windows mit. Der Benutzer hat die Datei
        // bereits bewusst gestartet – die installierte Kopie soll nicht bei jedem Start erneut die SmartScreen-Warnung zeigen.
        DeleteFileW(exe + ":Zone.Identifier");

        CreateShortcut(StartMenuLink, exe);
        if (desktopLink) CreateShortcut(DesktopLink, exe);
        else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);

        SetContextMenu(contextMenu, exe);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Axon");
            k.SetValue("DisplayVersion", AppPaths.Version);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("Publisher", "Axon");
            k.SetValue("InstallLocation", AppPaths.InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
    }

    public static void SetContextMenu(bool on, string exe)
    {
        if (on)
        {
            foreach (var (key, arg) in new[] { (DirShellKey, "%V"), (DirBgShellKey, "%V") })
            {
                using var k = Registry.CurrentUser.CreateSubKey(key);
                k.SetValue("", "In Axon öffnen");
                k.SetValue("Icon", exe + ",0");
                using var c = k.CreateSubKey("command");
                c.SetValue("", $"\"{exe}\" \"{arg}\"");
            }
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(DirShellKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(DirBgShellKey, false);
        }
    }

    /// <summary>Entfernt Verknüpfungen, Kontextmenü und Eintrag. Die Exe löscht ein kurzer cmd-Nachlauf, sobald Axon beendet ist.
    /// Einstellungen unter %APPDATA%\Axon bleiben erhalten.</summary>
    public static void Uninstall()
    {
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        SetContextMenu(false, "");
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);

        if (IsInstalled)
        {
            string cmd = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{AppPaths.InstalledExe}\" & rmdir \"{AppPaths.InstallDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }

    public static void LaunchInstalled()
    {
        Process.Start(new ProcessStartInfo(AppPaths.InstalledExe) { UseShellExecute = true, WorkingDirectory = AppPaths.InstallDir });
    }

    static void CreateShortcut(string lnkPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
        Type? t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.IconLocation = target + ",0";
        lnk.Description = "Axon – Claude Code als Desktop-App";
        lnk.Save();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool DeleteFileW(string path);

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
