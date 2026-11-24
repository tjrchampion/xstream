namespace XStream.Core.Video;

/// <summary>
/// What the stream says about its own picture, from the H.264 sequence parameter set: size, profile and level, and
/// the colour signalling (range and matrix) the renderer must follow to show it the way the encoder meant.
/// </summary>
public sealed record H264Format(int Width, int Height, int Profile, int Level, bool FullRange, int Matrix, int Primaries, int Transfer)
{
    /// <summary>BT.601 matrix (5, 6) rather than BT.709 (1, and "unspecified" for HD).</summary>
    public bool Bt601 => Matrix is 5 or 6;

    public string ProfileName => Profile switch
    {
        66 => "Baseline", 77 => "Main", 88 => "Extended", 100 => "High", 110 => "High 10", 122 => "High 4:2:2", 244 => "High 4:4:4",
        _ => $"profile {Profile}",
    };

    public override string ToString() =>
        $"{Width}x{Height} H.264 {ProfileName} level {Level / 10.0:0.0}, {(Bt601 ? "BT.601" : Matrix == 1 ? "BT.709" : $"matrix {Matrix} (as BT.709)")}, " +
        $"{(FullRange ? "full" : "limited")} range";

    /// <summary>Finds and parses the SPS in an Annex B access unit (null if it has none or it can't be read).</summary>
    public static H264Format? FromAccessUnit(ReadOnlySpan<byte> au)
    {
        for (int i = 0; i + 3 < au.Length; i++)
        {
            if (au[i] != 0 || au[i + 1] != 0 || au[i + 2] != 1) continue;
            int start = i + 3;
            if ((au[start] & 0x1F) != 7) continue;
            int end = start + 1;
            while (end + 2 < au.Length && !(au[end] == 0 && au[end + 1] == 0 && au[end + 2] is 0 or 1)) end++;
            if (end + 2 >= au.Length) end = au.Length;
            return Parse(au[start..end]);
        }
        return null;
    }

    /// <summary>Parses one SPS NAL unit (starting with its header byte).</summary>
    public static H264Format? Parse(ReadOnlySpan<byte> nal)
    {
        try
        {
            // Remove emulation prevention bytes (00 00 03 -> 00 00).
            var rbsp = new byte[nal.Length];
            int n = 0, zeros = 0;
            for (int i = 1; i < nal.Length; i++)
            {
                byte b = nal[i];
                if (zeros >= 2 && b == 3) { zeros = 0; continue; }
                zeros = b == 0 ? zeros + 1 : 0;
                rbsp[n++] = b;
            }
            var r = new Bits(rbsp, n);
            int profile = r.U(8); r.U(8); int level = r.U(8);
            r.Ue();
            int chroma = 1;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = r.Ue();
                if (chroma == 3) r.U(1);
                r.Ue(); r.Ue(); r.U(1);
                if (r.U(1) == 1)
                    for (int i = 0; i < (chroma == 3 ? 12 : 8); i++)
                        if (r.U(1) == 1) SkipScalingList(ref r, i < 6 ? 16 : 64);
            }
            r.Ue();
            int poc = r.Ue();
            if (poc == 0) r.Ue();
            else if (poc == 1)
            {
                r.U(1); r.Se(); r.Se();
                int cycle = r.Ue();
                for (int i = 0; i < cycle; i++) r.Se();
            }
            r.Ue(); r.U(1);
            int wMbs = r.Ue() + 1, hMaps = r.Ue() + 1;
            int frameMbsOnly = r.U(1);
            if (frameMbsOnly == 0) r.U(1);
            r.U(1);
            int width = wMbs * 16, height = hMaps * 16 * (2 - frameMbsOnly);
            if (r.U(1) == 1)
            {
                int cx = chroma == 0 || chroma == 3 ? 1 : 2, cy = (chroma == 1 ? 2 : 1) * (2 - frameMbsOnly);
                int left = r.Ue(), right = r.Ue(), top = r.Ue(), bottom = r.Ue();
                width -= cx * (left + right);
                height -= cy * (top + bottom);
            }
            bool full = false; int matrix = 2, primaries = 2, transfer = 2;
            if (r.U(1) == 1) // VUI
            {
                if (r.U(1) == 1 && r.U(8) == 255) { r.U(16); r.U(16); }
                if (r.U(1) == 1) r.U(1);
                if (r.U(1) == 1)
                {
                    r.U(3);
                    full = r.U(1) == 1;
                    if (r.U(1) == 1) { primaries = r.U(8); transfer = r.U(8); matrix = r.U(8); }
                }
            }
            return new H264Format(width, height, profile, level, full, matrix, primaries, transfer);
        }
        catch (IndexOutOfRangeException) { return null; }
    }

    private static void SkipScalingList(ref Bits r, int size)
    {
        int last = 8, next = 8;
        for (int j = 0; j < size; j++)
        {
            if (next != 0) next = (last + r.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    private ref struct Bits(byte[] data, int length)
    {
        private int _pos;
        public int U(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, _pos++)
            {
                if (_pos >= length * 8) throw new IndexOutOfRangeException();
                v = (v << 1) | ((data[_pos >> 3] >> (7 - (_pos & 7))) & 1);
            }
            return v;
        }
        public int Ue()
        {
            int zeros = 0;
            while (U(1) == 0) if (++zeros > 31) throw new IndexOutOfRangeException();
            return zeros == 0 ? 0 : (1 << zeros) - 1 + U(zeros);
        }
        public int Se() { int k = Ue(); return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2); }
    }
}
