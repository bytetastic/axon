using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Axon.Services;

/// <summary>
/// Lokale Kommunikation über eine Named Pipe (kein Netzwerk, kein Port; nur derselbe Benutzer).
/// Nachrichten sind einzelne Zeilen:
///   H \t tabId \t hookJson   – ein Claude-Code-Hook meldet sich
///   S \t tabId \t statusJson – Nutzungsdaten aus Claudes Statusline (Kontext, Abo-Limits, Kosten)
///   O \t ordner              – zweiter Axon-Start: Ordner in neuem Tab öffnen / Fenster nach vorn
///   U \t exePfad             – eine neuere Axon.exe wurde gestartet: Update anbieten
/// </summary>
public sealed class IpcServer : IDisposable
{
    readonly string _name;
    readonly CancellationTokenSource _cts = new();

    public event Action<string, string>? HookReceived;
    public event Action<string, string>? StatusReceived;
    public event Action<string>? OpenRequested;
    /// <summary>Eine neuere Axon.exe wurde gestartet, während dieses Axon läuft (Pfad der neuen Datei).</summary>
    public event Action<string>? UpdateOffered;

    public IpcServer(string name) => _name = name;

    public void Start() => Task.Run(Loop);

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = new NamedPipeServerStream(_name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex)
            {
                AppPaths.Log("Pipe-Server: " + ex.Message);
                await Task.Delay(1000);
                continue;
            }
            try
            {
                await server.WaitForConnectionAsync(_cts.Token);
            }
            catch
            {
                server.Dispose();
                if (_cts.IsCancellationRequested) return;
                continue;
            }
            _ = Task.Run(() => Handle(server));
        }
    }

    async Task Handle(NamedPipeServerStream s)
    {
        try
        {
            using (s)
            using (var reader = new StreamReader(s, new UTF8Encoding(false)))
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                    Dispatch(line);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Pipe-Verbindung: " + ex.Message);
        }
    }

    void Dispatch(string line)
    {
        if (line.StartsWith("H\t") || line.StartsWith("S\t"))
        {
            int t2 = line.IndexOf('\t', 2);
            if (t2 < 0) return;
            if (line[0] == 'H') HookReceived?.Invoke(line[2..t2], line[(t2 + 1)..]);
            else StatusReceived?.Invoke(line[2..t2], line[(t2 + 1)..]);
        }
        else if (line.StartsWith("O\t"))
        {
            OpenRequested?.Invoke(line[2..]);
        }
        else if (line.StartsWith("U\t"))
        {
            UpdateOffered?.Invoke(line[2..]);
        }
    }

    public static bool Send(string pipeName, string message, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeoutMs);
            var bytes = new UTF8Encoding(false).GetBytes(message.Replace("\r", " ").Replace("\n", " ") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _cts.Cancel();
}

/// <summary>Läuft als <c>Axon.exe --hook</c>: Hook-JSON von stdin lesen und an das laufende Axon schicken. Lädt kein WPF.</summary>
public static class HookClient
{
    public static int Run()
    {
        try
        {
            string? pipe = Environment.GetEnvironmentVariable("AXON_PIPE");
            string? tab = Environment.GetEnvironmentVariable("AXON_TAB");
            if (string.IsNullOrEmpty(pipe) || string.IsNullOrEmpty(tab)) return 0;
            string input;
            using (var stdin = Console.OpenStandardInput())
            using (var r = new StreamReader(stdin, new UTF8Encoding(false)))
                input = r.ReadToEnd();
            if (input.Length == 0) return 0;
            IpcServer.Send(pipe, "H\t" + tab + "\t" + input);
        }
        catch { }
        return 0;
    }
}
