using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Axon.Services;

/// <summary>Ein Fenster der Abo-Limits (5 Stunden oder Woche).</summary>
public sealed class RateWindow
{
    public double UsedPercent { get; init; }
    public DateTime ResetsAt { get; init; }
    /// <summary>Wann Axon diesen Stand zuletzt von Claude bekommen hat.</summary>
    public DateTime Seen { get; init; } = DateTime.Now;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasReset => DateTime.Now >= ResetsAt;
}

/// <summary>Nutzungsdaten, wie Claude Code sie an die Statusline übergibt.</summary>
public sealed class UsageInfo
{
    public string Model { get; init; } = "";
    public double? CostUsd { get; init; }
    public long DurationMs { get; init; }
    public long ApiDurationMs { get; init; }
    public long LinesAdded { get; init; }
    public long LinesRemoved { get; init; }

    public long ContextUsed { get; init; }
    public long ContextSize { get; init; }
    public double? ContextPercent { get; init; }
    public long InputTokens { get; init; }
    public long CacheWrite { get; init; }
    public long CacheRead { get; init; }
    public long OutputTokens { get; init; }
    public bool HasContext { get; init; }

    public RateWindow? FiveHour { get; init; }
    public RateWindow? Week { get; init; }

    public DateTime Received { get; init; } = DateTime.Now;

    static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static UsageInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var ctx = Obj(r, "context_window");
            var cur = Obj(ctx, "current_usage");
            var cost = Obj(r, "cost");
            var rl = Obj(r, "rate_limits");
            long size = Long(ctx, "context_window_size");
            long used = Long(ctx, "total_input_tokens");
            return new UsageInfo
            {
                Model = Str(Obj(r, "model"), "display_name"),
                CostUsd = Dbl(cost, "total_cost_usd"),
                DurationMs = Long(cost, "total_duration_ms"),
                ApiDurationMs = Long(cost, "total_api_duration_ms"),
                LinesAdded = Long(cost, "total_lines_added"),
                LinesRemoved = Long(cost, "total_lines_removed"),
                ContextUsed = used,
                ContextSize = size,
                ContextPercent = Dbl(ctx, "used_percentage") ?? (size > 0 && used > 0 ? used * 100.0 / size : null),
                InputTokens = Long(cur, "input_tokens"),
                CacheWrite = Long(cur, "cache_creation_input_tokens"),
                CacheRead = Long(cur, "cache_read_input_tokens"),
                OutputTokens = Long(cur, "output_tokens"),
                HasContext = cur.ValueKind == JsonValueKind.Object,
                FiveHour = Window(Obj(rl, "five_hour")),
                Week = Window(Obj(rl, "seven_day")),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static RateWindow? Window(JsonElement w)
    {
        var pct = Dbl(w, "used_percentage");
        long reset = Long(w, "resets_at");
        if (pct == null || reset <= 0) return null;
        return new RateWindow { UsedPercent = pct.Value, ResetsAt = DateTimeOffset.FromUnixTimeSeconds(reset).LocalDateTime };
    }

    static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double? Dbl(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? (long)d : 0;

    // ───────────────────────────── Formatierung ─────────────────────────────

    public static string Tokens(long n) => n switch
    {
        < 1000 => n.ToString(De),
        < 1_000_000 => (n / 1000.0).ToString(n < 10_000 ? "0.0" : "0", De) + "k",
        _ => (n / 1_000_000.0).ToString("0.00", De) + " M",
    };

    public static string Percent(double p) => p.ToString(p < 10 ? "0.#" : "0", De) + " %";

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min";
        return $"{(int)t.TotalHours} h {t.Minutes:00} min";
    }

    public static string ResetText(DateTime at)
    {
        var left = at - DateTime.Now;
        if (left <= TimeSpan.Zero) return "bereits zurückgesetzt";
        if (left.TotalHours >= 24)
        {
            int days = (int)Math.Round(left.TotalDays);
            return $"zurückgesetzt {at.ToString("ddd HH:mm", De)} Uhr (in {days} {(days == 1 ? "Tag" : "Tagen")})";
        }
        string inText = left.TotalMinutes < 60 ? $"{Math.Max(1, (int)left.TotalMinutes)} min" : $"{(int)left.TotalHours} h {left.Minutes:00} min";
        return $"zurückgesetzt in {inText} ({at.ToString("HH:mm", De)} Uhr)";
    }

    public static string Money(double usd) => usd.ToString(usd < 10 ? "0.00" : "0.0", De) + " $";
}

/// <summary>
/// Läuft als <c>Axon.exe --statusline</c>, aufgerufen von Claude Code nach jeder Änderung (gebündelt alle 300 ms).
/// Schickt die Daten an das laufende Axon und gibt eine dezente Zeile für das Terminal aus. Lädt kein WPF.
/// </summary>
public static class StatusLineClient
{
    public static int Run()
    {
        string line = "";
        try
        {
            string input;
            using (var stdin = Console.OpenStandardInput())
            using (var r = new StreamReader(stdin, new UTF8Encoding(false)))
                input = r.ReadToEnd();
            string? pipe = Environment.GetEnvironmentVariable("AXON_PIPE");
            string? tab = Environment.GetEnvironmentVariable("AXON_TAB");
            if (!string.IsNullOrEmpty(pipe) && !string.IsNullOrEmpty(tab) && input.Length > 0)
                IpcServer.Send(pipe, "S\t" + tab + "\t" + input, 400);
            line = Compact(UsageInfo.Parse(input));
        }
        catch { }
        try
        {
            using var stdout = Console.OpenStandardOutput();
            var bytes = new UTF8Encoding(false).GetBytes(line + "\n");
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch { }
        return 0;
    }

    static string Compact(UsageInfo? u)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (u?.ContextPercent is { } c) parts.Add("Kontext " + UsageInfo.Percent(c));
        if (u?.FiveHour is { HasReset: false } f) parts.Add("5-h-Limit " + UsageInfo.Percent(f.UsedPercent));
        if (u?.Week is { HasReset: false } w) parts.Add("Woche " + UsageInfo.Percent(w.UsedPercent));
        if (parts.Count == 0) parts.Add("Nutzung: Daten nach der ersten Antwort");
        // dezentes Graugrün, passend zum Axon-Farbschema
        return "\x1b[38;2;111;143;128m" + string.Join(" · ", parts) + "\x1b[0m";
    }
}
