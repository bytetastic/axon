using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Axon.Services;

public sealed class ChatEntry
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string ProjectPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public DateTime LastUsed { get; init; }
    /// <summary>Zeitabschnitt („Heute“, „Gestern“ …); die Überschrift steht nur am ersten Chat eines Abschnitts.</summary>
    public string Group { get; set; } = "";
    public bool ShowGroup { get; set; }
    /// <summary>Läuft gerade in einem Tab.</summary>
    public bool IsOpen { get; set; }

    public string LastUsedText => ProjectEntry.Ago(LastUsed);
    public string Tooltip => $"{Title}\n{ProjectPath}";
}

/// <summary>Einzelne Gespräche aus Claude Codes Sitzungsdateien (~\.claude\projects\&lt;ordner&gt;\&lt;id&gt;.jsonl).</summary>
public static class ChatHistory
{
    // Titel ändern sich nicht mehr, sobald Claude einen vergeben hat – deshalb pro Datei merken (die Dateien werden bis zu 60 MB groß).
    static readonly Dictionary<string, (string Cwd, string Title)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static List<ChatEntry> Load(IReadOnlyDictionary<string, DateTime> hiddenProjects, int max = 60)
    {
        if (DemoContent.Enabled) return Grouped(DemoContent.Chats());
        var hidden = new HashSet<string>(hiddenProjects.Keys.Select(k => k.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        string temp = Path.GetTempPath().TrimEnd('\\');
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var list = new List<ChatEntry>();
        try
        {
            if (!Directory.Exists(root)) return list;
            // nur die Gespräche selbst – Subagent-Verläufe liegen eine Ebene tiefer
            var files = new DirectoryInfo(root).EnumerateDirectories()
                .SelectMany(d => d.EnumerateFiles("*.jsonl"))
                .OrderByDescending(f => f.LastWriteTime);
            foreach (var f in files)
            {
                if (list.Count >= max) break;
                var info = Read(f.FullName);
                if (info == null) continue;
                var (cwd, title) = info.Value;
                if (cwd.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || hidden.Contains(cwd.TrimEnd('\\')) || !Directory.Exists(cwd)) continue;
                string name = Path.GetFileName(cwd.TrimEnd('\\'));
                list.Add(new ChatEntry
                {
                    SessionId = Path.GetFileNameWithoutExtension(f.Name),
                    Title = title,
                    ProjectPath = cwd,
                    ProjectName = string.IsNullOrEmpty(name) ? cwd : name,
                    LastUsed = f.LastWriteTime,
                });
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Chatliste: " + ex.Message);
        }
        return Grouped(list);
    }

    static List<ChatEntry> Grouped(List<ChatEntry> list)
    {
        string last = "";
        foreach (var c in list)
        {
            c.Group = GroupOf(c.LastUsed);
            c.ShowGroup = c.Group != last;
            last = c.Group;
        }
        return list;
    }

    static string GroupOf(DateTime t)
    {
        var days = (DateTime.Today - t.Date).TotalDays;
        if (days < 1) return "HEUTE";
        if (days < 2) return "GESTERN";
        if (days < 7) return "DIESE WOCHE";
        if (days < 31) return "DIESER MONAT";
        return "ÄLTER";
    }

    /// <summary>Ordner und Titel eines Gesprächs. Titel: Claudes eigener (ai-title), sonst die erste Eingabe.
    /// Null, wenn die Datei keinen Ordner oder keine echte Eingabe enthält.</summary>
    static (string Cwd, string Title)? Read(string file)
    {
        lock (Cache)
            if (Cache.TryGetValue(file, out var hit)) return hit;

        string? cwd = null, aiTitle = null, prompt = null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            // Der Titel steht nach rund 30 Zeilen (gemessen: höchstens ~300 KB); weiter wird nie gelesen.
            for (int i = 0; i < 120 && fs.Position < 2_000_000; i++)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                if (line.StartsWith("{\"type\":\"ai-title\""))
                {
                    aiTitle = Field(line, "aiTitle");
                    if (aiTitle != null && cwd != null) break;
                    continue;
                }
                if (cwd != null && prompt != null) continue;
                if (!line.Contains("\"cwd\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (cwd == null && r.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String) cwd = c.GetString();
                    if (prompt == null && r.TryGetProperty("type", out var t) && t.GetString() == "user" && !IsMeta(r)) prompt = PromptText(r);
                }
                catch (JsonException) { }
            }
        }
        catch
        {
            return null;
        }
        if (string.IsNullOrEmpty(cwd)) return null;
        string? title = Clean(aiTitle) ?? Clean(prompt);
        if (title == null) return null;
        var result = (cwd!, title);
        if (aiTitle != null)
            lock (Cache) Cache[file] = result;
        return result;
    }

    static bool IsMeta(JsonElement r) =>
        r.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True
        || r.TryGetProperty("isSidechain", out var s) && s.ValueKind == JsonValueKind.True;

    /// <summary>Getippter Text einer Benutzerzeile – Befehle, Befehlsausgaben und Werkzeugergebnisse zählen nicht.</summary>
    static string? PromptText(JsonElement r)
    {
        if (!r.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return null;
        string? text = null;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString();
        else if (content.ValueKind == JsonValueKind.Array)
            foreach (var part in content.EnumerateArray())
                if (part.TryGetProperty("type", out var pt) && pt.GetString() == "text" && part.TryGetProperty("text", out var tx))
                {
                    text = tx.GetString();
                    break;
                }
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart().StartsWith('<')) return null;
        return text;
    }

    static string? Field(string line, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Eine Zeile, höchstens 90 Zeichen.</summary>
    static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length > 90 ? s[..88] + " …" : s;
    }
}
