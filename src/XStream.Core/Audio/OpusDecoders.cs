using System.Runtime.InteropServices;
using Concentus.Structs;

namespace XStream.Core.Audio;

/// <summary>An Opus decoder: libopus itself when it's available, otherwise the managed port (Concentus).</summary>
internal interface IOpusDecoder : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Decode into interleaved stereo PCM; returns samples per channel (negative on error). Empty
    /// <paramref name="data"/> means a lost packet (concealment); <paramref name="fec"/> decodes the previous packet
    /// from this one's forward error correction data, with <paramref name="frameSize"/> the lost packet's length.
    /// </summary>
    int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec);

    static IOpusDecoder Create(int sampleRate, int channels)
    {
        if (Environment.GetEnvironmentVariable("XSTREAM_OPUS") is not "managed")
        {
            try { return new NativeOpus(sampleRate, channels); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException) { }
        }
        return new ManagedOpus(sampleRate, channels);
    }
}

/// <summary>
/// libopus through P/Invoke: about 5x faster than the managed port and allocates nothing (Concentus allocates
/// ~29 KB per 20 ms packet, which the garbage collector then has to clean up mid-stream).
/// </summary>
internal sealed unsafe class NativeOpus : IOpusDecoder
{
    [DllImport("opus")] private static extern IntPtr opus_decoder_create(int fs, int channels, out int error);
    [DllImport("opus")] private static extern int opus_decode(IntPtr st, byte* data, int len, short* pcm, int frameSize, int decodeFec);
    [DllImport("opus")] private static extern void opus_decoder_destroy(IntPtr st);
    [DllImport("opus")] private static extern IntPtr opus_get_version_string();

    private IntPtr _st;
    public string Name { get; }

    public NativeOpus(int sampleRate, int channels)
    {
        Native.NativeLibs.Ensure();
        _st = opus_decoder_create(sampleRate, channels, out int err);
        if (_st == IntPtr.Zero || err != 0) throw new InvalidOperationException("opus_decoder_create failed: " + err);
        Name = Marshal.PtrToStringAnsi(opus_get_version_string()) ?? "libopus";
    }

    public int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec)
    {
        fixed (byte* d = data)
        fixed (short* p = pcm)
            return opus_decode(_st, data.IsEmpty ? null : d, data.Length, p, frameSize, fec ? 1 : 0);
    }

    public void Dispose()
    {
        if (_st != IntPtr.Zero) { opus_decoder_destroy(_st); _st = IntPtr.Zero; }
    }
}

/// <summary>The managed Opus port, used when libopus isn't available.</summary>
internal sealed class ManagedOpus(int sampleRate, int channels) : IOpusDecoder
{
#pragma warning disable CS0618 // the array API is the one with forward error correction decoding
    private readonly OpusDecoder _dec = new(sampleRate, channels);
    private byte[] _in = new byte[1500];
    private short[] _out = new short[5760 * channels];
    public string Name => "Concentus (managed)";

    public int Decode(ReadOnlySpan<byte> data, Span<short> pcm, int frameSize, bool fec)
    {
        if (_in.Length < data.Length) _in = new byte[data.Length];
        if (_out.Length < pcm.Length) _out = new short[pcm.Length];
        data.CopyTo(_in);
        int n = _dec.Decode(data.IsEmpty ? null : _in, 0, data.Length, _out, 0, frameSize, fec);
        if (n > 0) _out.AsSpan(0, Math.Min(pcm.Length, n * channels)).CopyTo(pcm);
        return n;
    }
#pragma warning restore CS0618
    public void Dispose() { }
}
