using System.Runtime.InteropServices;
using XStream.Core.Native;
using XStream.Core.Video;

namespace XStream.Core.Audio;

/// <summary>
/// Game audio: Opus packets from the console -> Opus decoder with loss recovery (Concentus) -> SDL audio output.
///
/// Latency is kept low and steady: the output queue is held near <see cref="TargetMs"/>. If it grows past
/// that by more than a frame or two (a network burst, or the sound card's clock running slow), the excess is
/// dropped instead of letting audio drift behind the picture. Late or duplicate packets are skipped.
/// Push is called from the network thread; decoding a 10-20 ms Opus frame takes well under 0.1 ms.
/// </summary>
public sealed unsafe class AudioPlayer : IDisposable
{
    private const string Sdl = "SDL2";
    private const uint InitAudio = 0x10;
    private const ushort AudioF32Sys = 0x8120;
    private const int SampleRate = 48_000, Channels = 2;

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
    [DllImport(Sdl)] private static extern int SDL_QueueAudio(uint dev, float* data, uint len);
    [DllImport(Sdl)] private static extern uint SDL_GetQueuedAudioSize(uint dev);
    [DllImport(Sdl)] private static extern void SDL_ClearQueuedAudio(uint dev);
    [DllImport(Sdl)] private static extern IntPtr SDL_GetError();

    static AudioPlayer() => NativeLibs.Ensure();

    private readonly object _lock = new();
    private uint _dev;
    // Opus decoding with loss recovery (FEC + concealment) instead of skipping lost packets; see OpusLossDecoder.
    private readonly OpusLossDecoder _opus = new();
    private float[] _mix = new float[SampleRate / 50 * Channels * 3]; // room for 60 ms
    private volatile bool _disposed;

    /// <summary>Output queue the player aims for. Lower = less lag, higher = fewer crackles on a jittery link.</summary>
    public double TargetMs { get; set; } = 40;
    /// <summary>0..1, applied while converting.</summary>
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public long PacketsDecoded => _opus.Decoded;
    public long PacketsDropped => _opus.Dropped;
    /// <summary>Lost packets rebuilt from the next packet's FEC data / filled by concealment / too long a gap to fill.</summary>
    public long PacketsRecovered => _opus.Recovered;
    public long PacketsConcealed => _opus.Concealed;
    public long PacketsSkipped => _opus.Skipped;
    /// <summary>Times the queue grew far too long and was cleared to stay in sync.</summary>
    public long Resyncs { get; private set; }
    /// <summary>Lag removed by catching up gently (quiet frames dropped, others played slightly faster), in ms.</summary>
    public double CaughtUpMs { get; private set; }
    public string DeviceInfo { get; }

    public AudioPlayer()
    {
        if (SDL_InitSubSystem(InitAudio) != 0) throw new InvalidOperationException("SDL audio: " + Err());
        var want = new AudioSpec { Freq = SampleRate, Format = AudioF32Sys, Channels = Channels, Samples = 256 };
        _dev = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref want, out var have, 0); // 0: SDL converts if the device differs
        if (_dev == 0) { SDL_QuitSubSystem(InitAudio); throw new InvalidOperationException("No audio output: " + Err()); }
        DeviceInfo = $"{have.Freq / 1000.0:0.#} kHz, {have.Channels} ch, {have.Samples}-sample buffer";
        SDL_PauseAudioDevice(_dev, 0);
    }

    // Decoding runs on its own thread: the network thread only queues the packet, so a 20 ms audio decode never
    // holds up the video packets behind it.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(byte[] data, int seq)> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;

    /// <summary>Which Opus decoder is in use (libopus, or the managed fallback).</summary>
    public string Decoder => _opus.DecoderName;

    /// <summary>One RTP payload (an Opus packet) with its RTP sequence number. Any thread; returns immediately.</summary>
    public void Push(byte[] opus, int seq)
    {
        if (_disposed) return;
        if (_thread is null)
            lock (_lock)
                if (_thread is null)
                {
                    _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "vexel-audio", Priority = ThreadPriority.AboveNormal };
                    _thread.Start();
                }
        if (_queue.Count > 50) _queue.TryDequeue(out _); // a stalled decoder must not grow this without bound
        _queue.Enqueue((opus, seq));
        _wake.Set();
    }

    private void DecodeLoop()
    {
        using var _games = WindowsStreaming.GameThread();
        while (!_disposed)
        {
            _wake.WaitOne(100);
            while (_queue.TryDequeue(out var p))
                lock (_lock)
                {
                    if (_disposed) return;
                    _opus.Push(p.data, p.seq, Output);
                }
        }
    }

    /// <summary>Queue decoded audio (interleaved stereo 16-bit) for playback, with volume applied.</summary>
    private void Output(short[] pcm, int n)
    {
        if (n <= 0) return;
        if (_mix.Length < n * Channels) _mix = new float[n * Channels];
        float gain = Muted ? 0 : Math.Clamp(Volume, 0, 1) / 32768f;
        for (int i = 0; i < n * Channels; i++) _mix[i] = pcm[i] * gain;

        // Keep the queue near the target. Bursty arrivals (start-up, after a hiccup) and clock drift between the console
        // and this device make it grow, and with packets arriving as fast as they play it would never shrink again:
        // sound would run ~50 ms behind the picture. So catch up without audible glitches.
        double queuedMs = SDL_GetQueuedAudioSize(_dev) / (double)(sizeof(float) * Channels) * 1000 / SampleRate;
        double excess = queuedMs - TargetMs;
        if (excess > 60)
        {
            SDL_ClearQueuedAudio(_dev); // far behind (a long stall): start fresh
            Resyncs++;
        }
        else if (excess > AudioCatchUp.SlackMs)
        {
            if (AudioCatchUp.IsQuiet(_mix.AsSpan(0, n * Channels), gain)) { CaughtUpMs += n * 1000.0 / SampleRate; return; }
            int shorter = AudioCatchUp.Shorten(_mix, n, Channels);
            CaughtUpMs += (n - shorter) * 1000.0 / SampleRate;
            n = shorter;
        }
        fixed (float* p = _mix) SDL_QueueAudio(_dev, p, (uint)(n * Channels * sizeof(float)));
    }

    /// <summary>Audio waiting to be played, in ms.</summary>
    public double QueuedMs
    {
        get { lock (_lock) return _dev == 0 ? 0 : SDL_GetQueuedAudioSize(_dev) / (double)(sizeof(float) * Channels) * 1000 / SampleRate; }
    }

    private static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_dev != 0) { SDL_CloseAudioDevice(_dev); _dev = 0; SDL_QuitSubSystem(InitAudio); }
            _opus.Dispose();
        }
        _wake.Set();
    }
}
