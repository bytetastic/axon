using System;
using System.Collections.Generic;

namespace Axon.Services;

/// <summary>
/// Demo-Modus (<c>AXON_DEMO=1</c>) für Screenshots: erfundene Beispielprojekte und eine nachgestellte Sitzung,
/// ohne Claude zu starten und ohne echte Ordner, Pfade oder Kontodaten zu zeigen.
/// </summary>
public static class DemoContent
{
    public static bool Enabled => Environment.GetEnvironmentVariable("AXON_DEMO") == "1";

    public static readonly string[] Tabs = { @"D:\Projekte\aurora-web", @"D:\Projekte\data-pipeline", @"D:\Projekte\notizen" };

    public static List<ProjectEntry> Projects() => new()
    {
        new ProjectEntry { Path = @"D:\Projekte\aurora-web", Name = "aurora-web", LastUsed = DateTime.Now.AddMinutes(-2), HasClaudeHistory = true },
        new ProjectEntry { Path = @"D:\Projekte\data-pipeline", Name = "data-pipeline", LastUsed = DateTime.Now.AddMinutes(-48), HasClaudeHistory = true },
        new ProjectEntry { Path = @"D:\Projekte\notizen", Name = "notizen", LastUsed = DateTime.Now.AddHours(-5), HasClaudeHistory = true },
        new ProjectEntry { Path = @"D:\Projekte\portfolio", Name = "portfolio", LastUsed = DateTime.Now.AddDays(-1), HasClaudeHistory = true },
        new ProjectEntry { Path = @"D:\Projekte\wetter-bot", Name = "wetter-bot", LastUsed = DateTime.Now.AddDays(-3), HasClaudeHistory = false },
    };

