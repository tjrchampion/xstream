using System.Runtime.InteropServices;
using Concentus.Enums;
using Concentus.Structs;
using XStream.Core.Native;

namespace XStream.Core.Audio;

/// <summary>
/// Microphone for party and game chat: SDL capture (48 kHz mono) -> Opus (Concentus, pure C#) in 20 ms
/// frames, handed to <see cref="Frame"/> for sending to the console on the WebRTC audio track.
/// The device is only open between <see cref="Start"/> and <see cref="Stop"/>, so the OS's microphone
/// indicator is off whenever chat is off.
/// </summary>
public sealed unsafe class Microphone : IDisposable
{
    private const string Sdl = "SDL2";
    private const uint InitAudio = 0x10;
    private const ushort AudioS16Sys = 0x8010;
    public const int SampleRate = 48_000;
    /// <summary>20 ms: the usual Opus frame for voice.</summary>
    public const int FrameSamples = SampleRate / 50;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioSpec
    {
        public int Freq; public ushort Format; public byte Channels; public byte Silence;
        public ushort Samples; public ushort Padding; public uint Size; public IntPtr Callback; public IntPtr UserData;
    }

    [DllImport(Sdl)] private static extern int SDL_InitSubSystem(uint flags);
    [DllImport(Sdl)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Sdl)] private static extern uint SDL_OpenAudioDevice(IntPtr device, int iscapture, ref AudioSpec desired, out AudioSpec obtained, int allowedChanges);
    [DllImport(Sdl)] private static extern void SDL_CloseAudioDevice(uint dev);
    [DllImport(Sdl)] private static extern void SDL_PauseAudioDevice(uint dev, int pause);
    [DllImport(Sdl)] private static extern uint SDL_DequeueAudio(uint dev, byte* data, uint len);
    [DllImport(Sdl)] private static extern uint SDL_GetQueuedAudioSize(uint dev);
    [DllImport(Sdl)] private static extern IntPtr SDL_GetError();
    [DllImport(Sdl)] private static extern int SDL_GetNumAudioDevices(int iscapture);
    [DllImport(Sdl)] private static extern IntPtr SDL_GetAudioDeviceName(int index, int iscapture);

    /// <summary>The microphones the system lists (names as <see cref="Start"/> takes them).</summary>
    public static IReadOnlyList<string> Devices()
    {
        if (SDL_InitSubSystem(InitAudio) != 0) return [];
        try
        {
            var names = new List<string>();
            int n = SDL_GetNumAudioDevices(1);
            for (int i = 0; i < n; i++)
                if (Marshal.PtrToStringUTF8(SDL_GetAudioDeviceName(i, 1)) is { Length: > 0 } name && !names.Contains(name)) names.Add(name);
            return names;
        }
        finally { SDL_QuitSubSystem(InitAudio); }
    }

    static Microphone() => NativeLibs.Ensure();

    private readonly object _lock = new();
    private uint _dev;
    private Thread? _thread;
    private volatile bool _running;
    private OpusEncoder? _encoder;

    /// <summary>One encoded 20 ms Opus frame. Raised on the microphone thread.</summary>
    public event Action<byte[]>? Frame;
    public bool IsOn => _running;
    public string DeviceInfo { get; private set; } = "off";
    /// <summary>Which microphone is open ("System default" when none was named or it's gone).</summary>
    public string Name { get; private set; } = "";
    /// <summary>Input level of the last frame, 0..1 (for a "you're talking" indicator).</summary>
    public float Level { get; private set; }
    public long FramesSent { get; private set; }

    /// <summary>Open the microphone (by name; null/"" or one that's gone = the system default) and start sending. Throws if there's no microphone.</summary>
    public void Start(string? device = null)
    {
        lock (_lock)
        {
            if (_running) return;
            if (SDL_InitSubSystem(InitAudio) != 0) throw new InvalidOperationException("SDL audio: " + Err());
            var want = new AudioSpec { Freq = SampleRate, Format = AudioS16Sys, Channels = 1, Samples = 480 };
            if (device is { Length: > 0 })
            {
                var name = Marshal.StringToCoTaskMemUTF8(device);
                try { _dev = SDL_OpenAudioDevice(name, 1, ref want, out _, 0); } finally { Marshal.FreeCoTaskMem(name); }
                Name = device;
            }
            if (_dev == 0) { _dev = SDL_OpenAudioDevice(IntPtr.Zero, 1, ref want, out _, 0); Name = "System default"; } // 0: SDL converts to what we asked for
            var have = want;
            if (_dev == 0) { SDL_QuitSubSystem(InitAudio); throw new InvalidOperationException("No microphone: " + Err()); }
            DeviceInfo = $"{Name}, {have.Freq / 1000.0:0.#} kHz mono";
            _encoder = new OpusEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP)
            {
                Bitrate = 32_000,
                UseInbandFEC = true,
                PacketLossPercent = 5,
                SignalType = OpusSignal.OPUS_SIGNAL_VOICE,
            };
            _running = true;
            SDL_PauseAudioDevice(_dev, 0);
            _thread = new Thread(Loop) { IsBackground = true, Name = "xstream-mic", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
    }

    public void Stop()
    {
        Thread? t;
        lock (_lock)
        {
            if (!_running) return;
            _running = false;
            t = _thread;
            _thread = null;
        }
        t?.Join(500);
        lock (_lock)
        {
            if (_dev != 0) { SDL_CloseAudioDevice(_dev); _dev = 0; SDL_QuitSubSystem(InitAudio); }
            DeviceInfo = "off";
            Level = 0;
        }
    }

    private void Loop()
    {
        const int bytesPerFrame = FrameSamples * 2;
        var pcm = new short[FrameSamples];
        var opus = new byte[1275];
        while (_running)
        {
            uint dev = _dev;
            if (dev == 0 || SDL_GetQueuedAudioSize(dev) < bytesPerFrame) { Thread.Sleep(4); continue; }
            // If we fell behind (a stall), drop old audio rather than send it late.
            while (SDL_GetQueuedAudioSize(dev) >= bytesPerFrame * 6)
                fixed (short* p = pcm) SDL_DequeueAudio(dev, (byte*)p, bytesPerFrame);
            fixed (short* p = pcm)
                if (SDL_DequeueAudio(dev, (byte*)p, bytesPerFrame) < bytesPerFrame) continue;

            int peak = 0;
            foreach (var s in pcm) peak = Math.Max(peak, Math.Abs((int)s));
            Level = peak / 32768f;
            int n;
            try { n = _encoder!.Encode(pcm, 0, FrameSamples, opus, 0, opus.Length); }
            catch { continue; }
            if (n <= 0) continue;
            FramesSent++;
            Frame?.Invoke(opus.AsSpan(0, n).ToArray());
        }
    }

    private static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    public void Dispose() => Stop();
}
