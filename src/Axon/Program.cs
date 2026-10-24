using System;
using System.Runtime.CompilerServices;
using Axon.Services;

namespace Axon;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Hook-Modus zuerst und ohne WPF: läuft bei jedem Claude-Ereignis und soll sofort fertig sein.
        if (args.Length > 0 && args[0] == "--hook") return HookClient.Run();
        if (args.Length > 0 && args[0] == "--statusline") return StatusLineClient.Run();
        if (args.Length > 0 && args[0] == "--transcribe") return TranscribeFile(args);
        return RunApp(args);
    }

    /// <summary>Nur für Tests: Axon.exe --transcribe &lt;wav&gt; [small|turbo] – schreibt Text und Dauer nach stdout.</summary>
    static int TranscribeFile(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var model = Dictation.ModelFor(args.Length > 2 ? args[2] : null);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!Dictation.IsDownloaded(model))
            {
                Dictation.Download(model, null, default).GetAwaiter().GetResult();
                Console.WriteLine($"Download: {sw.ElapsedMilliseconds / 1000.0:0.0} s");
                sw.Restart();
            }
            string text = Dictation.Transcribe(VoiceRecorder.ReadWav(args[1]), model).GetAwaiter().GetResult();
            long first = sw.ElapsedMilliseconds;
            sw.Restart();
            Dictation.Transcribe(VoiceRecorder.ReadWav(args[1]), model).GetAwaiter().GetResult();
            Console.WriteLine($"{model.Key}: erster Lauf {first} ms (mit Laden), zweiter {sw.ElapsedMilliseconds} ms");
            Console.WriteLine(text);
            Dictation.Unload();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Fehler: " + ex);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp(string[] args)
    {
        var app = new App(args);
        app.InitializeComponent();
        return app.Run();
    }
}
