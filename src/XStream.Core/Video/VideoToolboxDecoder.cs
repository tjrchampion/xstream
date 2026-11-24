using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XStream.Core.Rtp;

namespace XStream.Core.Video;

/// <summary>
/// Vexel's own H.264 decoder for macOS: Apple's hardware decoder (VideoToolbox) driven directly, set up for the
/// lowest latency rather than playback smoothness:
///  - real-time mode (kVTDecompressionPropertyKey_RealTime), so the media engine keeps its clocks up between
///    frames instead of ramping down and decoding each frame slowly (FFmpeg doesn't expose this);
///  - synchronous: each frame comes back from the call that submitted it, never queued or reordered;
///  - no copies: the frame goes to the hardware straight from one reused native buffer, and the picture stays in
///    the decoder's IOSurface (CPU/GPU shared memory on Apple Silicon), which the renderer reads in place, where
///    FFmpeg copies every picture out to system memory first.
/// Recovers by itself from errors and from the session being invalidated (sleep/wake, display changes).
/// </summary>
public sealed unsafe class VideoToolboxDecoder : VideoDecoder
{
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CM = "/System/Library/Frameworks/CoreMedia.framework/CoreMedia";
    private const string VT = "/System/Library/Frameworks/VideoToolbox.framework/VideoToolbox";
    private const string CV = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

