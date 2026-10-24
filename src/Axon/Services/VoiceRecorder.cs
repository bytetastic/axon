using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Axon.Native;

namespace Axon.Services;

/// <summary>
/// Nimmt vom Standard-Mikrofon auf – direkt im Format, das Whisper braucht (16 kHz, mono, 16 bit).
/// Für Tests liest es per AXON_TEST_VOICE=&lt;wav&gt; stattdessen eine Datei in Echtzeit ein.
/// </summary>
public sealed unsafe class VoiceRecorder : IDisposable
{
    public const int SampleRate = 16000;
    const int BufferCount = 6;
    const int BufferSamples = SampleRate / 20; // 50 ms je Puffer – so oft bekommt die Klangkurve einen neuen Pegel
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(10);

    readonly List<float> _samples = new();
    readonly object _lock = new();
    IntPtr _hwi;
    WaveIn.WAVEHDR* _headers;
    Thread? _thread;
    volatile bool _stopping;
    float[]? _testSource;

    /// <summary>Lautstärke des letzten Puffers, 0 (Stille) bis 1 (laut).</summary>
    public double Level { get; private set; }
    /// <summary>Lautester Wert der ganzen Aufnahme (0 heißt: es kam gar nichts an).</summary>
    public float Peak { get; private set; }
    public double Seconds { get { lock (_lock) return _samples.Count / (double)SampleRate; } }
    public bool IsRecording => _thread != null;

    /// <summary>Wird ausgelöst (im Aufnahme-Thread), wenn die Höchstlänge oder das Ende der Testdatei erreicht ist.</summary>
    public event Action? Ended;

    /// <param name="deviceId">winmm-Gerätenummer aus <see cref="Microphones"/> – bewusst nie WAVE_MAPPER,
    /// damit feststeht, welches Gerät aufnimmt.</param>
    public void Start(uint deviceId)
    {
        if (_thread != null) return;
        lock (_lock) _samples.Clear();
        Peak = 0;
        Level = 0;
        _stopping = false;

        if (Environment.GetEnvironmentVariable("AXON_TEST_VOICE") is { Length: > 0 } wav)
        {
            _testSource = ReadWav(wav);
            _thread = new Thread(TestLoop) { IsBackground = true, Name = "Axon-Testaufnahme" };
            _thread.Start();
            return;
        }

        var fmt = new WaveIn.WAVEFORMATEX
        {
            wFormatTag = 1, // PCM
            nChannels = 1,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 2,
            nAvgBytesPerSec = SampleRate * 2,
        };
        int r = WaveIn.waveInOpen(out _hwi, deviceId, ref fmt, IntPtr.Zero, IntPtr.Zero, WaveIn.CALLBACK_NULL);
        if (r != 0) throw new InvalidOperationException($"Mikrofon lässt sich nicht öffnen (Fehler {r})");

        _headers = (WaveIn.WAVEHDR*)NativeMemory.AllocZeroed((nuint)(BufferCount * sizeof(WaveIn.WAVEHDR)));
        for (int i = 0; i < BufferCount; i++)
        {
            var h = &_headers[i];
            h->lpData = (IntPtr)NativeMemory.AllocZeroed(BufferSamples * 2);
            h->dwBufferLength = BufferSamples * 2;
            WaveIn.waveInPrepareHeader(_hwi, h, sizeof(WaveIn.WAVEHDR));
            WaveIn.waveInAddBuffer(_hwi, h, sizeof(WaveIn.WAVEHDR));
        }
        WaveIn.waveInStart(_hwi);
        _thread = new Thread(MicLoop) { IsBackground = true, Name = "Axon-Mikrofon" };
        _thread.Start();
    }

