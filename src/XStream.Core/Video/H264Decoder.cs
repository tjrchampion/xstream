using System.Runtime.InteropServices;
using System.Diagnostics;
using XStream.Core.Rtp;

namespace XStream.Core.Video;

/// <summary>Nv12 / Yuv420p: planes in system memory. D3D11: still on the GPU (a slice of the decoder's texture array).</summary>
public enum PlaneLayout { Nv12, Yuv420p, D3D11, Unsupported }

/// <summary>A decoded picture in system memory. Owns its FFmpeg frame; dispose when shown or replaced.</summary>
public sealed unsafe class DecodedPicture : IDisposable
{
    private IntPtr _frame;
    public int Width { get; }
    public int Height { get; }
    public PlaneLayout Layout { get; }
    public uint RtpTimestamp { get; }
    public bool IsKeyframe { get; }
    /// <summary>When the last packet of this frame arrived from the network.</summary>
    public double ArrivalMs { get; }
    /// <summary>When decoding finished.</summary>
    public double DecodedMs { get; }

    internal DecodedPicture(IntPtr frame, AccessUnit au, double decodedMs)
    {
        _frame = frame;
        Width = FF.FrameWidth(frame);
        Height = FF.FrameHeight(frame);
        Layout = FF.PixFmtName(FF.FrameFormat(frame)) switch
        {
            "nv12" => PlaneLayout.Nv12,
            "yuv420p" or "yuvj420p" => PlaneLayout.Yuv420p,
            "d3d11" => PlaneLayout.D3D11,
            _ => PlaneLayout.Unsupported,
        };
        RtpTimestamp = au.RtpTimestamp;
        IsKeyframe = au.IsKeyframe;
        ArrivalMs = au.CompletedAtMs;
        DecodedMs = decodedMs;
    }

    public byte* Plane(int i) => FF.FrameData(_frame)[i];
    /// <summary>D3D11 frames: the ID3D11Texture2D (texture array) and the slice holding this picture.</summary>
    public (IntPtr Texture, int Slice) D3D11Surface => ((IntPtr)FF.FrameData(_frame)[0], (int)(nint)FF.FrameData(_frame)[1]);
    public int Pitch(int i) => FF.FrameLinesize(_frame)[i];

    public void Dispose()
    {
        if (_frame != IntPtr.Zero) FF.av_frame_free(ref _frame);
    }
}