    [StructLayout(LayoutKind.Sequential)]
    private struct CMTime { public long Value; public int Timescale; public uint Flags; public long Epoch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct OutputCallbackRecord
    {
        public delegate* unmanaged<IntPtr, IntPtr, int, uint, IntPtr, CMTime, CMTime, void> Callback;
        public IntPtr RefCon;
    }

    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr alloc, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(CF)] private static extern void CFDictionarySetValue(IntPtr dict, IntPtr key, IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFNumberCreate(IntPtr alloc, nint type, void* value);
    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CM)] private static extern int CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr alloc, nuint count, byte** pointers, nuint* sizes, int nalHeaderLength, out IntPtr formatDesc);
    [DllImport(CM)] private static extern int CMBlockBufferCreateWithMemoryBlock(IntPtr structAlloc, void* memoryBlock, nuint blockLength, IntPtr blockAllocator, IntPtr customBlockSource, nuint offsetToData, nuint dataLength, uint flags, out IntPtr blockBuffer);
    [DllImport(CM)] private static extern int CMSampleBufferCreateReady(IntPtr alloc, IntPtr dataBuffer, IntPtr formatDesc, nint numSamples, nint numSampleTimingEntries, void* timing, nint numSampleSizeEntries, nuint* sizes, out IntPtr sampleBuffer);
    [DllImport(VT)] private static extern int VTDecompressionSessionCreate(IntPtr alloc, IntPtr formatDesc, IntPtr decoderSpec, IntPtr imageAttrs, OutputCallbackRecord* callback, out IntPtr session);
    [DllImport(VT)] private static extern int VTDecompressionSessionDecodeFrame(IntPtr session, IntPtr sampleBuffer, uint flags, IntPtr frameRefCon, out uint infoFlags);
    [DllImport(VT)] private static extern int VTDecompressionSessionWaitForAsynchronousFrames(IntPtr session);
    [DllImport(VT)] private static extern void VTDecompressionSessionInvalidate(IntPtr session);
    [DllImport(VT)] private static extern byte VTDecompressionSessionCanAcceptFormatDescription(IntPtr session, IntPtr formatDesc);
    [DllImport(VT)] private static extern int VTSessionSetProperty(IntPtr session, IntPtr key, IntPtr value);
    [DllImport(CV)] private static extern IntPtr CVPixelBufferRetain(IntPtr pb);
    [DllImport(CV)] private static extern void CVPixelBufferRelease(IntPtr pb);
    [DllImport(CV)] private static extern int CVPixelBufferLockBaseAddress(IntPtr pb, ulong flags);
    [DllImport(CV)] private static extern int CVPixelBufferUnlockBaseAddress(IntPtr pb, ulong flags);
    [DllImport(CV)] private static extern void* CVPixelBufferGetBaseAddressOfPlane(IntPtr pb, nuint plane);
    [DllImport(CV)] private static extern nuint CVPixelBufferGetBytesPerRowOfPlane(IntPtr pb, nuint plane);
    [DllImport(CV)] private static extern nuint CVPixelBufferGetWidth(IntPtr pb);
    [DllImport(CV)] private static extern nuint CVPixelBufferGetHeight(IntPtr pb);

    private const ulong LockReadOnly = 1;
    private const uint DecodeFrame1xRealTime = 1 << 2, InfoAsynchronous = 1 << 0;
    private const int InvalidSession = -12903, BadData = -12909;
    private const uint Nv12VideoRange = 0x34323076; // '420v'
    private const nint CFNumberSInt32 = 3;

    private static readonly IntPtr KeyCallbacks, ValueCallbacks, True, AllocatorNull;
    private static readonly IntPtr RealTimeKey, RequireHwKey, EnableHwKey, PixelFormatKey, IOSurfaceKey, MetalKey;

    static VideoToolboxDecoder()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        IntPtr cf = NativeLibrary.Load(CF), vt = NativeLibrary.Load(VT), cv = NativeLibrary.Load(CV);
        KeyCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"); // the struct itself
        ValueCallbacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        True = Const(cf, "kCFBooleanTrue");
        AllocatorNull = Const(cf, "kCFAllocatorNull");
        RealTimeKey = Const(vt, "kVTDecompressionPropertyKey_RealTime");
        RequireHwKey = Const(vt, "kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder");
        EnableHwKey = Const(vt, "kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder");
        PixelFormatKey = Const(cv, "kCVPixelBufferPixelFormatTypeKey");
        IOSurfaceKey = Const(cv, "kCVPixelBufferIOSurfacePropertiesKey");
        MetalKey = Const(cv, "kCVPixelBufferMetalCompatibilityKey");
    }

    private static IntPtr Const(IntPtr lib, string name) => *(IntPtr*)NativeLibrary.GetExport(lib, name);

    private IntPtr _session, _format;
    private GCHandle _self;
    private byte[]? _sps, _pps;
    // The frame in AVCC form (4-byte lengths instead of start codes), in native memory VideoToolbox reads directly.
    private byte* _avcc;
    private int _avccCapacity;
    // Set by the output callback (synchronously, on the decoding thread).
    private IntPtr _out;
    private int _outStatus, _outputs;

    public override string Version => $"VideoToolbox, macOS {Environment.OSVersion.Version}";

    public VideoToolboxDecoder()
    {
        _self = GCHandle.Alloc(this);
        Hardware = true;
        Backend = "videotoolbox (Vexel's own decoder: real-time, no GPU-to-memory copy)";
        _avccCapacity = 1 << 20;
        _avcc = (byte*)NativeMemory.Alloc((nuint)_avccCapacity);
    }

    public override DecodedPicture? Decode(AccessUnit au)
    {
        double t0 = Clock();
        var data = au.Data;
        int len = ToAvcc(data, out bool parametersChanged);
        au.Release(); // copied (as AVCC) into our native buffer

        if (parametersChanged && !EnsureSession()) { Errors++; LastDecodeMs = Clock() - t0; return null; }
        if (_session == IntPtr.Zero || len == 0) { if (_session == IntPtr.Zero) Errors++; LastDecodeMs = Clock() - t0; return null; }

        var picture = DecodeAvcc(len, au, t0);
        if (picture is null && _outStatus == InvalidSession)
        {
            // Sleep/wake or a GPU change invalidated the session: rebuild it. This frame is lost; the pipeline asks
            // for a keyframe because Errors went up.
            DestroySession();
            EnsureSession();
        }
        LastDecodeMs = Clock() - t0;
        return picture;
    }

    private DecodedPicture? DecodeAvcc(int len, AccessUnit au, double t0)
    {
        double ts = Clock();
        IntPtr block = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            // kCFAllocatorNull: VideoToolbox reads our buffer in place and never frees it (we outlive the call).
            if (CMBlockBufferCreateWithMemoryBlock(IntPtr.Zero, _avcc, (nuint)len, AllocatorNull, IntPtr.Zero, 0, (nuint)len, 0, out block) != 0)
            { Errors++; return null; }
            nuint size = (nuint)len;
            if (CMSampleBufferCreateReady(IntPtr.Zero, block, _format, 1, 0, null, 1, &size, out sample) != 0)
            { Errors++; return null; }

            _out = IntPtr.Zero; _outStatus = 0;
            int before = _outputs;
            int st = VTDecompressionSessionDecodeFrame(_session, sample, DecodeFrame1xRealTime, IntPtr.Zero, out uint info);
            if (st == 0 && _outputs == before && (info & InfoAsynchronous) != 0) VTDecompressionSessionWaitForAsynchronousFrames(_session);
            if (st != 0) _outStatus = st;
            SendMsTotal += Clock() - ts;

            int produced = _outputs - before;
            if (produced == 0) { if (st != 0 || _outStatus != 0) Errors++; else if (Decoded > 0) HeldBack++; return null; }
            if (produced > 1) Bunched++;
            var pb = _out; _out = IntPtr.Zero;
            if (pb == IntPtr.Zero) { Errors++; return null; }
            return Wrap(pb, au);
        }
        finally
        {
            if (sample != IntPtr.Zero) CFRelease(sample);
            if (block != IntPtr.Zero) CFRelease(block);
        }
    }

    /// <summary>Lock the pixel buffer for reading (no copy: the renderer uploads straight from it).</summary>
    private DecodedPicture? Wrap(IntPtr pb, AccessUnit au)
    {
        if (CVPixelBufferLockBaseAddress(pb, LockReadOnly) != 0) { CVPixelBufferRelease(pb); Errors++; return null; }
        Decoded++;
        return new DecodedPicture(pb, (int)CVPixelBufferGetWidth(pb), (int)CVPixelBufferGetHeight(pb),
            (byte*)CVPixelBufferGetBaseAddressOfPlane(pb, 0), (int)CVPixelBufferGetBytesPerRowOfPlane(pb, 0),
            (byte*)CVPixelBufferGetBaseAddressOfPlane(pb, 1), (int)CVPixelBufferGetBytesPerRowOfPlane(pb, 1),
            au, Clock());
    }

    /// <summary>Called by <see cref="DecodedPicture.Dispose"/>.</summary>
    internal static void ReleasePicture(IntPtr pb)
    {
        CVPixelBufferUnlockBaseAddress(pb, LockReadOnly);
        CVPixelBufferRelease(pb);
    }

    [UnmanagedCallersOnly]
    private static void OnOutput(IntPtr refCon, IntPtr frameRefCon, int status, uint infoFlags, IntPtr image, CMTime pts, CMTime duration)
    {
        if (GCHandle.FromIntPtr(refCon).Target is not VideoToolboxDecoder self) return;
        self._outStatus = status;
        if (status != 0 || image == IntPtr.Zero) return;
        if (self._out != IntPtr.Zero) CVPixelBufferRelease(self._out); // only the newest picture matters
        self._out = CVPixelBufferRetain(image);
        self._outputs++;
    }

    /// <summary>
    /// Annex B to AVCC (4-byte big-endian lengths) into the native buffer: slices and SEI go to the decoder; SPS/PPS
    /// are remembered (a change means a new format description); access-unit delimiters are dropped.
    /// </summary>
    private int ToAvcc(ReadOnlySpan<byte> d, out bool parametersChanged)
    {
        parametersChanged = false;
        int need = d.Length + 64;
        if (need > _avccCapacity)
        {
            NativeMemory.Free(_avcc);
            _avccCapacity = Math.Max(need, _avccCapacity * 2);
            _avcc = (byte*)NativeMemory.Alloc((nuint)_avccCapacity);
        }
        int w = 0, i = NextStart(d, 0, out int sc);
        while (i >= 0)
        {
            int start = i + sc;
            int next = NextStart(d, start, out int nsc);
            int end = next < 0 ? d.Length : next;
            while (end > start && d[end - 1] == 0) end--; // trailing zeros belong to the next start code
            var nal = d[start..end];
            if (nal.Length > 0)
            {
                int type = nal[0] & 0x1F;
                if (type == 7) parametersChanged |= Remember(ref _sps, nal);
                else if (type == 8) parametersChanged |= Remember(ref _pps, nal);
                else if (type != 9)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(_avcc + w, 4), (uint)nal.Length);
                    nal.CopyTo(new Span<byte>(_avcc + w + 4, nal.Length));
                    w += 4 + nal.Length;
                }
            }
            i = next; sc = nsc;
        }
        return w;
    }

    private static bool Remember(ref byte[]? slot, ReadOnlySpan<byte> nal)
    {
        if (slot is not null && nal.SequenceEqual(slot)) return false;
        slot = nal.ToArray();
        return true;
    }

    private static int NextStart(ReadOnlySpan<byte> d, int from, out int length)
    {
        for (int i = from; i + 2 < d.Length; i++)
        {
            if (d[i] != 0 || d[i + 1] != 0) continue;
            if (d[i + 2] == 1) { length = 3; return i; }
            if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) { length = 4; return i; }
        }
        length = 0;
        return -1;
    }

    /// <summary>(Re)build the format description from SPS/PPS, and the session if it can't take the new format.</summary>
    private bool EnsureSession()
    {
        if (_sps is null || _pps is null) return false;
        IntPtr format;
        fixed (byte* sps = _sps)
        fixed (byte* pps = _pps)
        {
            byte** sets = stackalloc byte*[2] { sps, pps };
            nuint* sizes = stackalloc nuint[2] { (nuint)_sps.Length, (nuint)_pps.Length };
            if (CMVideoFormatDescriptionCreateFromH264ParameterSets(IntPtr.Zero, 2, sets, sizes, 4, out format) != 0) return false;
        }
        if (_session != IntPtr.Zero && VTDecompressionSessionCanAcceptFormatDescription(_session, format) != 0)
        {
            if (_format != IntPtr.Zero) CFRelease(_format);
            _format = format;
            return true;
        }
        DestroySession();
        _format = format;
        return CreateSession();
    }

    private bool CreateSession()
    {
        IntPtr spec = Dict(), attrs = Dict(), surface = Dict();
        uint fmt = Nv12VideoRange;
        IntPtr fmtNum = CFNumberCreate(IntPtr.Zero, CFNumberSInt32, &fmt);
        try
        {
            CFDictionarySetValue(spec, EnableHwKey, True);
            CFDictionarySetValue(spec, RequireHwKey, True);
            CFDictionarySetValue(attrs, PixelFormatKey, fmtNum);
            CFDictionarySetValue(attrs, IOSurfaceKey, surface); // IOSurface-backed: shareable with Metal, no copies
            CFDictionarySetValue(attrs, MetalKey, True);
            var record = new OutputCallbackRecord { Callback = &OnOutput, RefCon = GCHandle.ToIntPtr(_self) };
            int st = VTDecompressionSessionCreate(IntPtr.Zero, _format, spec, attrs, &record, out _session);
            if (st != 0) { _session = IntPtr.Zero; FallbackReason = $"VideoToolbox session failed ({st})"; return false; }
            int rt = VTSessionSetProperty(_session, RealTimeKey, True);
            if (rt != 0) FallbackReason = $"VideoToolbox didn't accept real-time mode ({rt})";
            return true;
        }
        finally { CFRelease(spec); CFRelease(attrs); CFRelease(surface); CFRelease(fmtNum); }
    }

    private static IntPtr Dict() => CFDictionaryCreateMutable(IntPtr.Zero, 0, KeyCallbacks, ValueCallbacks);

    private void DestroySession()
    {
        if (_session != IntPtr.Zero)
        {
            VTDecompressionSessionWaitForAsynchronousFrames(_session);
            VTDecompressionSessionInvalidate(_session);
            CFRelease(_session);
            _session = IntPtr.Zero;
        }
        if (_out != IntPtr.Zero) { CVPixelBufferRelease(_out); _out = IntPtr.Zero; }
    }

    public override void Dispose()
    {
        DestroySession();
        if (_format != IntPtr.Zero) { CFRelease(_format); _format = IntPtr.Zero; }
        if (_avcc != null) { NativeMemory.Free(_avcc); _avcc = null; }
        if (_self.IsAllocated) _self.Free();
    }
}
