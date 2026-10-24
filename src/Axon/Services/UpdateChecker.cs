using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Axon.Services;

/// <summary>Ein Release auf GitHub, das neuer ist als die laufende Version.</summary>
public sealed class UpdateInfo
{
    public Version Version { get; init; } = new(0, 0, 0);
    public string Title { get; init; } = "";
    public string Notes { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
    /// <summary>SHA-256 der Exe, wie GitHub sie für das Release-Asset meldet (falls vorhanden).</summary>
    public string? Sha256 { get; init; }
}

/// <summary>
/// Fragt beim Start bei GitHub nach dem neuesten Release. Übertragen werden dabei nur die üblichen Verbindungsdaten
/// (IP-Adresse, Zeitpunkt) und die Programmkennung „Axon/&lt;Version&gt;“ – keine Inhalte, keine Pfade, kein Konto.
/// Abschaltbar in den Einstellungen.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "bytetastic/axon";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    static HttpClient CreateClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Axon/{AppPaths.Version}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        // Nur für automatische Tests gegen ein privates Repo: ein Token aus der Umgebung des Testprozesses.
        // Die ausgelieferte App setzt diese Variable nie.
        if (Environment.GetEnvironmentVariable("AXON_UPDATE_TOKEN") is { Length: > 0 } token)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>Neueres Release oder null (keins, offline, Repo nicht erreichbar – alles still).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = CreateClient(TimeSpan.FromSeconds(10));
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppPaths.Log($"Update-Prüfung: GitHub antwortet {(int)resp.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
            if (!System.Version.TryParse(AppPaths.Version, out var current) || version <= current) return null;

            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(a.GetProperty("name").GetString(), "Axon.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                bool useApi = Environment.GetEnvironmentVariable("AXON_UPDATE_TOKEN") is { Length: > 0 };
                return new UpdateInfo
                {
                    Version = version,
                    Title = r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                    Notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    PageUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
                    // öffentliches Repo: direkter Download; Testmodus mit Token: über die API
                    DownloadUrl = (useApi ? a.GetProperty("url").GetString() : a.GetProperty("browser_download_url").GetString()) ?? "",
                    Size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                    Sha256 = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
                };
            }
            AppPaths.Log($"Update-Prüfung: Release {tag} ohne Axon.exe");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            AppPaths.Log("Update-Prüfung nicht möglich: " + ex.Message);
            return null;
        }
    }

    /// <summary>Lädt die neue Exe herunter und prüft Größe, Prüfsumme und Version. Liefert den Pfad der fertigen Datei.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(AppPaths.DataDir, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "Axon-*.exe")) { try { File.Delete(old); } catch { } }
        string target = Path.Combine(dir, $"Axon-{info.Version}.exe");
        string part = target + ".part";

        using (var http = CreateClient(TimeSpan.FromMinutes(10)))
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var fi = new FileInfo(part);
        if (info.Size > 0 && fi.Length != info.Size)
            throw new InvalidDataException($"Download unvollständig ({fi.Length} von {info.Size} Bytes).");
        if (info.Sha256 != null)
        {
            string hash;
            await using (var fs = File.OpenRead(part))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (hash != info.Sha256) throw new InvalidDataException("Prüfsumme stimmt nicht – Download verworfen.");
        }
        var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(part);
        if (new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart) != info.Version)
            throw new InvalidDataException("Die heruntergeladene Datei hat nicht die erwartete Version.");

        File.Move(part, target, true);
        return target;
    }
}
