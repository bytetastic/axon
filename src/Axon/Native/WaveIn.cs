using System;
using System.Runtime.InteropServices;

namespace Axon.Native;

/// <summary>Mikrofonaufnahme über die alte, aber überall vorhandene winmm-Schnittstelle (kein Zusatzpaket nötig).</summary>
internal static unsafe class WaveIn
{
    public const uint WAVE_MAPPER = 0xFFFFFFFF;
    public const uint CALLBACK_NULL = 0;
    public const uint WHDR_DONE = 0x1;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    public const uint DRVM_MAPPER_PREFERRED_GET = 0x2015;

    [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll")] public static extern int waveInGetDevCapsW(UIntPtr uDeviceID, byte* pwic, int cbwic);
    [DllImport("winmm.dll")] public static extern int waveInMessage(IntPtr hwi, uint uMsg, out uint dw1, out uint dw2);
    [DllImport("winmm.dll")] public static extern int waveInOpen(out IntPtr hwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] public static extern int waveInPrepareHeader(IntPtr hwi, WAVEHDR* pwh, int cbwh);
    [DllImport("winmm.dll")] public static extern int waveInUnprepareHeader(IntPtr hwi, WAVEHDR* pwh, int cbwh);
    [DllImport("winmm.dll")] public static extern int waveInAddBuffer(IntPtr hwi, WAVEHDR* pwh, int cbwh);
    [DllImport("winmm.dll")] public static extern int waveInStart(IntPtr hwi);
    [DllImport("winmm.dll")] public static extern int waveInStop(IntPtr hwi);
    [DllImport("winmm.dll")] public static extern int waveInReset(IntPtr hwi);
    [DllImport("winmm.dll")] public static extern int waveInClose(IntPtr hwi);
}