/// <summary>
/// Low-latency H.264 decoder on FFmpeg. Uses the GPU where available (D3D11VA on the ROG Ally X,
/// VideoToolbox on Mac) and falls back to software. Configured to never hold frames back:
/// low-delay mode, no frame threading, one output per input.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private IntPtr _ctx, _pkt, _hwFrame, _device;
    private readonly IntPtr _sharedD3D11;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    /// <summary>Clock for <see cref="DecodedPicture.DecodedMs"/>; set it to the network clock so latencies line up.</summary>
    public Func<double> Clock { get; set; }

    public string Backend { get; private set; }
    public bool Hardware { get; private set; }
    /// <summary>Decoded pictures stay on the GPU (shared D3D11 device with the window): no copy to system memory and back.</summary>
    public bool ZeroCopy { get; private set; }
    /// <summary>Set when the hardware decoder produced nothing and we switched to software (see Decode).</summary>
    public string? FallbackReason { get; private set; }
    private readonly IntPtr _codec;
    private int _packetsWithoutPicture;
    public double LastDecodeMs { get; private set; }
    public long Decoded { get; private set; }
    public long Errors { get; private set; }

    public static string Version
    {
        get
        {
            uint v = FF.avcodec_version();
            return $"libavcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
        }
    }

    /// <param name="d3d11Device">The window's ID3D11Device (Windows, XStream's own renderer). When given, the
    /// decoder decodes on that device and hands over GPU textures instead of copying every frame to system memory.</param>
    public H264Decoder(bool preferHardware = true, string? forceDevice = null, IntPtr d3d11Device = default)
    {
        _sharedD3D11 = d3d11Device;
        Clock = () => _sw.Elapsed.TotalMilliseconds;
        FF.av_log_set_level(16); // errors only
        var codec = FF.avcodec_find_decoder_by_name("h264");
        if (codec == IntPtr.Zero) throw new InvalidOperationException("FFmpeg has no H.264 decoder");
        _codec = codec;

        string? hwName = null;
        foreach (var type in preferHardware ? HardwareCandidates(forceDevice) : Array.Empty<string>())
        {
            if (TryOpen(codec, type)) { hwName = type; break; }
            Close();
        }
        if (hwName is null && !TryOpen(codec, null)) throw new InvalidOperationException("Could not open the H.264 decoder");

        Hardware = hwName is not null;
        ZeroCopy = hwName == "d3d11va" && _sharedD3D11 != IntPtr.Zero;
        Backend = Hardware ? $"{hwName} (hardware{(ZeroCopy ? ", zero-copy" : "")})" : "software";
        _pkt = FF.av_packet_alloc();
        _hwFrame = FF.av_frame_alloc();
    }

    private static IEnumerable<string> HardwareCandidates(string? force)
    {
        if (force is not null) { yield return force; yield break; }
        if (OperatingSystem.IsWindows()) { yield return "d3d11va"; yield return "dxva2"; }
        else if (OperatingSystem.IsMacOS()) yield return "videotoolbox";
        else { yield return "vaapi"; yield return "cuda"; }
    }

    private bool TryOpen(IntPtr codec, string? hwType)
    {
        _ctx = FF.avcodec_alloc_context3(codec);
        // Latency settings: output each frame as soon as it's decodable.
        FF.av_opt_set(_ctx, "flags", "+low_delay", 0);
        FF.av_opt_set(_ctx, "flags2", "+fast", 0);
        FF.av_opt_set(_ctx, "thread_type", "slice", 0); // frame threading would add one frame of delay per thread
        FF.av_opt_set(_ctx, "threads", hwType is null ? "auto" : "1", 0);

        if (hwType is not null)
        {
            int type = FF.av_hwdevice_find_type_by_name(hwType);
            if (type <= 0) return false;
            if (hwType == "d3d11va" && _sharedD3D11 != IntPtr.Zero)
            {
                // Decode on the window's device so frames can be drawn straight from the decoder's textures.
                _device = FF.av_hwdevice_ctx_alloc(type);
                if (_device == IntPtr.Zero) return false;
                Marshal.AddRef(_sharedD3D11); // FFmpeg releases its reference when the device context is freed
                *FF.D3D11DeviceSlot(_device) = _sharedD3D11;
                if (FF.av_hwdevice_ctx_init(_device) < 0) return false;
                FF.av_opt_set(_ctx, "extra_hw_frames", "4", 0); // surfaces the window may still be holding
            }
            else if (FF.av_hwdevice_ctx_create(out _device, type, IntPtr.Zero, IntPtr.Zero, 0) < 0) return false;
            int off = FF.HwDeviceCtxOffset(_ctx);
            if (off <= 0) return false;
            *(IntPtr*)((byte*)_ctx + off) = FF.av_buffer_ref(_device);
        }
        return FF.avcodec_open2(_ctx, codec, IntPtr.Zero) >= 0;
    }

    /// <summary>Decode one access unit. Returns the picture it produced, or null (not decodable yet / error).</summary>
    public DecodedPicture? Decode(AccessUnit au)
    {
        double t0 = Clock();
        if (FF.av_new_packet(_pkt, au.Data.Length) < 0) return null;
        fixed (byte* src = au.Data) Buffer.MemoryCopy(src, *FF.PacketData(_pkt), au.Data.Length, au.Data.Length);
        int r = FF.avcodec_send_packet(_ctx, _pkt);
        FF.av_packet_unref(_pkt);
        if (r < 0) { Errors++; return null; }

        DecodedPicture? newest = null;
        while (true)
        {
            r = FF.avcodec_receive_frame(_ctx, _hwFrame);
            if (r < 0) break; // EAGAIN (needs more input) or error
            var output = FF.av_frame_alloc();
            bool ok;
            string fmt = FF.PixFmtName(FF.FrameFormat(_hwFrame));
            if (ZeroCopy && fmt == "d3d11")
            {
                ok = true;
                FF.av_frame_move_ref(output, _hwFrame); // keep it on the GPU; the window copies it to its own texture
            }
            else if (Hardware && fmt is not ("nv12" or "yuv420p" or "yuvj420p"))
                ok = FF.av_hwframe_transfer_data(output, _hwFrame, 0) >= 0; // GPU -> NV12 in system memory
            else
            {
                // Software frame: take ownership by swapping into a fresh frame.
                ok = true;
                var tmp = _hwFrame; _hwFrame = output; output = tmp;
            }
            FF.av_frame_unref(_hwFrame);
            if (!ok) { FF.av_frame_free(ref output); Errors++; continue; }
            newest?.Dispose(); // only the newest picture matters for display
            Decoded++;
            newest = new DecodedPicture(output, au, Clock());
        }
        LastDecodeMs = Clock() - t0;

        // A hardware decoder can open fine and then fail to create its surfaces (odd drivers, remote desktop,
        // Wine): FFmpeg then decodes nothing, forever. After ~1 s of packets with no picture, go software.
        if (newest is null && Hardware && Decoded == 0) { if (++_packetsWithoutPicture >= 60) FallBackToSoftware(); }
        else _packetsWithoutPicture = 0;
        return newest;
    }

    private void FallBackToSoftware()
    {
        string was = Backend;
        Close();
        if (!TryOpen(_codec, null)) throw new InvalidOperationException("Could not open the H.264 decoder");
        Hardware = false; ZeroCopy = false;
        Backend = "software (hardware decoding produced no pictures)";
        FallbackReason = $"{was} produced no pictures; switched to software decoding";
        Errors++; // the pipeline asks the console for a keyframe so the new decoder can start
    }

    /// <summary>Clear decoder state (after a reconnect or a long gap).</summary>
    public void Flush() => FF.avcodec_flush_buffers(_ctx);

    private void Close()
    {
        if (_ctx != IntPtr.Zero) FF.avcodec_free_context(ref _ctx);
        if (_device != IntPtr.Zero) FF.av_buffer_unref(ref _device);
    }

    public void Dispose()
    {
        Close();
        if (_pkt != IntPtr.Zero) FF.av_packet_free(ref _pkt);
        if (_hwFrame != IntPtr.Zero) FF.av_frame_free(ref _hwFrame);
    }
}
