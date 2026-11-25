using System.Buffers.Binary;
using System.Text;

namespace XStream.Core.Video;

/// <summary>One video frame for <see cref="Mp4Writer"/>: Annex B H.264 bytes, its time in ms from the clip's start.</summary>
public readonly record struct ClipFrame(byte[] AnnexB, double AtMs, bool Keyframe);

/// <summary>One Opus packet for <see cref="Mp4Writer"/>, with its time in ms from the clip's start.</summary>
public readonly record struct ClipAudio(byte[] Opus, double AtMs);

/// <summary>
/// Writes a plain MP4 (ISO BMFF) file: one H.264 video track (no B-frames, as the console sends) and an optional
/// Opus audio track (48 kHz stereo, "Opus" sample entry with dOps, per the Opus-in-ISOBMFF spec). Samples go in
/// one mdat, the index (moov) after it. Enough for every common player; no re-encoding, so saving is instant.
/// </summary>
public static class Mp4Writer
{
    private const int VideoScale = 90_000, AudioScale = 48_000, MovieScale = 1000;

    public static void Write(Stream output, IReadOnlyList<ClipFrame> frames, IReadOnlyList<ClipAudio> audio)
    {
        if (frames.Count == 0 || !frames[0].Keyframe) throw new ArgumentException("A clip must start with a keyframe.");
        var (sps, pps) = ParameterSets(frames[0].AnnexB);
        if (sps is null || pps is null) throw new ArgumentException("The first keyframe has no SPS/PPS.");
        var format = H264Format.Parse(sps) ?? throw new ArgumentException("Unreadable SPS.");

        // Sample data: video (length-prefixed NAL units, parameter sets left out: they're in avcC), then audio.
        var videoSamples = frames.Select(f => ToAvcc(f.AnnexB)).ToList();
        long mdatHeader = 16; // 64-bit size form: a 50 Mbps minute is ~375 MB, keep it safe
        long ftypSize = 32;
        long videoAt = ftypSize + mdatHeader;
        long audioAt = videoAt + videoSamples.Sum(s => (long)s.Length);
        long mdatSize = mdatHeader + (audioAt - videoAt) + audio.Sum(a => (long)a.Opus.Length);

        using var w = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        // ftyp
        Box(w, "ftyp", b => { b.Write(Ascii("isom")); Be32(b, 0x200); b.Write(Ascii("isom")); b.Write(Ascii("iso2")); b.Write(Ascii("avc1")); b.Write(Ascii("mp41")); });
        // mdat
        Be32(w, 1); w.Write(Ascii("mdat")); Be64(w, mdatSize);
        foreach (var s in videoSamples) w.Write(s);
        foreach (var a in audio) w.Write(a.Opus);

        // Durations from the timestamps; the last frame lasts as long as the one before it.
        var vDur = Durations(frames.Select(f => f.AtMs).ToList(), VideoScale, 1500);
        var aDur = audio.Select(a => (uint)(OpusSamples(a.Opus) is var n and > 0 ? n : 960)).ToList();
        long videoTicks = vDur.Sum(d => (long)d);
        long audioTicks = aDur.Sum(d => (long)d);
        long movieMs = Math.Max(videoTicks * MovieScale / VideoScale, audioTicks * MovieScale / AudioScale);

        Box(w, "moov", moov =>
        {
            FullBox(moov, "mvhd", 0, 0, b =>
            {
                Be32(b, 0); Be32(b, 0); Be32(b, MovieScale); Be32(b, (uint)movieMs);
                Be32(b, 0x00010000); Be16(b, 0x0100); b.Write(new byte[10]); Matrix(b); b.Write(new byte[24]);
                Be32(b, audio.Count > 0 ? 3u : 2u);
            });
            Track(moov, 1, videoTicks * MovieScale / VideoScale, format.Width, format.Height, video: true, VideoScale, videoTicks, stbl =>
            {
                FullBox(stbl, "stsd", 0, 0, b =>
                {
                    Be32(b, 1);
                    Box(b, "avc1", e =>
                    {
                        e.Write(new byte[6]); Be16(e, 1); e.Write(new byte[16]);
                        Be16(e, (ushort)format.Width); Be16(e, (ushort)format.Height);
                        Be32(e, 0x00480000); Be32(e, 0x00480000); Be32(e, 0); Be16(e, 1);
                        e.Write(new byte[32]); Be16(e, 0x0018); Be16(e, 0xFFFF);
                        Box(e, "avcC", c =>
                        {
                            c.Write((byte)1); c.Write(sps[1]); c.Write(sps[2]); c.Write(sps[3]); c.Write((byte)0xFF);
                            c.Write((byte)0xE1); Be16(c, (ushort)sps.Length); c.Write(sps);
                            c.Write((byte)1); Be16(c, (ushort)pps.Length); c.Write(pps);
                        });
                    });
                });
                Stts(stbl, vDur);
                FullBox(stbl, "stss", 0, 0, b =>
                {
                    var keys = frames.Select((f, i) => (f, i)).Where(x => x.f.Keyframe).Select(x => x.i + 1).ToList();
                    Be32(b, (uint)keys.Count); foreach (var k in keys) Be32(b, (uint)k);
                });
                Chunk(stbl, videoSamples.Select(s => s.Length).ToList(), videoAt);
            });
            if (audio.Count > 0)
                Track(moov, 2, audioTicks * MovieScale / AudioScale, 0, 0, video: false, AudioScale, audioTicks, stbl =>
                {
                    FullBox(stbl, "stsd", 0, 0, b =>
                    {
                        Be32(b, 1);
                        Box(b, "Opus", e =>
                        {
                            e.Write(new byte[6]); Be16(e, 1); e.Write(new byte[8]);
                            Be16(e, 2); Be16(e, 16); Be32(e, 0); Be32(e, (uint)AudioScale << 16);
                            Box(e, "dOps", d => { d.Write((byte)0); d.Write((byte)2); Be16(d, 312); Be32(d, AudioScale); Be16(d, 0); d.Write((byte)0); });
                        });
                    });
                    Stts(stbl, aDur);
                    Chunk(stbl, audio.Select(a => a.Opus.Length).ToList(), audioAt);
                });
        });
    }