    public static List<ChatEntry> Chats()
    {
        // feste Ids, damit sich Demo-Chats anheften lassen (Favoriten) – Nummer = Position in der Liste
        int n = 0;
        ChatEntry C(string title, string project, DateTime t) =>
            new() { SessionId = "demo-" + ++n, Title = title, ProjectPath = @"D:\Projekte\" + project, ProjectName = project, LastUsed = t };
        return new()
        {
            C("Dunkelmodus-Schalter in den Einstellungen", "aurora-web", DateTime.Now.AddMinutes(-2)),
            C("CSV-Import robuster machen", "data-pipeline", DateTime.Now.AddMinutes(-48)),
            C("Wochenplan als Tabelle zusammenfassen", "notizen", DateTime.Now.AddHours(-5)),
            C("Ladezeit der Startseite halbieren", "aurora-web", DateTime.Now.AddDays(-1)),
            C("Projektgalerie mit Filtern", "portfolio", DateTime.Now.AddDays(-1).AddHours(-3)),
            C("Fehlende Tests für den Parser", "data-pipeline", DateTime.Now.AddDays(-3)),
            C("Telegram-Befehl /morgen einbauen", "wetter-bot", DateTime.Now.AddDays(-4)),
            C("Abhängigkeiten aktualisieren", "aurora-web", DateTime.Now.AddDays(-12)),
        };
    }

    public static UsageInfo Usage() => new()
    {
        Model = "Opus 5.5",
        CostUsd = 0.84,
        DurationMs = 1_380_000,
        ApiDurationMs = 312_000,
        LinesAdded = 86,
        LinesRemoved = 14,
        ContextUsed = 61_400,
        ContextSize = 200_000,
        ContextPercent = 30.7,
        InputTokens = 5_200,
        CacheRead = 49_800,
        CacheWrite = 6_400,
        OutputTokens = 1_900,
        HasContext = true,
        FiveHour = new RateWindow { UsedPercent = 23, ResetsAt = DateTime.Now.AddHours(3).AddMinutes(12) },
        Week = new RateWindow { UsedPercent = 41, ResetsAt = DateTime.Now.AddDays(3) },
    };

    const string E = "\x1b";
    const string Reset = E + "[0m";
    static string Fg(int r, int g, int b) => $"{E}[38;2;{r};{g};{b}m";
    static string Bg(int r, int g, int b) => $"{E}[48;2;{r};{g};{b}m";
    static readonly string Orange = Fg(215, 119, 87), Gray = Fg(153, 153, 153), Dim = Fg(110, 110, 110), White = Fg(230, 230, 230), Green = Fg(78, 186, 101);
    /// <summary>Punkt vor Textantworten – Claudes Themenfarbe „text“ (dunkles Thema), daran erkennt Axon die Antworten.</summary>
    static readonly string TextDot = Fg(255, 255, 255);

    /// <summary>Langer Verlauf mit mehreren Runden (<c>AXON_DEMO_SCENE=answers</c>) – zum Testen von Hervorhebung und Sprüngen.</summary>
    public static string LongTranscript(int cols)
    {
        var rounds = new (string ask, string[] first, (string tool, string arg, string result)[] tools, string[] answer)[]
        {
            ("Warum lädt die Startseite so langsam?",
                new[] { "Ich messe zuerst, was beim Laden passiert." },
                new[] { ("Bash", "npm run build -- --stats", "Bundle 1,8 MB, davon 1,1 MB Diagramm-Bibliothek"), ("Read", "src/pages/Start.tsx", "Read 96 lines") },
                new[] { "Der größte Brocken ist die Diagramm-Bibliothek: Sie wird auf der Startseite geladen,", "obwohl dort gar kein Diagramm zu sehen ist.", "", "Vorschlag:", "  1. Diagramme erst auf der Statistikseite nachladen (dynamischer Import)", "  2. Bilder der Galerie verkleinert ausliefern", "", "Damit sollte sich die Ladezeit etwa halbieren. Soll ich beides umsetzen?" }),
            ("Ja, mach beides",
                new[] { "Ich fange mit dem Nachladen der Diagramme an." },
                new[] { ("Update", "src/pages/Statistik.tsx", "Updated src/pages/Statistik.tsx with 4 additions and 2 removals"), ("Update", "src/components/Galerie.tsx", "Updated src/components/Galerie.tsx with 7 additions and 1 removal"), ("Bash", "npm run build -- --stats", "Bundle 0,7 MB") },
                new[] { "Erledigt. Das Bundle der Startseite ist von 1,8 MB auf 0,7 MB geschrumpft.", "", "  • Diagramme kommen jetzt erst auf der Statistikseite", "  • Die Galerie liefert Bilder in drei Größen aus (srcset)", "", "Gemessen im Produktions-Build: erste Anzeige nach 0,9 s statt 2,1 s." }),
            ("Laufen die Tests noch?",
                Array.Empty<string>(),
                new[] { ("Bash", "npm test", "✓ 48 Tests bestanden (4,2 s)") },
                new[] { "Ja, alle 48 Tests laufen durch." }),
            ("Schreib einen kurzen Eintrag fürs Changelog",
                new[] { "Hier ein Vorschlag im Stil der bisherigen Einträge:" },
                Array.Empty<(string, string, string)>(),
                new[] { "## 2.4.0", "", "- Startseite lädt etwa doppelt so schnell (Diagramme werden erst bei Bedarf geladen)", "- Galerie liefert Bilder passend zur Bildschirmgröße aus", "", "Soll ich ihn in CHANGELOG.md eintragen?" }),
        };
        var lines = new List<string>
        {
            $"{Orange} ▐▛███▜▌{Reset}   {E}[1mClaude Code{E}[22m {Gray}v2.1.316{Reset}",
            $"{Orange}▝▜█████▛▘{Reset}  {Gray}Opus 5.5{Reset}",
            $"{Orange}  ▘▘ ▝▝{Reset}    {Gray}~\\Projekte\\aurora-web{Reset}",
            "",
        };
        string userBg = Bg(55, 55, 55);
        foreach (var (ask, first, tools, answer) in rounds)
        {
            lines.Add($"{userBg}{Gray}>{White} {ask.PadRight(Math.Max(0, Math.Min(cols - 4, 70)))}{Reset}");
            lines.Add("");
            if (first.Length > 0)
            {
                lines.Add($"{TextDot}●{Reset} {first[0]}");
                for (int i = 1; i < first.Length; i++) lines.Add("  " + first[i]);
                lines.Add("");
            }
            foreach (var (tool, arg, result) in tools)
            {
                lines.Add($"{Green}●{Reset} {E}[1m{tool}{E}[22m({arg})");
                lines.Add($"  {Gray}⎿  {result}{Reset}");
                lines.Add("");
            }
            lines.Add($"{TextDot}●{Reset} {answer[0]}");
            for (int i = 1; i < answer.Length; i++) lines.Add(answer[i].Length == 0 ? "" : "  " + answer[i]);
            lines.Add("");
        }
        string rule = Gray + new string('─', Math.Max(20, cols - 2)) + Reset;
        lines.Add(rule);
        lines.Add($"{Gray}>{Reset} ");
        lines.Add(rule);
        lines.Add($"  {Fg(111, 143, 128)}Kontext 38 % · 5-h-Limit 27 % · Woche 43 %{Reset}");
        return string.Join("\r\n", lines) + $"{E}[2A{E}[3G";
    }

    /// <summary>Nachgestellte Sitzung: eine Anfrage, Werkzeugaufrufe, eine Änderung mit Diff, Tests –
    /// bzw. mit <paramref name="attention"/> eine Freigabe-Abfrage.</summary>
    public static string Transcript(int cols, bool attention = false)
    {
        string line = Gray + new string('─', Math.Max(20, cols - 2)) + Reset;
        string addBg = Bg(24, 58, 38), delBg = Bg(70, 28, 32);
        // Diff-Zeilen auf gleiche Breite auffüllen, damit die farbigen Hintergründe bündig sind
        string Diff(string bg, string sign, string code) => $"{bg}{sign}  {code.PadRight(60)}{Reset}";
        var head = new[]
        {
            $"{Orange} ▐▛███▜▌{Reset}   {E}[1mClaude Code{E}[22m {Gray}v2.1.309{Reset}",
            $"{Orange}▝▜█████▛▘{Reset}  {Gray}Opus 5.5{Reset}",
            $"{Orange}  ▘▘ ▝▝{Reset}    {Gray}~\\Projekte\\aurora-web{Reset}",
            "",
        };
        if (attention)
        {
            int w = Math.Min(cols - 4, 84);
            string Box(string s) => $"{Orange}│{Reset} {s}{new string(' ', Math.Max(0, w - 3 - Visible(s)))}{Orange}│{Reset}";
            return string.Join("\r\n", head) + "\r\n" + string.Join("\r\n", new[]
            {
                $"{Gray}>{Reset} Schreib Tests für den neuen Dunkelmodus-Schalter",
                "",
                $"{TextDot}●{Reset} Für die Klick-Tests brauche ich die Testbibliothek für Benutzereingaben.",
                "",
                $"{Orange}╭{new string('─', w - 2)}╮{Reset}",
                Box($"{White}Bash command{Reset}"),
                Box(""),
                Box($"  npm install --save-dev @testing-library/user-event"),
                Box($"  {Gray}Install the user-event testing library{Reset}"),
                Box(""),
                Box("Do you want to proceed?"),
                Box($"{Orange}❯ 1. Yes{Reset}"),
                Box("  2. Yes, and don't ask again for npm install commands"),
                Box("  3. No, and tell Claude what to do differently (esc)"),
                $"{Orange}╰{new string('─', w - 2)}╯{Reset}",
            }) + $"{E}[?25l"; // bei Abfragen zeigt Claude keinen Cursor
        }
        return string.Join("\r\n", head) + "\r\n" + string.Join("\r\n", new[]
        {
            $"{Gray}>{Reset} Füge in den Einstellungen einen Schalter für den Dunkelmodus hinzu und teste ihn",
            "",
            $"{TextDot}●{Reset} Ich schaue mir zuerst an, wie die Einstellungsseite aufgebaut ist.",
            "",
            $"{Green}●{Reset} {White}Read{Reset}(src/settings/SettingsPage.tsx)",
            $"  {Gray}⎿  Read 142 lines{Reset}",
            "",
            $"{Green}●{Reset} {White}Update{Reset}(src/settings/SettingsPage.tsx)",
            $"  {Gray}⎿  Updated src/settings/SettingsPage.tsx with 9 additions and 1 removal{Reset}",
            $"      {Dim}38{Reset}      <Section title=\"Darstellung\">",
            $"      {Dim}39{Reset}  {Diff(delBg, "-", "   <ThemeHint />")}",
            $"      {Dim}39{Reset}  {Diff(addBg, "+", "   <Toggle")}",
            $"      {Dim}40{Reset}  {Diff(addBg, "+", "     label=\"Dunkelmodus\"")}",
            $"      {Dim}41{Reset}  {Diff(addBg, "+", "     checked={theme === \"dark\"}")}",
            $"      {Dim}42{Reset}  {Diff(addBg, "+", "     onChange={on => setTheme(on ? \"dark\" : \"light\")}")}",
            $"      {Dim}43{Reset}  {Diff(addBg, "+", "   />")}",
            "",
            $"{Green}●{Reset} {White}Bash{Reset}(npm test -- settings)",
            $"  {Gray}⎿  ✓ 12 Tests bestanden (1,8 s){Reset}",
            "",
            $"{TextDot}●{Reset} Der Schalter ist eingebaut und speichert die Wahl dauerhaft. Alle 12 Tests laufen durch.",
            $"  Als Nächstes könnte ich den Modus beim ersten Start automatisch an Windows anpassen.",
            "",
            $" {Orange}✻ Denkt nach … {Gray}(24 s · ↓ 1.9k tokens){Reset}",
            "",
            line,
            $"{Gray}>{Reset} ",
            line,
            $"  {Fg(111, 143, 128)}Kontext 31 % · 5-h-Limit 23 % · Woche 41 %{Reset}",
        }) + $"{E}[2A{E}[3G"; // Cursor zurück ins Eingabefeld
    }

    /// <summary>Sichtbare Länge ohne Escape-Sequenzen.</summary>
    static int Visible(string s) => System.Text.RegularExpressions.Regex.Replace(s, "\x1b\\[[0-9;]*m", "").Length;
}
