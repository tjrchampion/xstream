using System.Runtime.Intrinsics;

namespace XStream.Core.Video;

/// <summary>
/// "Clarity boost": a halo-free sharpen on the luma (brightness) plane, applied before the GPU scales the
/// picture up to the display. Same idea as AMD FSR 1's sharpening pass and the clarity filters in browser
/// streaming tools, done on the CPU because SDL's renderer has no custom shaders.
///
/// out = c + k/16 * (4c - up - down - left - right), then clamped to the min/max of those five pixels, so
/// edges get steeper but never overshoot into bright/dark halos and flat areas (where compression noise
/// lives) are barely touched. Chroma is left alone: the eye judges sharpness from luma.
///
/// Runs with 128-bit SIMD (NEON on Apple Silicon, SSE on x86) across row bands in parallel: about 1 ms for
/// a 1080p frame, 1.5 ms at 1440p. Writes to a separate buffer, never into the decoder's frame (that's a reference picture).
/// </summary>
public static unsafe class Sharpen
{
    /// <summary>Strength (k in sixteenths) for levels 0-3.</summary>
    public static int Strength(int level) => level switch { <= 0 => 0, 1 => 3, 2 => 5, _ => 8 };

    public static void Apply(byte* src, int srcPitch, byte* dst, int dstPitch, int width, int height, int level)
    {
        int k = Strength(level);
        if (k == 0 || width < 18 || height < 3)
        {
            for (int y = 0; y < height; y++) Buffer.MemoryCopy(src + y * srcPitch, dst + y * dstPitch, width, width);
            return;
        }
        // Top and bottom rows unchanged.
        Buffer.MemoryCopy(src, dst, width, width);
        Buffer.MemoryCopy(src + (height - 1) * srcPitch, dst + (height - 1) * dstPitch, width, width);

        int rows = height - 2;
        if (Bands.Count == 1 || rows < 64) { Rows(src, srcPitch, dst, dstPitch, width, 1, height - 1, k); return; }
        Bands.Run(src, srcPitch, dst, dstPitch, width, rows, k);
    }

    private static readonly BandWorkers Bands = new(Math.Clamp(Environment.ProcessorCount / 2, 1, 6));

    /// <summary>
    /// Dedicated threads, one per row band (the caller does the first band). Unlike Parallel.For, which has to
    /// get thread-pool threads going for every frame (measured: 1 frame in 20 waited up to 11 ms, enough to miss
    /// the display's refresh), these are always there and start within microseconds.
    /// </summary>
    private sealed class BandWorkers
    {
        public readonly int Count;
        private readonly SemaphoreSlim[] _go;
        private readonly ManualResetEventSlim _done = new(false, spinCount: 2000);
        private int _remaining;
        // The current job (set before the workers are released, read after).
        private byte* _src, _dst;
        private int _sp, _dp, _width, _rows, _k;

        public BandWorkers(int count)
        {
            Count = count;
            _go = new SemaphoreSlim[count];
            for (int i = 1; i < count; i++)
            {
                _go[i] = new SemaphoreSlim(0);
                int band = i;
                new Thread(() => Work(band)) { IsBackground = true, Name = $"vexel-sharpen-{i}", Priority = ThreadPriority.AboveNormal }.Start();
            }
        }

        public void Run(byte* src, int sp, byte* dst, int dp, int width, int rows, int k)
        {
            _src = src; _sp = sp; _dst = dst; _dp = dp; _width = width; _rows = rows; _k = k;
            _done.Reset();
            Volatile.Write(ref _remaining, Count - 1);
            for (int i = 1; i < Count; i++) _go[i].Release();
            Band(0);
            _done.Wait();
        }

        private void Work(int band)
        {
            using var _games = XStream.Core.Native.WindowsStreaming.GameThread();
            while (true)
            {
                _go[band].Wait();
                Band(band);
                if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
            }
        }

        private void Band(int b)
        {
            int y0 = 1 + _rows * b / Count, y1 = 1 + _rows * (b + 1) / Count;
            Rows(_src, _sp, _dst, _dp, _width, y0, y1, _k);
        }
    }

    private static void Rows(byte* src, int sp, byte* dst, int dp, int width, int y0, int y1, int k)
    {
        var kv = Vector128.Create((short)k);
        var zero = Vector128<short>.Zero;
        var max = Vector128.Create((short)255);
        for (int y = y0; y < y1; y++)
        {
            byte* row = src + y * sp, up = row - sp, down = row + sp, o = dst + y * dp;
            o[0] = row[0];
            int x = 1;
            for (; x + 16 < width; x += 16)
            {
                var c = Vector128.Load(row + x);
                var l = Vector128.Load(row + x - 1);
                var r = Vector128.Load(row + x + 1);
                var u = Vector128.Load(up + x);
                var dn = Vector128.Load(down + x);
                var lo = Pixel(Lo(c), Lo(l), Lo(r), Lo(u), Lo(dn), kv, zero, max);
                var hi = Pixel(Hi(c), Hi(l), Hi(r), Hi(u), Hi(dn), kv, zero, max);
                Vector128.Narrow(lo.AsUInt16(), hi.AsUInt16()).Store(o + x);
            }
            for (; x < width - 1; x++) o[x] = Scalar(row[x], row[x - 1], row[x + 1], up[x], down[x], k);
            o[width - 1] = row[width - 1];
        }
    }

    private static Vector128<short> Lo(Vector128<byte> v) => Vector128.WidenLower(v).AsInt16();
    private static Vector128<short> Hi(Vector128<byte> v) => Vector128.WidenUpper(v).AsInt16();

    private static Vector128<short> Pixel(Vector128<short> c, Vector128<short> l, Vector128<short> r, Vector128<short> u,
        Vector128<short> d, Vector128<short> k, Vector128<short> zero, Vector128<short> max)
    {
        var lap = (c << 2) - (l + r + u + d);                  // -1020..1020
        var outv = c + Vector128.ShiftRightArithmetic(lap * k, 4); // |lap*k| <= 8160 fits in short
        var mn = Vector128.Min(Vector128.Min(Vector128.Min(l, r), Vector128.Min(u, d)), c);
        var mx = Vector128.Max(Vector128.Max(Vector128.Max(l, r), Vector128.Max(u, d)), c);
        outv = Vector128.Min(Vector128.Max(outv, mn), mx);
        return Vector128.Min(Vector128.Max(outv, zero), max);
    }

    /// <summary>Reference version of one pixel (used for the right-hand edge and in tests).</summary>
    public static byte Scalar(int c, int l, int r, int u, int d, int k)
    {
        int v = c + ((4 * c - l - r - u - d) * k >> 4);
        int mn = Math.Min(Math.Min(Math.Min(l, r), Math.Min(u, d)), c);
        int mx = Math.Max(Math.Max(Math.Max(l, r), Math.Max(u, d)), c);
        return (byte)Math.Clamp(v, mn, mx);
    }
}
