using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Axon.Services;

public sealed class ProjectEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTime LastUsed { get; init; }
    public bool HasClaudeHistory { get; init; }

    public string ShortPath
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + Path[home.Length..] : Path;
        }
    }

    public string LastUsedText => Ago(LastUsed);

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "gerade eben";
        if (d.TotalMinutes < 60) return $"vor {(int)d.TotalMinutes} Min.";
        if (d.TotalHours < 24) return $"vor {(int)d.TotalHours} Std.";
        if (d.TotalDays < 2) return "gestern";
        if (d.TotalDays < 7) return $"vor {(int)d.TotalDays} Tagen";
        return t.ToString("dd.MM.yyyy");
    }
}

/// <summary>Zuletzt genutzte Projekte: aus Axons eigener Liste und aus Claude Codes Sitzungsdateien (~\.claude\projects).</summary>
public static class ProjectHistory
{
    public static List<ProjectEntry> Load(IEnumerable<RecentFolder> axonRecent, IReadOnlyDictionary<string, DateTime> hidden, int max = 14)
    {
        if (DemoContent.Enabled) return DemoContent.Projects();
        var hiddenAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in hidden) hiddenAt[kv.Key.TrimEnd('\\')] = kv.Value;
        var map = new Dictionary<string, ProjectEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in axonRecent)
            if (Directory.Exists(r.Path))
                map[r.Path] = Entry(r.Path, r.LastUsed, false);

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        try
        {
            if (Directory.Exists(root))
            {
                foreach (var dir in new DirectoryInfo(root).EnumerateDirectories())
                {
                    var newest = dir.EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
                    if (newest == null) continue;
                    string? cwd = ReadCwd(newest.FullName);
                    if (cwd == null || !Directory.Exists(cwd)) continue;
                    if (map.TryGetValue(cwd, out var existing))
                    {
                        if (newest.LastWriteTime > existing.LastUsed) map[cwd] = Entry(cwd, newest.LastWriteTime, true);
                        else map[cwd] = Entry(cwd, existing.LastUsed, true);
                    }
                    else map[cwd] = Entry(cwd, newest.LastWriteTime, true);
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Projektliste: " + ex.Message);
        }

        // Temporäre Ordner (Scratchpads, Tests) gehören nicht in die Liste.
        string temp = System.IO.Path.GetTempPath().TrimEnd('\\');
        return map.Values
            .Where(e => !e.Path.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            // ausgeblendete Projekte bleiben weg, bis sie in Axon wieder geöffnet werden (AppSettings.TouchRecent)
            .Where(e => !hiddenAt.ContainsKey(e.Path.TrimEnd('\\')))
            .OrderByDescending(e => e.LastUsed)
            .Take(max)
            .ToList();
    }

    static ProjectEntry Entry(string path, DateTime t, bool claude)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path;
        return new ProjectEntry { Path = path, Name = name, LastUsed = t, HasClaudeHistory = claude };
    }

    static string? ReadCwd(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            for (int i = 0; i < 40; i++)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                if (!line.Contains("\"cwd\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                        return cwd.GetString();
                }
                catch (JsonException) { }
            }
        }
        catch { }
        return null;
    }
}
