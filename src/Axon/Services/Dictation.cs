using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Axon.Services;

public sealed record VoiceModel(string Key, string Label, string File, long Size, string Sha256)
{
    public string SizeText => $"{Size / 1_000_000} MB";
}

/// <summary>
/// Spracheingabe mit Whisper – komplett lokal, nichts verlässt den Rechner (außer dem einmaligen Modell-Download).
/// Das Modell bleibt nach dem Diktieren ein paar Minuten geladen und wird dann wieder freigegeben.
/// </summary>
public static class Dictation
{
    public static readonly VoiceModel[] Models =
    {
        new("small", "Schnell", "ggml-small-q5_1.bin", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb"),
        new("turbo", "Genau", "ggml-large-v3-turbo-q5_0.bin", 574_041_195, "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"),
    };

    const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";
    static readonly TimeSpan KeepLoaded = TimeSpan.FromMinutes(3);

    // Hilft Whisper bei Wörtern, die beim Arbeiten mit Claude Code ständig vorkommen.
    const string Prompt = "Claude, Axon, Commit, Push, Branch, Repository, PowerShell, Terminal.";

    public static VoiceModel ModelFor(string? key) => Models.FirstOrDefault(m => m.Key == key) ?? Models[0];

    /// <summary>Modelle sind groß und gehören nicht ins servergespiegelte Roaming-Profil → LocalAppData. Für Tests umlenkbar.</summary>
    public static string ModelDir
    {
        get
        {
            string dir = Environment.GetEnvironmentVariable("AXON_VOICE_MODELS") is { Length: > 0 } m ? m
                : Environment.GetEnvironmentVariable("AXON_DATA") is { Length: > 0 } ? Path.Combine(AppPaths.DataDir, "models")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Axon", "models");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PathOf(VoiceModel m) => Path.Combine(ModelDir, m.File);
    public static bool IsDownloaded(VoiceModel m) => new FileInfo(PathOf(m)) is { Exists: true } f && f.Length == m.Size;

    // ───────────── Download ─────────────

    /// <summary>
    /// Lädt das Modell von Hugging Face. Bewusst nur über IPv4: Hugging Face liefert über Amazon CloudFront aus,
    /// und IPv6 dorthin bricht bei manchen Anschlüssen mitten im TLS-Aufbau ab.
    /// </summary>
    public static async Task Download(VoiceModel m, IProgress<double>? progress, CancellationToken ct)
    {
        string target = PathOf(m), part = target + ".part";
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, token) =>
            {
                var addrs = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, token);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addrs, ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Axon/" + AppPaths.Version);
        using var resp = await http.GetAsync(BaseUrl + m.File, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.AppendData(buf, 0, n);
                done += n;
                progress?.Report(Math.Min(1, done / (double)m.Size));
            }
        }
        string hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        if (done != m.Size || hash != m.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("Modell-Download beschädigt (Prüfsumme stimmt nicht)");
        }
        File.Move(part, target, true);
        AppPaths.Log($"Sprachmodell geladen: {m.File}");
    }

    // ───────────── Erkennung ─────────────

    static readonly object _gate = new();
    static WhisperFactory? _factory;
    static string? _factoryFile;
    static Timer? _unload;
    static int _busy;

    /// <summary>Erkennt deutschen Text. Leere Rückgabe heißt: nichts Verwertbares gesprochen.</summary>
    public static Task<string> Transcribe(float[] samples, VoiceModel m, CancellationToken ct = default) => Task.Run(async () =>
    {
        var speech = TrimSilence(samples);
        if (speech == null) return "";

        var sb = new StringBuilder();
        lock (_gate)
        {
            _busy++;
            _unload?.Dispose();
            _unload = null;
        }
        try
        {
            WhisperFactory factory;
            lock (_gate)
            {
                if (_factory == null || _factoryFile != m.File)
                {
                    LocateNativeLibrary();
                    _factory?.Dispose();
                    _factory = null;
                    _factory = WhisperFactory.FromPath(PathOf(m));
                    _factoryFile = m.File;
                }
                factory = _factory;
            }
            var builder = factory.CreateBuilder()
                .WithLanguage("de")
                .WithPrompt(Prompt)
                .WithThreads(Threads)
                .WithNoSpeechThreshold(0.6f);
            // Whisper rechnet sonst immer ein volles 30-s-Fenster (1500 Schritte) – bei kurzen Sätzen fast nur Leerlauf.
            int ctx = AudioContext(speech.Length);
            if (ctx < 1500) builder = builder.WithAudioContextSize(ctx);
            await using var processor = builder.Build();
            await foreach (var seg in processor.ProcessAsync(speech, ct))
                sb.Append(seg.Text);
        }
        finally
        {
            lock (_gate)
            {
                _busy--;
                _unload?.Dispose();
                _unload = new Timer(_ => Unload(), null, KeepLoaded, Timeout.InfiniteTimeSpan);
            }
        }
        return Clean(sb.ToString());
    }, ct);

    /// <summary>
    /// In der Single-File-Exe entpackt .NET die nativen Whisper-DLLs in einen eigenen Ordner (NATIVE_DLL_SEARCH_DIRECTORIES),
    /// Whisper.net sucht aber nur neben der Exe – also den Pfad selbst vorgeben. Achtung: Whisper.net nimmt davon nur
    /// den Ordner und hängt selbst runtimes\win-x64 an; der Dateiname ist egal.
    /// </summary>
    static void LocateNativeLibrary()
    {
        if (Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath != null) return;
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Prepend(AppContext.BaseDirectory);
        foreach (var dir in dirs)
        {
            if (!File.Exists(Path.Combine(dir, "runtimes", "win-x64", "whisper.dll"))) continue;
            Whisper.net.LibraryLoader.RuntimeOptions.LibraryPath = Path.Combine(dir, "whisper.dll");
            return;
        }
        AppPaths.Log("Whisper-Bibliothek nicht gefunden in: " + string.Join(" | ", dirs));
    }

    /// <summary>Gibt das geladene Modell frei (mehrere hundert MB Arbeitsspeicher).</summary>
    public static void Unload()
    {
        lock (_gate)
        {
            if (_busy > 0) return; // läuft gerade – der Timer danach räumt auf
            _unload?.Dispose();
            _unload = null;
            _factory?.Dispose();
            _factory = null;
            _factoryFile = null;
        }
    }

    /// <summary>50 Schritte je Sekunde Ton plus Reserve; AXON_VOICE_CTX=0 schaltet es für Vergleiche ab.</summary>
    static int AudioContext(int samples)
    {
        if (Environment.GetEnvironmentVariable("AXON_VOICE_CTX") == "0") return 1500;
        double seconds = samples / (double)VoiceRecorder.SampleRate;
        return Math.Min(1500, (int)Math.Ceiling(seconds * 50) + 128);
    }

    /// <summary>Rechen-Threads: whisper.cpp skaliert nur über echte Kerne gut; für Tests per AXON_VOICE_THREADS.</summary>
    static int Threads => int.TryParse(Environment.GetEnvironmentVariable("AXON_VOICE_THREADS"), out int t) && t > 0
        ? t
        : Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    /// <summary>
    /// Schneidet Stille am Anfang und Ende ab. Zu kurze oder zu leise Aufnahmen → null:
    /// Whisper „erfindet“ bei Stille sonst gern Sätze wie „Untertitel im Auftrag des ZDF“.
    /// </summary>
    static float[]? TrimSilence(float[] s)
    {
        const int frame = VoiceRecorder.SampleRate / 50; // 20 ms
        int frames = s.Length / frame;
        if (frames == 0) return null;
        var rms = new double[frames];
        double max = 0;
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * frame; i < (f + 1) * frame; i++) sum += s[i] * s[i];
            rms[f] = Math.Sqrt(sum / frame);
            max = Math.Max(max, rms[f]);
        }
        double threshold = Math.Max(0.006, max * 0.08);
        int first = Array.FindIndex(rms, r => r > threshold), last = Array.FindLastIndex(rms, r => r > threshold);
        if (first < 0 || max < 0.012) return null;
        if (rms.Count(r => r > threshold) < 12) return null; // weniger als ~¼ s Stimme
        int start = Math.Max(0, (first - 15) * frame), end = Math.Min(s.Length, (last + 16) * frame); // 0,3 s Luft
        // Whisper braucht mindestens 1 s Ton – kurze Wörter mit Stille auffüllen.
        var result = new float[Math.Max(end - start, VoiceRecorder.SampleRate * 11 / 10)];
        Array.Copy(s, start, result, 0, end - start);
        return result;
    }

    static readonly Regex Noise = new(@"\[[^\]]*\]|\([^)]*(Musik|Applaus|Lachen|Stille)[^)]*\)|\*[^*]*\*", RegexOptions.IgnoreCase);
    static readonly Regex Phantom = new(@"[^.!?]*(Untertitel|Amara\.org|Vielen Dank fürs Zuschauen|Danke fürs Zuschauen)[^.!?]*[.!?]?", RegexOptions.IgnoreCase);

    static string Clean(string text)
    {
        text = Noise.Replace(text, " ");
        text = Phantom.Replace(text, " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Trim('-', ' ');
    }
}
