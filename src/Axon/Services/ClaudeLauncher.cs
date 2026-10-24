using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Axon.Services;

public static class ClaudeLauncher
{
    /// <summary>Sucht claude.exe: Einstellung → PATH → ~\.local\bin → npm-Installation.</summary>
    public static string? Find(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ClaudePath) && File.Exists(settings.ClaudePath)) return settings.ClaudePath;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim().Trim('"'), "claude.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        if (File.Exists(npm)) return npm;
        return null;
    }

    /// <summary>
    /// Schreibt die Hook-Einstellungen, die Axon per <c>--settings</c> an Claude übergibt.
    /// Die globale ~\.claude\settings.json wird nie angefasst.
    /// </summary>
    public static string WriteHookSettings()
    {
        string exe = AppPaths.ExePath;
        var events = new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Notification", "Stop", "StopFailure" };
        var hooks = new JsonObject();
        foreach (var ev in events)
        {
            hooks[ev] = new JsonArray
            {
                new JsonObject
                {
                    ["hooks"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "command",
                            ["command"] = exe,
                            ["args"] = new JsonArray("--hook"),
                            ["async"] = true,
                        },
                    },
                },
            };
        }
        var root = new JsonObject { ["hooks"] = hooks };
        // Nutzungsdaten (Kontext, Abo-Limits) kommen über Claudes Statusline – aber nur, wenn keine eigene konfiguriert ist.
        if (!UserHasStatusLine())
            root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = StatusLineCommand(exe), ["padding"] = 0 };
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        string path = AppPaths.HooksFile;
        try
        {
            if (!File.Exists(path) || File.ReadAllText(path) != json) File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppPaths.Log("Hook-Datei: " + ex.Message);
        }
        return path;
    }

    /// <summary>Hat der Benutzer in ~\.claude\settings.json schon eine eigene Statusline? Dann überschreibt Axon sie nicht.</summary>
    public static bool UserHasStatusLine()
    {
        try
        {
            string path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
            if (!File.Exists(path)) return false;
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return node?["statusLine"] != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Statusline-Befehle laufen als Shell-Zeile – in Git Bash, falls vorhanden, sonst in PowerShell.
    /// Ein Pfad ohne Leerzeichen (notfalls der kurze 8.3-Name) funktioniert in beiden.
    /// </summary>
    static string StatusLineCommand(string exe)
    {
        string path = exe;
        if (path.Contains(' '))
        {
            var sb = new System.Text.StringBuilder(1024);
            if (GetShortPathNameW(exe, sb, sb.Capacity) > 0 && !sb.ToString().Contains(' ')) path = sb.ToString();
        }
        if (!path.Contains(' ')) return path.Replace('\\', '/') + " --statusline";
        return HasGitBash() ? $"\"{exe.Replace('\\', '/')}\" --statusline" : $"& '{exe}' --statusline";
    }

    static bool HasGitBash()
    {
        if (System.Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } p && File.Exists(p)) return true;
        foreach (var root in new[] { System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData) + "\\Programs" })
            if (File.Exists(Path.Combine(root, "Git", "bin", "bash.exe"))) return true;
        return false;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, int bufferSize);

    /// <summary>Variablen, die nur eine laufende Claude-Sitzung setzt. Startet man Axon aus Claude heraus, würden sie die neue Sitzung verwirren.</summary>
    static readonly string[] SessionMarkers =
    {
        "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "AI_AGENT",
        "NO_COLOR", "GIT_TERMINAL_PROMPT",
    };

    public static Dictionary<string, string?> ChildEnvironment(string tabId)
    {
        var env = new Dictionary<string, string?>
        {
            ["AXON_PIPE"] = AppPaths.PipeName,
            ["AXON_TAB"] = tabId,
            ["COLORTERM"] = "truecolor",
            ["TERM_PROGRAM"] = "Axon",
            ["TERM_PROGRAM_VERSION"] = AppPaths.Version,
        };
        if (!string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("CLAUDECODE")))
            foreach (var name in SessionMarkers) env[name] = null;
        return env;
    }
}