    /// <summary>Beendet die Aufnahme und gibt alle Samples (−1 … 1) zurück.</summary>
    public float[] Stop()
    {
        if (_thread == null) return Array.Empty<float>();
        _stopping = true;
        if (_thread != Thread.CurrentThread) _thread.Join();
        _thread = null;

        if (_hwi != IntPtr.Zero)
        {
            WaveIn.waveInStop(_hwi);
            WaveIn.waveInReset(_hwi); // gibt halb gefüllte Puffer zurück
            for (int i = 0; i < BufferCount; i++)
            {
                var h = &_headers[i];
                if ((h->dwFlags & WaveIn.WHDR_DONE) != 0 && h->dwBytesRecorded > 0) Consume(h);
                WaveIn.waveInUnprepareHeader(_hwi, h, sizeof(WaveIn.WAVEHDR));
                NativeMemory.Free((void*)h->lpData);
            }
            WaveIn.waveInClose(_hwi);
            NativeMemory.Free(_headers);
            _headers = null;
            _hwi = IntPtr.Zero;
        }
        Level = 0;
        lock (_lock) return _samples.ToArray();
    }

    void MicLoop()
    {
        int next = 0;
        while (!_stopping)
        {
            var h = &_headers[next];
            if ((h->dwFlags & WaveIn.WHDR_DONE) == 0) { Thread.Sleep(15); continue; }
            Consume(h);
            h->dwFlags &= ~WaveIn.WHDR_DONE;
            h->dwBytesRecorded = 0;
            WaveIn.waveInAddBuffer(_hwi, h, sizeof(WaveIn.WAVEHDR));
            next = (next + 1) % BufferCount;
            if (Seconds >= MaxLength.TotalSeconds) { Ended?.Invoke(); return; }
        }
    }

    void Consume(WaveIn.WAVEHDR* h)
    {
        var pcm = new ReadOnlySpan<short>((void*)h->lpData, (int)h->dwBytesRecorded / 2);
        var block = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++) block[i] = pcm[i] / 32768f;
        Append(block);
    }

    void Append(ReadOnlySpan<float> block)
    {
        double sum = 0;
        float peak = Peak;
        foreach (float s in block)
        {
            sum += s * s;
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        Peak = peak;
        double rms = block.Length > 0 ? Math.Sqrt(sum / block.Length) : 0;
        // −60 dB → 0, −12 dB → 1
        Level = rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 60) / 48, 0, 1);
        lock (_lock) _samples.AddRange(block);
    }

    void TestLoop()
    {
        var src = _testSource!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int pos = 0; pos < src.Length && !_stopping; pos += BufferSamples)
        {
            // nach der Uhr takten – Sleep(50) schläft unter Windows eher 62 ms, die Datei liefe sonst zu langsam
            int wait = (int)(pos * 1000L / SampleRate - sw.ElapsedMilliseconds);
            if (wait > 0) Thread.Sleep(wait);
            Append(src.AsSpan(pos, Math.Min(BufferSamples, src.Length - pos)));
        }
        if (!_stopping) Ended?.Invoke();
    }

    /// <summary>Liest eine WAV-Datei (PCM 16 bit, 16 kHz, mono).</summary>
    public static float[] ReadWav(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF") throw new InvalidDataException("keine WAV-Datei");
        br.ReadInt32();
        br.ReadChars(4);
        short channels = 1, bits = 16;
        int rate = SampleRate;
        while (br.BaseStream.Position < br.BaseStream.Length)
        {
            string id = new(br.ReadChars(4));
            int len = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16();
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                br.BaseStream.Seek(len - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (channels != 1 || bits != 16 || rate != SampleRate)
                    throw new InvalidDataException($"WAV muss 16 kHz, mono, 16 bit sein (ist {rate} Hz, {channels} Kanal/Kanäle, {bits} bit)");
                var bytes = br.ReadBytes(len);
                var result = new float[bytes.Length / 2];
                for (int i = 0; i < result.Length; i++) result[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                return result;
            }
            else br.BaseStream.Seek(len + (len & 1), SeekOrigin.Current);
        }
        throw new InvalidDataException("WAV ohne Daten");
    }

    public void Dispose() => Stop();
}
