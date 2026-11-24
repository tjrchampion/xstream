using System.Diagnostics;
using XStream.Core.Rtp;

namespace XStream.Core.Video;

/// <summary>
/// An H.264 decoder for the stream. Every implementation must give each frame back from the same call that
/// received it (no frames held back: each one held adds a frame of delay), and count it if it ever doesn't.
/// </summary>
public abstract class VideoDecoder : IDisposable
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    protected VideoDecoder() => Clock = () => _sw.Elapsed.TotalMilliseconds;

    /// <summary>Clock for <see cref="DecodedPicture.DecodedMs"/>; set it to the network clock so latencies line up.</summary>
    public Func<double> Clock { get; set; }

    public string Backend { get; protected set; } = "";
    public bool Hardware { get; protected set; }
    /// <summary>Decoded pictures stay in GPU memory the renderer reads directly (no copy to system memory and back).</summary>
    public bool ZeroCopy { get; protected set; }
    /// <summary>Set when the decoder had to fall back to something slower (shown to the user once).</summary>
    public string? FallbackReason { get; protected set; }
    public double LastDecodeMs { get; protected set; }
    public long Decoded { get; protected set; }
    public long Errors { get; protected set; }
    /// <summary>Frames that produced no picture in the same call (after the first picture): hidden decoder delay.</summary>
    public long HeldBack { get; protected set; }
    /// <summary>Calls that produced more than one picture (the older ones are dropped).</summary>
    public long Bunched { get; protected set; }
    /// <summary>Time spent decoding (submitting the frame and waiting for the picture), total ms.</summary>
    public double SendMsTotal { get; protected set; }
    public double ReceiveMsTotal { get; protected set; }
    /// <summary>Time spent copying pictures from GPU to system memory, total ms (zero for zero-copy paths).</summary>
    public double TransferMsTotal { get; protected set; }

    /// <summary>Decode one access unit (Annex B). Returns the picture it produced, or null (not decodable yet / error).</summary>
    public abstract DecodedPicture? Decode(AccessUnit au);

    public abstract void Dispose();

    /// <summary>Library/framework version, for the log.</summary>
    public abstract string Version { get; }

    /// <summary>
    /// The lowest-latency decoder for this machine: on macOS our own VideoToolbox decoder (real-time mode, no
    /// copies); elsewhere, or if that fails, FFmpeg (Direct3D 11 on Windows, zero-copy with the window's device).
    /// XSTREAM_DECODER=ffmpeg forces FFmpeg (for comparisons).
    /// </summary>
    public static VideoDecoder Create(bool preferHardware = true, IntPtr d3d11Device = default)
    {
        if (preferHardware && OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("XSTREAM_DECODER") is not "ffmpeg")
        {
            try { return new VideoToolboxDecoder(); }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or TypeInitializationException) { }
        }
        return new H264Decoder(preferHardware, d3d11Device: d3d11Device);
    }
}