    /// <summary>Samples in one Opus packet (from its TOC byte), at 48 kHz.</summary>
    public static int OpusSamples(ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0) return 0;
        int config = packet[0] >> 3;
        int per = config < 12 ? new[] { 480, 960, 1920, 2880 }[config & 3]
            : config < 16 ? new[] { 480, 960 }[config & 1]
            : new[] { 120, 240, 480, 960 }[config & 3];
        int count = (packet[0] & 3) switch { 0 => 1, 1 or 2 => 2, _ => packet.Length > 1 ? packet[1] & 0x3F : 1 };
        return per * count;
    }

    /// <summary>Splits Annex B into NAL units (without start codes).</summary>
    public static List<ReadOnlyMemory<byte>> Nals(byte[] annexB)
    {
        var nals = new List<ReadOnlyMemory<byte>>();
        int i = 0, start = -1;
        while (i + 2 < annexB.Length)
        {
            if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
            {
                if (start >= 0) nals.Add(Trim(annexB, start, i));
                i += 3; start = i;
            }
            else i++;
        }
        if (start >= 0 && start < annexB.Length) nals.Add(annexB.AsMemory(start));
        return nals;

        static ReadOnlyMemory<byte> Trim(byte[] b, int s, int e)
        {
            while (e > s && b[e - 1] == 0) e--; // the 4-byte start code's leading zero belongs to no NAL
            return b.AsMemory(s, e - s);
        }
    }

    private static (byte[]? Sps, byte[]? Pps) ParameterSets(byte[] annexB)
    {
        byte[]? sps = null, pps = null;
        foreach (var n in Nals(annexB))
        {
            if (n.Length == 0) continue;
            int type = n.Span[0] & 0x1F;
            if (type == 7) sps ??= n.ToArray();
            else if (type == 8) pps ??= n.ToArray();
        }
        return (sps, pps);
    }

    private static byte[] ToAvcc(byte[] annexB)
    {
        var nals = Nals(annexB).Where(n => n.Length > 0 && (n.Span[0] & 0x1F) is not (7 or 8 or 9)).ToList();
        var o = new byte[nals.Sum(n => 4 + n.Length)];
        int at = 0;
        foreach (var n in nals)
        {
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(at), (uint)n.Length);
            n.Span.CopyTo(o.AsSpan(at + 4));
            at += 4 + n.Length;
        }
        return o;
    }

    private static List<uint> Durations(List<double> atMs, int scale, uint fallback)
    {
        var d = new List<uint>(atMs.Count);
        for (int i = 0; i < atMs.Count; i++)
        {
            double ms = i + 1 < atMs.Count ? atMs[i + 1] - atMs[i] : (d.Count > 0 ? d[^1] * 1000.0 / scale : fallback * 1000.0 / scale);
            d.Add((uint)Math.Clamp(Math.Round(ms * scale / 1000), 1, scale)); // at least 1 tick, at most a second
        }
        return d;
    }

    private static void Track(BinaryWriter moov, uint id, long movieDuration, int width, int height, bool video, int scale, long ticks,
        Action<BinaryWriter> stbl) => Box(moov, "trak", trak =>
    {
        FullBox(trak, "tkhd", 0, 3, b =>
        {
            Be32(b, 0); Be32(b, 0); Be32(b, id); Be32(b, 0); Be32(b, (uint)movieDuration);
            b.Write(new byte[8]); Be16(b, 0); Be16(b, 0); Be16(b, video ? (ushort)0 : (ushort)0x0100); Be16(b, 0);
            Matrix(b); Be32(b, (uint)width << 16); Be32(b, (uint)height << 16);
        });
        Box(trak, "mdia", mdia =>
        {
            FullBox(mdia, "mdhd", 0, 0, b => { Be32(b, 0); Be32(b, 0); Be32(b, (uint)scale); Be32(b, (uint)ticks); Be16(b, 0x55C4); Be16(b, 0); });
            FullBox(mdia, "hdlr", 0, 0, b =>
            {
                Be32(b, 0); b.Write(Ascii(video ? "vide" : "soun")); b.Write(new byte[12]);
                b.Write(Ascii(video ? "Vexel video" : "Vexel audio")); b.Write((byte)0);
            });
            Box(mdia, "minf", minf =>
            {
                if (video) FullBox(minf, "vmhd", 0, 1, b => b.Write(new byte[8]));
                else FullBox(minf, "smhd", 0, 0, b => b.Write(new byte[4]));
                Box(minf, "dinf", dinf => FullBox(dinf, "dref", 0, 0, b => { Be32(b, 1); FullBox(b, "url ", 0, 1, _ => { }); }));
                Box(minf, "stbl", stbl);
            });
        });
    });

    private static void Stts(BinaryWriter stbl, List<uint> durations) => FullBox(stbl, "stts", 0, 0, b =>
    {
        var runs = new List<(uint count, uint dur)>();
        foreach (var d in durations)
            if (runs.Count > 0 && runs[^1].dur == d) runs[^1] = (runs[^1].count + 1, d);
            else runs.Add((1, d));
        Be32(b, (uint)runs.Count);
        foreach (var (count, dur) in runs) { Be32(b, count); Be32(b, dur); }
    });

    /// <summary>All of a track's samples in one chunk at <paramref name="offset"/> (stsc, stsz, co64).</summary>
    private static void Chunk(BinaryWriter stbl, List<int> sizes, long offset)
    {
        FullBox(stbl, "stsc", 0, 0, b => { Be32(b, 1); Be32(b, 1); Be32(b, (uint)sizes.Count); Be32(b, 1); });
        FullBox(stbl, "stsz", 0, 0, b => { Be32(b, 0); Be32(b, (uint)sizes.Count); foreach (var s in sizes) Be32(b, (uint)s); });
        FullBox(stbl, "co64", 0, 0, b => { Be32(b, 1); Be64(b, offset); });
    }

    private static void Matrix(BinaryWriter b)
    {
        foreach (var v in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }) Be32(b, v);
    }

    private static void Box(BinaryWriter parent, string type, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using (var b = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true)) body(b);
        Be32(parent, (uint)(ms.Length + 8));
        parent.Write(Ascii(type));
        ms.Position = 0;
        ms.CopyTo(parent.BaseStream);
    }

    private static void FullBox(BinaryWriter parent, string type, byte version, uint flags, Action<BinaryWriter> body) =>
        Box(parent, type, b => { Be32(b, ((uint)version << 24) | flags); body(b); });

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
    private static void Be16(BinaryWriter w, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); w.Write(b); }
    private static void Be32(BinaryWriter w, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); w.Write(b); }
    private static void Be64(BinaryWriter w, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); w.Write(b); }
}
