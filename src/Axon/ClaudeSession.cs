using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using Axon.Native;
using Axon.Services;
using Axon.Terminal;

namespace Axon;

public enum SessionState { Starting, Ready, Working, Attention, Done, Error, Exited }

/// <summary>Ein Tab: eine Claude-Code-Sitzung in einer eigenen Pseudokonsole.</summary>
public sealed class ClaudeSession : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Folder { get; }
    public string FolderName { get; }
    public TerminalEmulator Emulator { get; }
    public TerminalView View { get; }

    readonly AppSettings _settings;
    readonly Dispatcher _dispatcher;
    ConPtyProcess? _pty;
    string[] _args;
    bool _started;

    SessionState _state = SessionState.Starting;
    string _detail = "";
    string _claudeTitle = "";
    bool _unseen;
    bool _isSelected;

    public DateTime? TurnStart { get; private set; }
    public TimeSpan? LastTurn { get; private set; }
    public int ExitCode { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>(Sitzung, alter Zustand, neuer Zustand)</summary>
    public event Action<ClaudeSession, SessionState, SessionState>? StateChanged;
    /// <summary>Meldung von Claude (Freigabe nötig, wartet …).</summary>
    public event Action<ClaudeSession, string>? Message;
    public event Action<ClaudeSession>? InputSubmitted;
    /// <summary>Neue Nutzungsdaten aus Claudes Statusline.</summary>
    public event Action<ClaudeSession>? UsageChanged;

    /// <summary>Letzte Nutzungsdaten dieser Sitzung (Kontext, Kosten, Abo-Limits) – null bis zur ersten Meldung.</summary>
    public UsageInfo? Usage { get; private set; }

    public void HandleStatus(string json)
    {
        var u = UsageInfo.Parse(json);
        if (u == null) return;
        Usage = u;
        UsageChanged?.Invoke(this);
    }

    public ClaudeSession(string folder, IEnumerable<string> args, AppSettings settings, Dispatcher dispatcher)
    {
        Folder = folder;
        FolderName = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder;
        _settings = settings;
        _dispatcher = dispatcher;
        _args = args.ToArray();

        Emulator = new TerminalEmulator(120, 30);
        View = new TerminalView();
        View.Attach(Emulator);
        View.SetFont(settings.FontFamily, settings.FontSize);
        View.Send = Write;
        View.GridSizeChanged += OnGridSizeChanged;
        View.UserTyped += OnUserTyped;

        Emulator.Reply += Write;
        Emulator.TitleChanged += t => _dispatcher.BeginInvoke(() => ClaudeTitle = CleanTitle(t));
        Emulator.DesktopNotification += t => _dispatcher.BeginInvoke(() => Message?.Invoke(this, t));
    }

    // ───────────────────────── Eigenschaften für die Oberfläche ─────────────────────────

    public SessionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            var old = _state;
            _state = value;
            OnChanged();
            OnChanged(nameof(StateText));
            StateChanged?.Invoke(this, old, value);
        }
    }

    public string Detail
    {
        get => _detail;
        private set { if (_detail != value) { _detail = value; OnChanged(); OnChanged(nameof(StateText)); } }
    }

    public string ClaudeTitle
    {
        get => _claudeTitle;
        private set { if (_claudeTitle != value) { _claudeTitle = value; OnChanged(); OnChanged(nameof(TabTitle)); OnChanged(nameof(TabTooltip)); } }
    }

    /// <summary>Tab-Beschriftung: Ordnername; Claudes Gesprächstitel steht im Tooltip.</summary>
    public string TabTitle => FolderName;
    /// <summary>Öffentlich setzbar nur für den Demo-Modus (Screenshots).</summary>
    public void SetDemoState(SessionState s, string detail = "")
    {
        Detail = detail;
        if (s == SessionState.Working) TurnStart = DateTime.Now.AddSeconds(-24);
        State = s;
    }

    public string TabTooltip => string.IsNullOrEmpty(ClaudeTitle) || ClaudeTitle == "Claude Code" ? Folder : $"{ClaudeTitle}\n{Folder}";

    public bool Unseen
    {
        get => _unseen;
        set { if (_unseen != value) { _unseen = value; OnChanged(); } }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            if (value) Unseen = false;
            OnChanged();
        }
    }

    public string StateText => State switch
    {
        SessionState.Starting => "Startet …",
        SessionState.Ready => "Bereit",
        SessionState.Working => string.IsNullOrEmpty(Detail) ? "Claude denkt nach …" : $"Claude arbeitet · {Detail}",
        SessionState.Attention => string.IsNullOrEmpty(Detail) ? "Wartet auf dich" : Detail,
        SessionState.Done => LastTurn is { } t ? $"Fertig · {FormatDuration(t)}" : "Fertig",
        SessionState.Error => string.IsNullOrEmpty(Detail) ? "Fehler" : Detail,
        SessionState.Exited => $"Sitzung beendet (Code {ExitCode})",
        _ => "",
    };

    static string FormatDuration(TimeSpan t) => t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    static string CleanTitle(string t)
    {
        t = t.Trim();
        // Claude setzt einen Status-Glyphen vor den Titel (✳, Spinner) – den brauchen wir nicht.
        if (t.Length > 2 && !char.IsLetterOrDigit(t[0]) && t[1] == ' ') t = t[2..];
        return t;
    }

    void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ───────────────────────────── Prozess ─────────────────────────────

    void OnGridSizeChanged(int cols, int rows)
    {
        if (!_started) Start(cols, rows);
        else _pty?.Resize(cols, rows);
    }

    void Start(int cols, int rows)
    {
        _started = true;
        if (DemoContent.Enabled)
        {
            // Screenshot-Demo: nachgestellte Sitzung statt echtem Claude
            bool attention = Environment.GetEnvironmentVariable("AXON_DEMO_SCENE") == "attention" && Folder == DemoContent.Tabs[0];
            lock (Emulator.Sync) Emulator.Feed(DemoContent.Transcript(cols, attention));
            View.RequestRender();
            Usage = DemoContent.Usage();
            UsageChanged?.Invoke(this);
            State = SessionState.Ready;
            return;
        }
        string? claude = ClaudeLauncher.Find(_settings);
        if (claude == null)
        {
            Fail("Claude Code wurde nicht gefunden.",
                "Axon sucht claude.exe im PATH und unter %USERPROFILE%\\.local\\bin.\r\nIn den Einstellungen kann der Pfad auch direkt angegeben werden.");
            return;
        }

        var args = new List<string> { "--settings", ClaudeLauncher.WriteHookSettings() };
        args.AddRange(_args);
        string cmd = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? "cmd.exe /d /c " + ConPtyProcess.BuildCommandLine(claude, args)
            : ConPtyProcess.BuildCommandLine(claude, args);

        var pty = new ConPtyProcess();
        pty.Output += s =>
        {
            lock (Emulator.Sync) Emulator.Feed(s);
            View.RequestRender();
        };
        pty.Exited += code => _dispatcher.BeginInvoke(() => OnExited(pty, code));
        try
        {
            pty.Start(cmd, Directory.Exists(Folder) ? Folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ClaudeLauncher.ChildEnvironment(Id), cols, rows);
            _pty = pty;
            State = SessionState.Starting;
            AppPaths.Log($"Sitzung {Id} gestartet in {Folder} (PID {pty.ProcessId})");
        }
        catch (Exception ex)
        {
            Fail("Claude Code konnte nicht gestartet werden.", ex.Message);
        }
    }

    void Fail(string headline, string detail)
    {
        lock (Emulator.Sync)
            Emulator.Feed($"\r\n  \x1b[38;2;255;107;122m✕ {headline}\x1b[0m\r\n\r\n  \x1b[38;2;126;156;142m{detail.Replace("\r\n", "\r\n  ")}\x1b[0m\r\n");
        View.RequestRender();
        Detail = headline;
        State = SessionState.Error;
    }

    void OnExited(ConPtyProcess pty, int code)
    {
        if (!ReferenceEquals(pty, _pty)) return;
        ExitCode = code;
        _pty = null;
        pty.Dispose();
        Detail = "";
        State = SessionState.Exited;
        AppPaths.Log($"Sitzung {Id} beendet (Code {code})");
    }

    public bool IsRunning => _pty != null;

    public void Write(string s) => _pty?.Write(s);

    /// <summary>Startet Claude in diesem Tab neu (z. B. nach /exit). Mit <paramref name="continueLast"/> wird das letzte Gespräch fortgesetzt.</summary>
    public void Restart(bool continueLast)
    {
        _pty?.Dispose();
        _pty = null;
        _args = continueLast ? new[] { "--continue" } : Array.Empty<string>();
        lock (Emulator.Sync)
        {
            Emulator.Feed("\x1bc\x1b[3J");
        }
        View.RequestRender();
        Detail = "";
        LastTurn = null;
        _started = false;
        if (View.Cols > 0) Start(View.Cols, View.RowsCount);
    }

    public void Close()
    {
        _pty?.Dispose();
        _pty = null;
    }

    void OnUserTyped(string s)
    {
        bool enter = s.Contains('\r') || s.StartsWith("\x1b[13;");
        bool escape = s == "\x1b" || s.StartsWith("\x1b[27;");
        if (State == SessionState.Done) State = SessionState.Ready;
        if (State == SessionState.Attention)
        {
            // Antwort auf eine Freigabe-Abfrage: Enter oder Ziffer = weiter, Esc = abgelehnt
            if (enter || s.Length == 1 && char.IsDigit(s[0])) { Detail = ""; State = SessionState.Working; }
            else if (escape) { Detail = ""; State = SessionState.Ready; }
            return;
        }
        // Sofortiges Signal für die Animation; der UserPromptSubmit-Hook bestätigt danach den Arbeitsbeginn.
        if (enter && State is SessionState.Ready or SessionState.Starting) InputSubmitted?.Invoke(this);
    }

    // ───────────────────────────── Hooks ─────────────────────────────

    public void HandleHook(string json)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        string ev = Str(root, "hook_event_name");
        string tool = Str(root, "tool_name");
        switch (ev)
        {
            case "SessionStart":
                if (State is SessionState.Starting or SessionState.Exited) State = SessionState.Ready;
                break;
            case "UserPromptSubmit":
                TurnStart = DateTime.Now;
                Detail = "";
                State = SessionState.Working;
                break;
            case "PreToolUse":
                Detail = FriendlyTool(tool);
                if (State != SessionState.Attention) State = SessionState.Working;
                break;
            case "PostToolUse":
                if (State == SessionState.Attention) { Detail = ""; State = SessionState.Working; }
                break;
            case "PermissionRequest":
                Detail = string.IsNullOrEmpty(tool) ? "Freigabe nötig" : $"Freigabe nötig · {FriendlyTool(tool)}";
                State = SessionState.Attention;
                Message?.Invoke(this, Detail);
                break;
            case "Notification":
                {
                    string type = Str(root, "notification_type");
                    string msg = Str(root, "message");
                    if (type is "permission_prompt" or "elicitation_dialog" or "agent_needs_input")
                    {
                        if (State != SessionState.Attention)
                        {
                            Detail = string.IsNullOrEmpty(msg) ? "Wartet auf dich" : Shorten(msg);
                            State = SessionState.Attention;
                        }
                        Message?.Invoke(this, string.IsNullOrEmpty(msg) ? "Claude wartet auf dich" : msg);
                    }
                    else if (type == "idle_prompt")
                    {
                        Message?.Invoke(this, string.IsNullOrEmpty(msg) ? "Claude wartet auf deine Eingabe" : msg);
                    }
                    break;
                }
            case "Stop":
                if (TurnStart is { } ts) LastTurn = DateTime.Now - ts;
                TurnStart = null;
                Detail = "";
                State = SessionState.Done;
                break;
            case "StopFailure":
                TurnStart = null;
                Detail = "Abbruch durch API-Fehler";
                State = SessionState.Error;
                break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static string Shorten(string s) => s.Length > 70 ? s[..67] + " …" : s;

    static string FriendlyTool(string tool) => tool switch
    {
        "Bash" => "Befehl",
        "PowerShell" => "PowerShell",
        "Read" => "liest Datei",
        "Write" => "schreibt Datei",
        "Edit" or "MultiEdit" => "bearbeitet Datei",
        "Grep" or "Glob" => "sucht",
        "WebFetch" => "lädt Webseite",
        "WebSearch" => "sucht im Web",
        "Agent" or "Task" => "Subagent",
        "TodoWrite" => "plant",
        "" => "",
        _ when tool.StartsWith("mcp__") => tool.Split("__").Last(),
        _ => tool,
    };
}
