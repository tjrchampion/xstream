namespace XStream.Core.Audio;

/// <summary>
/// Opus decoding that hides lost packets instead of skipping them (a skipped 20 ms is an audible click/dropout):
///  - one packet lost: rebuilt from the in-band FEC copy the console puts in the next packet (useinbandfec=1 is
///    negotiated in the SDP), which is close to the original;
///  - longer gaps: up to <see cref="MaxConcealed"/> packets are filled by Opus packet-loss concealment (a smooth
///    continuation of the sound), the last one still from FEC; past that it moves on rather than fall behind.
/// Late or duplicate packets are dropped (playing them now would only add lag). Not thread-safe: one caller.
/// </summary>
public sealed class OpusLossDecoder : IDisposable
{
    public const int SampleRate = 48_000, Channels = 2;
    /// <summary>Longest gap (in packets, ~60 ms) that's filled in; longer gaps are skipped to stay in sync.</summary>
    public const int MaxConcealed = 3;
    private const int MaxFrame = 5760; // 120 ms, the largest Opus frame

    private readonly IOpusDecoder _dec = IOpusDecoder.Create(SampleRate, Channels);

    /// <summary>Which Opus implementation decodes (libopus, or the managed fallback).</summary>
    public string DecoderName => _dec.Name;
    private readonly short[] _pcm = new short[MaxFrame * Channels];
    private int _lastSeq = -1;
    private int _lastSamples = 960; // samples per channel in the last good packet (20 ms default)

    public long Decoded { get; private set; }
    /// <summary>Lost packets rebuilt from the next packet's FEC data.</summary>
    public long Recovered { get; private set; }
    /// <summary>Lost packets filled by concealment.</summary>
    public long Concealed { get; private set; }
    /// <summary>Late or duplicate packets ignored.</summary>
    public long Dropped { get; private set; }
    /// <summary>Lost packets neither recovered nor concealed (gap too long).</summary>
    public long Skipped { get; private set; }

    /// <summary>
    /// Decode one RTP payload. <paramref name="output"/> receives interleaved stereo 16-bit PCM and the number of
    /// samples per channel, possibly several times (recovered/concealed audio first, then this packet's).
    /// </summary>
    public void Push(byte[] packet, int seq, Action<short[], int> output)
    {
        if (packet.Length == 0) return;
        if (_lastSeq >= 0)
        {
            int d = (short)(ushort)(seq - _lastSeq);
            if (d <= 0) { Dropped++; return; }
            int lost = d - 1;
            if (lost > 0)
            {
                int conceal = Math.Min(lost - 1, MaxConcealed - 1);
                Skipped += Math.Max(0, lost - 1 - conceal);
                for (int i = 0; i < conceal; i++) Emit(null, _lastSamples, fec: false, output, () => Concealed++);
                // The packet just before this one: rebuilt from this packet's forward error correction.
                Emit(packet, _lastSamples, fec: true, output, () => Recovered++);
            }
        }
        _lastSeq = seq;
        try
        {
            int samples = Concentus.Structs.OpusPacketInfo.GetNumSamples(packet.AsSpan(), SampleRate);
            if (samples > 0) _lastSamples = samples;
        }
        catch { /* malformed: decode below will fail too */ }
        Emit(packet, MaxFrame, fec: false, output, () => Decoded++);
    }

    private void Emit(byte[]? data, int frameSize, bool fec, Action<short[], int> output, Action count)
    {
        int n;
        try { n = _dec.Decode(data, _pcm, frameSize, fec); }
        catch { return; }
        if (n <= 0) return;
        count();
        output(_pcm, n);
    }

    public void Dispose() => _dec.Dispose();
}
