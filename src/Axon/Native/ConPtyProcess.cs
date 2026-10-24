using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Axon.Native;

/// <summary>
/// Startet einen Prozess in einer echten Windows-Pseudokonsole (ConPTY).
/// Ausgabe kommt als UTF-8-dekodierter Text über <see cref="Output"/> (auf einem Hintergrund-Thread).
/// Der Prozess und alle Kindprozesse hängen in einem Job-Objekt und sterben mit <see cref="Dispose"/>.
/// </summary>
public sealed class ConPtyProcess : IDisposable
{
    IntPtr _hpc;
    IntPtr _process;
    IntPtr _job;
    FileStream? _input;
    FileStream? _output;
    Thread? _reader;
    readonly object _writeLock = new();
    int _disposed;

    public int ProcessId { get; private set; }
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }

    /// <summary>Dekodierter Text aus der Konsole. Läuft auf dem Lese-Thread.</summary>
    public event Action<string>? Output;
    /// <summary>Prozess ist beendet und die Ausgabe vollständig gelesen.</summary>
    public event Action<int>? Exited;

    /// <param name="extraEnvironment">Zusätzliche Variablen; Wert <c>null</c> entfernt die Variable.</param>
    public void Start(string commandLine, string workingDirectory, IDictionary<string, string?> extraEnvironment, int cols, int rows)
    {
        if (!Win32.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (Eingabe)");
        if (!Win32.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (Ausgabe)");

        int hr = Win32.CreatePseudoConsole(new Win32.COORD((short)cols, (short)rows), inputRead, outputWrite, 0, out _hpc);
        if (hr != 0) throw new Win32Exception(hr, "CreatePseudoConsole");

        // Die Enden, die jetzt der Pseudokonsole gehören, hier schließen – sonst kommt nie EOF.
        inputRead.Dispose();
        outputWrite.Dispose();

        _input = new FileStream(inputWrite, FileAccess.Write, 1, false);
        _output = new FileStream(outputRead, FileAccess.Read, 1, false);

        var attrSize = IntPtr.Zero;
        Win32.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize);
        var attrList = Marshal.AllocHGlobal(attrSize);
        IntPtr env = IntPtr.Zero;
        try
        {
            if (!Win32.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            if (!Win32.UpdateProcThreadAttribute(attrList, 0, (IntPtr)Win32.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var si = new Win32.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Win32.STARTUPINFOEX>();
            // Keine eigenen Std-Handles vererben (sonst landet Ausgabe ggf. am ConPTY vorbei).
            si.StartupInfo.dwFlags = Win32.STARTF_USESTDHANDLES;
            si.lpAttributeList = attrList;

            env = BuildEnvironmentBlock(extraEnvironment);
            var cmd = new StringBuilder(commandLine);
            uint flags = Win32.EXTENDED_STARTUPINFO_PRESENT | Win32.CREATE_UNICODE_ENVIRONMENT | Win32.CREATE_SUSPENDED;
            if (!Win32.CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, env, workingDirectory, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");

            _process = pi.hProcess;
            ProcessId = pi.dwProcessId;

            _job = Win32.CreateJobObjectW(IntPtr.Zero, null);
            if (_job != IntPtr.Zero)
            {
                var info = new Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = Win32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                Win32.SetInformationJobObject(_job, Win32.JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<Win32.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                Win32.AssignProcessToJobObject(_job, _process);
            }
            Win32.ResumeThread(pi.hThread);
            Win32.CloseHandle(pi.hThread);
        }
        finally
        {
            Win32.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
            if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ConPTY-Leser" };
        _reader.Start();

        var waiter = new Thread(WaitLoop) { IsBackground = true, Name = "ConPTY-Warter" };
        waiter.Start();
    }

    static IntPtr BuildEnvironmentBlock(IDictionary<string, string?> extra)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
            vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var kv in extra)
        {
            if (kv.Value == null) vars.Remove(kv.Key);
            else vars[kv.Key] = kv.Value;
        }
        var sb = new StringBuilder();
        foreach (var kv in vars)
        {
            if (kv.Key.Length == 0 || kv.Key.IndexOf('=', 1) >= 0) continue;
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    void ReadLoop()
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var buf = new byte[65536];
        var chars = new char[65536 + 4];
        try
        {
            while (true)
            {
                int n = _output!.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                int c = decoder.GetChars(buf, 0, n, chars, 0, false);
                if (c > 0) Output?.Invoke(new string(chars, 0, c));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    void WaitLoop()
    {
        Win32.WaitForSingleObject(_process, Win32.INFINITE);
        Win32.GetExitCodeProcess(_process, out uint code);
        ExitCode = unchecked((int)code);
        HasExited = true;
        // Pseudokonsole schließen, damit der Leser EOF bekommt; Restausgabe wird noch gelesen.
        ClosePseudoConsoleOnce();
        _reader?.Join(3000);
        Exited?.Invoke(ExitCode);
    }

    int _pcClosed;
    void ClosePseudoConsoleOnce()
    {
        if (Interlocked.Exchange(ref _pcClosed, 1) != 0) return;
        if (_hpc != IntPtr.Zero) Win32.ClosePseudoConsole(_hpc);
    }

    public void Write(string text)
    {
        if (string.IsNullOrEmpty(text) || _input == null || HasExited) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_writeLock)
        {
            try
            {
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    public void Resize(int cols, int rows)
    {
        if (_hpc == IntPtr.Zero || HasExited || _pcClosed != 0) return;
        Win32.ResizePseudoConsole(_hpc, new Win32.COORD((short)Math.Max(2, cols), (short)Math.Max(1, rows)));
    }

    /// <summary>Beendet die Sitzung: Konsole schließen, kurz warten, dann den ganzen Prozessbaum beenden.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var t = new Thread(() =>
        {
            // ClosePseudoConsole kann blockieren, solange Ausgabe ungelesen ist – der Leser läuft weiter.
            ClosePseudoConsoleOnce();
            if (_process != IntPtr.Zero && !HasExited) Win32.WaitForSingleObject(_process, 1500);
            if (_job != IntPtr.Zero) Win32.CloseHandle(_job); // KILL_ON_JOB_CLOSE räumt Kindprozesse ab
            if (_process != IntPtr.Zero && !HasExited) Win32.TerminateProcess(_process, 1);
            try { _input?.Dispose(); } catch { }
            try { _output?.Dispose(); } catch { }
        }) { IsBackground = true, Name = "ConPTY-Schliessen" };
        t.Start();
    }

    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') { sb.Append('\\', backslashes * 2 + 1); backslashes = 0; sb.Append('"'); continue; }
            sb.Append('\\', backslashes); backslashes = 0; sb.Append(c);
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string BuildCommandLine(string exe, IEnumerable<string> args)
        => string.Join(" ", new[] { Quote(exe) }.Concat(args.Select(Quote)));
}
