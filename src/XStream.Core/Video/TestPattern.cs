namespace XStream.Core.Video;

/// <summary>
/// Writes a tiny but valid H.264 Baseline stream for tests and `xstream video-test`: IDR frames made of raw I_PCM macroblocks
/// (a solid colour that changes every keyframe) and P frames where every macroblock is skipped.
/// Good enough to drive the real frame assembler, decoder and window without an encoder.
/// </summary>
public static class TestPattern
{
    sealed class Bits
    {
        readonly List<byte> _b = new(); int _cur, _n;
        public void U(int bits, long v) { for (int i = bits - 1; i >= 0; i--) Bit((int)((v >> i) & 1)); }
        public void Bit(int b) { _cur = (_cur << 1) | b; if (++_n == 8) { _b.Add((byte)_cur); _cur = 0; _n = 0; } }
        public void Ue(uint v) { uint x = v + 1; int len = 0; while ((x >> len) > 1) len++; U(len, 0); U(len + 1, x); }
        public void Se(int v) => Ue(v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));
        public void Align() { while (_n != 0) Bit(0); }
        public bool Aligned => _n == 0;
        public void Byte(byte v) => U(8, v);
        public void Trailing() { Bit(1); Align(); }
        public byte[] ToArray() => _b.ToArray();
    }

    static byte[] Nal(int refIdc, int type, Bits rbsp)
    {
        var raw = rbsp.ToArray();
        var o = new List<byte> { 0, 0, 0, 1, (byte)((refIdc << 5) | type) };
        int zeros = 0;
        foreach (var b in raw)
        {
            if (zeros >= 2 && b <= 3) { o.Add(3); zeros = 0; } // emulation prevention
            o.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return o.ToArray();
    }

    /// <summary>Annex-B stream: <paramref name="frames"/> frames, an IDR every <paramref name="gop"/>.</summary>
    public static byte[] Stream(int widthMbs, int heightMbs, int frames, int gop) => Stream(widthMbs, heightMbs, frames, gop, null);

    /// <summary>As above, each macroblock's colour (Y, Cb, Cr) from <paramref name="colour"/>(x, y) instead of one colour per keyframe.</summary>
    public static byte[] Stream(int widthMbs, int heightMbs, int frames, int gop, Func<int, int, (byte Y, byte Cb, byte Cr)>? colour)
    {
        var ms = new MemoryStream();
        var sps = new Bits();
        sps.U(8, 66); sps.U(8, 0xC0); sps.U(8, 30); // baseline, constraint_set0/1, level 3.0
        sps.Ue(0); sps.Ue(0); sps.Ue(2); sps.Ue(1); sps.Bit(0); // sps id, log2_max_frame_num-4, poc type 2, 1 ref, no gaps
        sps.Ue((uint)widthMbs - 1); sps.Ue((uint)heightMbs - 1);
        sps.Bit(1); sps.Bit(1); sps.Bit(0); sps.Bit(0); sps.Trailing(); // frame_mbs_only, direct8x8, no crop, no vui
        var pps = new Bits();
        pps.Ue(0); pps.Ue(0); pps.Bit(0); pps.Bit(0); pps.Ue(0); pps.Ue(0); pps.Ue(0);
        pps.Bit(0); pps.U(2, 0); pps.Se(0); pps.Se(0); pps.Se(0); pps.Bit(1); pps.Bit(0); pps.Bit(0); pps.Trailing();

        int idr = 0, frameNum = 0;
        for (int f = 0; f < frames; f++)
        {
            if (f % gop == 0)
            {
                ms.Write(Nal(3, 7, sps)); ms.Write(Nal(3, 8, pps));
                var s = new Bits();
                s.Ue(0); s.Ue(7); s.Ue(0); s.U(4, 0); s.Ue((uint)(idr++ % 65536)); // first_mb, I, pps, frame_num, idr_pic_id
                s.Bit(0); s.Bit(0); s.Se(0); s.Ue(1);                            // ref marking, qp delta, no deblocking
                byte y = (byte)(40 + (idr * 50) % 180), cb = (byte)(60 + (idr * 30) % 140), cr = (byte)(200 - (idr * 40) % 150);
                for (int mb = 0; mb < widthMbs * heightMbs; mb++)
                {
                    if (colour is not null) (y, cb, cr) = colour(mb % widthMbs, mb / widthMbs);
                    s.Ue(25); s.Align();                                             // I_PCM
                    for (int i = 0; i < 256; i++) s.Byte(y);
                    for (int i = 0; i < 64; i++) s.Byte(cb);
                    for (int i = 0; i < 64; i++) s.Byte(cr);
                }
                s.Trailing();
                ms.Write(Nal(3, 5, s));
                frameNum = 1;
            }
            else
            {
                var s = new Bits();
                s.Ue(0); s.Ue(5); s.Ue(0); s.U(4, frameNum++ % 16);                // first_mb, P, pps, frame_num
                s.Bit(0); s.Bit(0); s.Bit(0); s.Se(0); s.Ue(1);                   // no override, no reorder, no MMCO, qp, no deblock
                s.Ue((uint)(widthMbs * heightMbs));                                // every macroblock skipped
                s.Trailing();
                ms.Write(Nal(2, 1, s));
            }
        }
        return ms.ToArray();
    }

    /// <summary>
    /// 1280x720 colour-blindness check (video-test --pattern colours): three panels, each a big digit on a background
    /// that a deuteranope, protanope and tritanope respectively can't tell from it (the two colours differ only along
    /// that condition's confusion line, so its simulation in <see cref="ColorFilters"/> maps them to about the same
    /// colour). Under each panel, the same two colours as plain swatches. The colour filter should make the digits show.
    /// </summary>
    public static byte[] ColourPatches(int frames = 600, int gop = 60)
    {
        var dark = new[] { 0.02, 0.02, 0.02 };
        return Stream(80, 45, frames, gop, (x, y) =>
        {
            int panel = Math.Min(2, x / 27), px = x - panel * 27; // three panels 26 macroblocks wide, a 1-macroblock gap
            var (a, b) = ConfusablePairs[panel];
            if (px == 26 || y == 0 || y == 44 || y is 34 or 35) return Yuv(dark);
            if (y >= 36) return Yuv(px < 13 ? a : b);                    // swatches
            // The digit: a 5x7 glyph, 4 macroblocks per dot, centred in the panel.
            int gx = (px - 3) / 4, gy = (y - 4) / 4;
            bool on = px >= 3 && gx < 5 && y >= 4 && gy < 7 && Glyphs[panel][gy][gx] == '#';
            return Yuv(on ? a : b);
        });
    }

    /// <summary>
    /// Pairs of colours (linear RGB) a deuteranope, protanope and tritanope can't tell apart: a shared middle colour
    /// plus and minus a step along the direction that condition's simulation matrix (nearly) flattens.
    /// </summary>
    public static readonly (double[] A, double[] B)[] ConfusablePairs =
    [
        Pair([0.35, 0.30, 0.10], [0.922, -0.386, 0.028], 0.30), // deuteranopia: salmon red / green
        Pair([0.35, 0.30, 0.10], [0.990, -0.144, -0.003], 0.30), // protanopia: red / dark green
        Pair([0.30, 0.35, 0.35], [0.131, -0.145, 0.981], 0.30), // tritanopia: lilac blue / olive
    ];

    private static (double[], double[]) Pair(double[] mid, double[] dir, double t) =>
        ([mid[0] + t * dir[0], mid[1] + t * dir[1], mid[2] + t * dir[2]], [mid[0] - t * dir[0], mid[1] - t * dir[1], mid[2] - t * dir[2]]);

    private static readonly string[][] Glyphs =
    [
        ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."], // 6
        [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"], // 2
        [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."], // 9
    ];

    /// <summary>Linear RGB to BT.709 limited-range Y'CbCr bytes (gamma 2.2, as the renderers' colour filter assumes).</summary>
    private static (byte, byte, byte) Yuv(double[] lin)
    {
        double r = Math.Pow(Math.Clamp(lin[0], 0, 1), 1 / 2.2), g = Math.Pow(Math.Clamp(lin[1], 0, 1), 1 / 2.2), b = Math.Pow(Math.Clamp(lin[2], 0, 1), 1 / 2.2);
        double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return ((byte)Math.Round(16 + 219 * y), (byte)Math.Round(128 + 224 * (b - y) / 1.8556), (byte)Math.Round(128 + 224 * (r - y) / 1.5748));
    }
}
