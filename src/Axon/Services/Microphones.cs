using System;
using System.Collections.Generic;
using System.Linq;
using Axon.Native;
using Microsoft.Win32;

namespace Axon.Services;

public sealed record Microphone(uint Id, string Name);

/// <summary>
/// Aufnahmegeräte. Geräte, die den Systemton mitschneiden („Stereomix“ u. Ä.), werden nie benutzt –
/// auch dann nicht, wenn jemand sie in Windows zum Standard gemacht hat.
/// </summary>
public static unsafe class Microphones
{
    static readonly string[] LoopbackNames = { "stereomix", "stereo mix", "wave out mix", "what u hear", "was sie hören", "loopback", "monitor of" };

    public static bool IsLoopback(string name) => LoopbackNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Alle echten Mikrofone, mit vollem Namen (winmm kürzt auf 31 Zeichen, die Registry kennt den ganzen).</summary>
    public static List<Microphone> List()
    {
        var full = EndpointNames();
        var result = new List<Microphone>();
        uint n = WaveIn.waveInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            string? caps = CapsName(i);
            if (caps == null) continue;
            string name = full.FirstOrDefault(f => f.StartsWith(caps, StringComparison.Ordinal)) ?? caps;
            if (!IsLoopback(name)) result.Add(new Microphone(i, name));
        }
        return result;
    }

    /// <summary>Das in Windows eingestellte Standard-Aufnahmegerät (oder null, wenn Windows es nicht verrät).</summary>
    public static Microphone? Default()
    {
        // HWAVEIN-Wert für WAVE_MAPPER ist ((UINT)-1), also nullerweitert – nicht -1 als 64-bit-Zeiger
        if (WaveIn.waveInMessage(new IntPtr((long)WaveIn.WAVE_MAPPER), WaveIn.DRVM_MAPPER_PREFERRED_GET, out uint id, out _) != 0) return null;
        var all = List();
        return all.FirstOrDefault(m => m.Id == id) ?? (CapsName(id) is { } caps ? new Microphone(id, caps) : null);
    }

    /// <summary>
    /// Welches Gerät aufnehmen soll: das gewählte, wenn es da ist, sonst der Windows-Standard.
    /// <paramref name="note"/> erklärt, wenn etwas anders kam als eingestellt.
    /// </summary>
    public static Microphone? Resolve(string? wanted, out string? note)
    {
        note = null;
        if (!string.IsNullOrEmpty(wanted))
        {
            var hit = List().FirstOrDefault(m => m.Name == wanted);
            if (hit != null) return hit;
            note = $"„{wanted}“ nicht gefunden – nehme das Standard-Mikrofon";
        }
        var def = Default();
        if (def != null && IsLoopback(def.Name))
        {
            note = $"Windows-Standard ist „{def.Name}“ – das nimmt den PC-Ton auf, nicht das Mikrofon. Bitte in den Einstellungen ein Mikrofon wählen.";
            return null;
        }
        return def ?? List().FirstOrDefault();
    }

    static string? CapsName(uint id)
    {
        byte* caps = stackalloc byte[80]; // WAVEINCAPSW
        if (WaveIn.waveInGetDevCapsW((UIntPtr)id, caps, 80) != 0) return null;
        return new string((char*)(caps + 8)).TrimEnd();
    }

    /// <summary>„Beschreibung (Schnittstelle)“ aller aktiven Aufnahme-Endpunkte – so baut auch Windows den Namen.</summary>
    static List<string> EndpointNames()
    {
        var names = new List<string>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
            if (root == null) return names;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var dev = root.OpenSubKey(sub);
                if (dev?.GetValue("DeviceState") is not int state || state != 1) continue; // 1 = aktiv
                using var props = dev.OpenSubKey("Properties");
                if (props?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") is not string desc) continue;
                names.Add(props.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") is string iface ? $"{desc} ({iface})" : desc);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("Mikrofonnamen nicht lesbar: " + ex.Message);
        }
        return names;
    }
}
