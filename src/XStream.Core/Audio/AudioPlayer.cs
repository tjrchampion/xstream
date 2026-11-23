using System.Runtime.InteropServices;
using XStream.Core.Native;
using XStream.Core.Video;

namespace XStream.Core.Audio;

/// <summary>
/// Game audio: Opus packets from the console -> FFmpeg's Opus decoder -> SDL audio output.
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
    private const int SampleFmtFlt = 3, SampleFmtFltp = 8;

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
    private IntPtr _ctx, _pkt, _frame;
    private float[] _mix = new float[SampleRate / 50 * Channels * 3]; // room for 60 ms
    private int _lastSeq = -1;
    private bool _disposed;

    /// <summary>Output queue the player aims for. Lower = less lag, higher = fewer crackles on a jittery link.</summary>
    public double TargetMs { get; set; } = 40;
    /// <summary>0..1, applied while converting.</summary>
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public long PacketsDecoded { get; private set; }
    public long PacketsDropped { get; private set; }
    /// <summary>Times the queue grew too long and was trimmed to stay in sync.</summary>
    public long Resyncs { get; private set; }
    public string DeviceInfo { get; }

    public AudioPlayer()
    {
        var codec = FF.avcodec_find_decoder_by_name("opus");
        if (codec == IntPtr.Zero) codec = FF.avcodec_find_decoder_by_name("libopus");
        if (codec == IntPtr.Zero) throw new InvalidOperationException("FFmpeg has no Opus decoder");
        _ctx = FF.avcodec_alloc_context3(codec);
        FF.av_opt_set(_ctx, "ch_layout", "stereo", 0);
        FF.av_opt_set(_ctx, "sample_rate", SampleRate.ToString(), 0);
        int r = FF.avcodec_open2(_ctx, codec, IntPtr.Zero);
        if (r < 0) { FF.avcodec_free_context(ref _ctx); throw new InvalidOperationException("Opus decoder: " + FF.Error(r)); }
        _pkt = FF.av_packet_alloc();
        _frame = FF.av_frame_alloc();

        if (SDL_InitSubSystem(InitAudio) != 0) { Free(); throw new InvalidOperationException("SDL audio: " + Err()); }
        var want = new AudioSpec { Freq = SampleRate, Format = AudioF32Sys, Channels = Channels, Samples = 256 };
        _dev = SDL_OpenAudioDevice(IntPtr.Zero, 0, ref want, out var have, 0); // 0: SDL converts if the device differs
        if (_dev == 0) { SDL_QuitSubSystem(InitAudio); Free(); throw new InvalidOperationException("No audio output: " + Err()); }
        DeviceInfo = $"{have.Freq / 1000.0:0.#} kHz, {have.Channels} ch, {have.Samples}-sample buffer";
        SDL_PauseAudioDevice(_dev, 0);
    }

    /// <summary>One RTP payload (an Opus packet) with its RTP sequence number.</summary>
    public void Push(byte[] opus, int seq)
    {
        lock (_lock)
        {
            if (_disposed || opus.Length == 0) return;
            if (_lastSeq >= 0)
            {
                int d = (short)(ushort)(seq - _lastSeq);
                if (d <= 0) { PacketsDropped++; return; } // late or duplicate: playing it now would only add lag
            }
            _lastSeq = seq;

            if (FF.av_new_packet(_pkt, opus.Length) < 0) return;
            Marshal.Copy(opus, 0, (IntPtr)(*FF.PacketData(_pkt)), opus.Length);
            int r = FF.avcodec_send_packet(_ctx, _pkt);
            FF.av_packet_unref(_pkt);
            if (r < 0) { PacketsDropped++; return; }
            while (FF.avcodec_receive_frame(_ctx, _frame) >= 0)
            {
                Output();
                FF.av_frame_unref(_frame);
            }
            PacketsDecoded++;
        }
    }

    private void Output()
    {
        int n = FF.FrameSamples(_frame), fmt = FF.FrameFormat(_frame);
        if (n <= 0) return;
        if (_mix.Length < n * Channels) _mix = new float[n * Channels];
        byte** data = FF.FrameData(_frame);
        float gain = Muted ? 0 : Math.Clamp(Volume, 0, 1);
        if (fmt == SampleFmtFltp)
        {
            float* l = (float*)data[0], rch = data[1] != null ? (float*)data[1] : l; // mono: same on both sides
            for (int i = 0; i < n; i++) { _mix[2 * i] = l[i] * gain; _mix[2 * i + 1] = rch[i] * gain; }
        }
        else if (fmt == SampleFmtFlt)
        {
            float* s = (float*)data[0];
            for (int i = 0; i < n * Channels; i++) _mix[i] = s[i] * gain;
        }
        else return; // the native Opus decoder always produces float

        // Keep the queue near the target: if it's grown well past it, drop what's queued and start fresh.
        double queuedMs = SDL_GetQueuedAudioSize(_dev) / (double)(sizeof(float) * Channels) * 1000 / SampleRate;
        if (queuedMs > TargetMs + 60)
        {
            SDL_ClearQueuedAudio(_dev);
            Resyncs++;
        }
        fixed (float* p = _mix) SDL_QueueAudio(_dev, p, (uint)(n * Channels * sizeof(float)));
    }

    /// <summary>Audio waiting to be played, in ms.</summary>
    public double QueuedMs
    {
        get { lock (_lock) return _dev == 0 ? 0 : SDL_GetQueuedAudioSize(_dev) / (double)(sizeof(float) * Channels) * 1000 / SampleRate; }
    }

    private static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    private void Free()
    {
        if (_frame != IntPtr.Zero) FF.av_frame_free(ref _frame);
        if (_pkt != IntPtr.Zero) FF.av_packet_free(ref _pkt);
        if (_ctx != IntPtr.Zero) FF.avcodec_free_context(ref _ctx);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_dev != 0) { SDL_CloseAudioDevice(_dev); _dev = 0; SDL_QuitSubSystem(InitAudio); }
            Free();
        }
    }
}
