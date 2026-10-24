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
        return RunApp(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp(string[] args)
    {
        var app = new App(args);
        app.InitializeComponent();
        return app.Run();
    }
}
