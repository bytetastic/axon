using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Axon.Native;
using Axon.Terminal;

namespace Axon.Tests;

/// <summary>
/// Kleine Testsuite ohne Framework.
///   dotnet run                      → Parser- und ConPTY-Tests
///   dotnet run -- claude &lt;ordner&gt;   → startet Claude Code in einer ConPTY, wartet und gibt den Bildschirm aus
/// </summary>
public static class Program
{
    static int _failed, _passed;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length >= 2 && args[0] == "claude") return ClaudeProbe(args[1], args.Length > 2 ? args[2] : null);

        ParserTests();
        ConPtyTests();
        Console.WriteLine();
        Console.WriteLine($"Bestanden: {_passed}, fehlgeschlagen: {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  ok    {name}"); }
        else { _failed++; Console.WriteLine($"  FEHLT {name} {detail}"); }
    }

    static string Row(TerminalEmulator t, int y) => TerminalEmulator.LineText(t.ScreenLine(y)).TrimEnd();

    static void ParserTests()
    {
        Console.WriteLine("Parser");

        var t = new TerminalEmulator(10, 3);
        t.Feed("Hallo");
        Check("Text drucken", Row(t, 0) == "Hallo" && t.CursorX == 5);

        t.Feed("\r\nWelt");
        Check("CR LF", Row(t, 1) == "Welt" && t.CursorY == 1);

        t = new TerminalEmulator(5, 3);
        t.Feed("abcdefg");
        Check("Autowrap", Row(t, 0) == "abcde" && Row(t, 1) == "fg" && t.ScreenLine(0).Wrapped);

        t = new TerminalEmulator(5, 2);
        t.Feed("1\r\n2\r\n3\r\n4");
        Check("Scrollback", t.Scrollback.Count == 2 && Row(t, 0) == "3" && Row(t, 1) == "4",
            $"sb={t.Scrollback.Count} r0='{Row(t, 0)}'");

        t = new TerminalEmulator(10, 3);
        t.Feed("xxxxxxxxxx\x1b[2;3Hab\x1b[1;1H\x1b[K");
        Check("CUP + EL", Row(t, 0) == "" && Row(t, 1) == "  ab");

        t = new TerminalEmulator(10, 3);
        t.Feed("\x1b[38;2;10;20;30mA\x1b[38:2::1:2:3mB\x1b[38;5;196mC\x1b[1;4:3mD\x1b[0mE");
        var c = t.ScreenLine(0).Cells;
        Check("SGR Truecolor ;", c[0].Attr.Fg == CellAttr.Rgb(10, 20, 30));
        Check("SGR Truecolor :", c[1].Attr.Fg == CellAttr.Rgb(1, 2, 3));
        Check("SGR 256", c[2].Attr.Fg == CellAttr.Palette(196));
        Check("SGR Bold + Curly", (c[3].Attr.Flags & CellFlags.Bold) != 0 && (c[3].Attr.Flags & CellFlags.CurlyUnderline) != 0);
        Check("SGR Reset", c[4].Attr.Fg == 0 && c[4].Attr.Flags == 0);

        t = new TerminalEmulator(10, 2);
        t.Feed("a漢b");
        c = t.ScreenLine(0).Cells;
        Check("Breites Zeichen", c[1].Kind == Cell.WideLead && c[2].Kind == Cell.WideTail && c[3].Rune == 'b' && t.CursorX == 4);

        t = new TerminalEmulator(10, 2);
        t.Feed("\U0001F600x");
        c = t.ScreenLine(0).Cells;
        Check("Emoji (Surrogate)", c[0].Rune == 0x1F600 && c[0].Kind == Cell.WideLead && c[2].Rune == 'x');

        t = new TerminalEmulator(10, 3);
        t.Feed("\x1b]0;Mein Titel\x07");
        Check("OSC Titel (BEL)", t.Title == "Mein Titel");
        t.Feed("\x1b]2;Zweiter ✳\x1b\\");
        Check("OSC Titel (ST)", t.Title == "Zweiter ✳");

        t = new TerminalEmulator(10, 3);
        t.Feed("\x1b[?1049hALT\x1b[?1049l");
        Check("Alt-Screen", Row(t, 0) == "" && !t.AltActive);

        t = new TerminalEmulator(10, 3);
        t.Feed("\x1b[?2004h\x1b[?9001h\x1b[?25l");
        Check("Modi", t.BracketedPaste && t.Win32InputMode && !t.CursorVisible);

        t = new TerminalEmulator(10, 3);
        var replies = new List<string>();
        t.Reply += replies.Add;
        t.Feed("\x1b[2;4H\x1b[6n\x1b[c");
        Check("DSR/DA Antworten", replies.Count == 2 && replies[0] == "\x1b[2;4R", string.Join("|", replies).Replace("\x1b", "ESC"));

        t = new TerminalEmulator(6, 4);
        t.Feed("a\r\nb\r\nc\r\nd");
        t.Feed("\x1b[2;3r\x1b[2;1H\x1b[M");
        Check("Scrollbereich + DL", Row(t, 0) == "a" && Row(t, 1) == "c" && Row(t, 2) == "" && Row(t, 3) == "d");

        t = new TerminalEmulator(6, 2);
        t.Feed("\x1b(0lqqk\x1b(B");
        Check("DEC-Liniengrafik", Row(t, 0) == "┌──┐");

        t = new TerminalEmulator(10, 4);
        t.Feed("1\r\n2\r\n3\r\n4");
        t.Resize(10, 2);
        Check("Verkleinern schiebt in Scrollback", Row(t, 0) == "3" && Row(t, 1) == "4" && t.Scrollback.Count == 2 && t.CursorY == 1);
        t.Resize(10, 4);
        Check("Vergrößern holt zurück", Row(t, 0) == "1" && Row(t, 3) == "4" && t.Scrollback.Count == 0 && t.CursorY == 3);

        t = new TerminalEmulator(10, 3);
        t.Feed("abc\x1b[1;2H\x1b[2@X");
        Check("ICH", Row(t, 0) == "aX bc");
        t.Feed("\x1b[1;1H\x1b[2P");
        Check("DCH", Row(t, 0) == " bc");

        t = new TerminalEmulator(20, 3);
        t.Feed("\x1b]8;;https://example.org\x1b\\Link\x1b]8;;\x1b\\ x");
        c = t.ScreenLine(0).Cells;
        Check("OSC 8 Hyperlink", c[0].Attr.Link != 0 && t.GetLink(c[0].Attr.Link) == "https://example.org" && c[5].Attr.Link == 0);
    }

    static void ConPtyTests()
    {
        Console.WriteLine("ConPTY");

        var (t1, raw1, code1) = RunInPty("cmd.exe /c echo Hallo Axon", 80, 10, 5000);
        Check("cmd echo", ScreenContains(t1, "Hallo Axon") && code1 == 0, $"code={code1}");

        var (t2, raw2, code2) = RunInPty("cmd.exe /c dir /b C:\\Windows\\System32", 80, 20, 15000);
        Check("Lange Ausgabe landet im Scrollback", t2.Scrollback.Count > 100, $"sb={t2.Scrollback.Count}");

        string claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(claude))
        {
            var (t3, _, code3) = RunInPty(ConPtyProcess.Quote(claude) + " --version", 80, 10, 15000);
            Check("claude --version", ScreenContains(t3, "Claude Code") && code3 == 0);
        }

        string sample = raw1.Length > 300 ? raw1[..300] : raw1;
        Console.WriteLine("  Rohausgabe (Anfang): " + Escape(sample));
    }

    static bool ScreenContains(TerminalEmulator t, string s)
    {
        for (int i = 0; i < t.Scrollback.Count; i++) if (TerminalEmulator.LineText(t.Scrollback[i]).Contains(s)) return true;
        for (int y = 0; y < t.Rows; y++) if (Row(t, y).Contains(s)) return true;
        return false;
    }

    static (TerminalEmulator, string, int) RunInPty(string cmd, int cols, int rows, int timeoutMs, Action<ConPtyProcess>? interact = null)
    {
        var t = new TerminalEmulator(cols, rows);
        var raw = new StringBuilder();
        var pty = new ConPtyProcess();
        var done = new ManualResetEventSlim();
        int code = -1;
        t.Reply += s => pty.Write(s);
        pty.Output += s => { lock (t.Sync) { raw.Append(s); t.Feed(s); } };
        pty.Exited += c => { code = c; done.Set(); };
        pty.Start(cmd, Environment.CurrentDirectory, new Dictionary<string, string?>(), cols, rows);
        interact?.Invoke(pty);
        done.Wait(timeoutMs);
        pty.Dispose();
        lock (t.Sync) return (t, raw.ToString(), code);
    }

    static string Escape(string s) => s.Replace("\x1b", "␛").Replace("\r", "␍").Replace("\n", "␊");

    /// <summary>Startet Claude Code interaktiv, wartet, gibt den Bildschirm und die Rohdaten aus. Schickt keine Eingabe.</summary>
    static int ClaudeProbe(string folder, string? rawOut)
    {
        string claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        var t = new TerminalEmulator(100, 32);
        var raw = new StringBuilder();
        var pty = new ConPtyProcess();
        t.Reply += s => pty.Write(s);
        pty.Output += s => { lock (t.Sync) { raw.Append(s); t.Feed(s); } };
        var env = new Dictionary<string, string?> { ["COLORTERM"] = "truecolor" };
        foreach (var name in new[] { "NO_COLOR", "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID",
                     "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_PID", "AI_AGENT", "GIT_TERMINAL_PROMPT" })
            env[name] = null;
        pty.Start(ConPtyProcess.Quote(claude), folder, env, 100, 32);
        Thread.Sleep(9000);
        lock (t.Sync)
        {
            Console.WriteLine($"Modi: win32={t.Win32InputMode} bracketed={t.BracketedPaste} cursor={t.CursorVisible} alt={t.AltActive} focus={t.FocusEvents} titel='{t.Title}'");
            for (int y = 0; y < t.Rows; y++) Console.WriteLine("|" + Row(t, y));
            if (rawOut != null) File.WriteAllText(rawOut, Escape(raw.ToString()));
        }
        pty.Dispose();
        Thread.Sleep(500);
        return 0;
    }
}
