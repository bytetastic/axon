using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Axon.Services;

namespace Axon;

public partial class App : Application
{
    readonly string[] _args;
    Mutex? _mutex;

    public App() : this(Environment.GetCommandLineArgs().Skip(1).ToArray()) { }

    public App(string[] args) => _args = args;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        if (_args.Contains("--uninstall"))
        {
            if (_args.Contains("--quiet")) { QuietAction(Installer.Uninstall); return; }
            RunUninstall();
            return;
        }
        if (_args.Contains("--install"))
        {
            // Stille Installation, z. B. für Skripte: Axon.exe --install [--desktop] [--context-menu]
            QuietAction(() => Installer.Install(_args.Contains("--desktop"), _args.Contains("--context-menu")));
            return;
        }

        string? folder = null;
        string? snapshot = null;
        int snapshotDelay = 4000;
        for (int i = 0; i < _args.Length; i++)
        {
            string a = _args[i];
            if (a == "--snapshot" && i + 1 < _args.Length) snapshot = _args[++i];
            else if (a == "--snapshot-delay" && i + 1 < _args.Length && int.TryParse(_args[++i], out var ms)) snapshotDelay = ms;
            else if (!a.StartsWith("--") && folder == null)
            {
                string p = a.Trim('"');
                if (Directory.Exists(p)) folder = Path.GetFullPath(p);
            }
        }

        // Update anwenden: Axon.exe --apply-update <pid> – wartet, bis das alte Axon beendet ist, installiert sich und startet neu.
        int applyIdx = Array.IndexOf(_args, "--apply-update");
        if (applyIdx >= 0)
        {
            ApplyUpdate(applyIdx + 1 < _args.Length && int.TryParse(_args[applyIdx + 1], out var pid) ? pid : 0);
            return;
        }

        // Nur ein Axon pro Benutzer: ein zweiter Start öffnet den Ordner als Tab im laufenden Fenster –
        // oder, wenn diese Datei eine neuere Version ist, bietet das laufende Axon das Update an.
        _mutex = new Mutex(true, "Local\\" + AppPaths.PipeName, out bool created);
        if (!created && snapshot == null)
        {
            bool newer = !Installer.IsRunningInstalled && Installer.RunningIsNewer;
            string message = newer ? "U\t" + AppPaths.ExePath : "O\t" + (folder ?? "");
            if (IpcServer.Send(AppPaths.PipeName, message))
            {
                Shutdown();
                return;
            }
        }

        var settings = AppSettings.Load();
        var win = new MainWindow(settings, folder, snapshot, snapshotDelay);
        MainWindow = win;
        win.Show();
    }

    void RunUninstall()
    {
        var r = MessageBox.Show(
            "Axon wirklich deinstallieren?\n\nEntfernt werden das Programm, die Verknüpfungen und das Explorer-Kontextmenü.\n" +
            "Deine Einstellungen unter %APPDATA%\\Axon bleiben erhalten.",
            "Axon deinstallieren", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes)
        {
            try
            {
                Installer.Uninstall();
                MessageBox.Show("Axon wurde entfernt.", "Axon", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Deinstallation fehlgeschlagen:\n" + ex.Message, "Axon", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Shutdown();
    }

    void ApplyUpdate(int oldPid)
    {
        try
        {
            if (oldPid > 0)
            {
                try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(20000); } catch (ArgumentException) { }
            }
            // gleiche Optionen wie bisher (Desktop-Verknüpfung, Kontextmenü)
            bool desktop = Installer.HasDesktopLink, context = Installer.HasContextMenu;
            Exception? last = null;
            for (int i = 0; i < 10; i++)
            {
                try { Installer.Install(desktop, context); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); } // Datei kann kurz noch gesperrt sein
            }
            if (last != null) throw last;
            Installer.LaunchInstalled();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Update fehlgeschlagen: " + ex);
            MessageBox.Show("Das Update konnte nicht installiert werden:\n" + ex.Message, "Axon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
    }

    void QuietAction(Action action)
    {
        int code = 0;
        try { action(); }
        catch (Exception ex)
        {
            AppPaths.Log("Installation/Deinstallation: " + ex);
            code = 1;
        }
        Shutdown(code);
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("Unbehandelter Fehler: " + e.Exception);
        e.Handled = true;
        // Fehler schon beim Aufbau: nicht unsichtbar weiterlaufen.
        if (MainWindow == null || !MainWindow.IsLoaded)
        {
            MessageBox.Show("Axon konnte nicht starten:\n\n" + e.Exception.Message + "\n\nDetails stehen in " + AppPaths.LogFile,
                "Axon", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
