using XStream.Core.Rtp;
using XStream.Core.Stability;

namespace XStream.Core.Video;

/// <summary>
/// Decode thread: takes access units from the playout buffer the moment they're due, decodes them,
/// and posts the newest picture to the window's mailbox. With the Competitive profile the playout
/// delay is ~0, so frames go network -> decoder -> screen with nothing queued in between.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    private readonly PlayoutBuffer<AccessUnit> _source;
    private readonly VideoDecoder _decoder;
    private readonly FrameMailbox _mailbox;
    private readonly Func<double> _clock;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _decodeError;
    private readonly double[] _decodeMs = new double[256];
    private int _n;

    public VideoDecoder Decoder => _decoder;
    /// <summary>Called on the decode thread for each picture before it goes to the window (testing hooks).</summary>
    public Action<DecodedPicture>? PictureDecoded { get; set; }

    public VideoPipeline(PlayoutBuffer<AccessUnit> source, VideoDecoder decoder, FrameMailbox mailbox, Func<double> clock)
    {
        _source = source;
        _decoder = decoder;
        _mailbox = mailbox;
        _clock = clock;
        _decoder.Clock = clock;
    }

    /// <summary>Call when a frame is enqueued so the decoder starts at once instead of on its next poll.</summary>
    public void Notify() => _wake.Set();

    /// <summary>True once after the decoder rejected a frame (feeds the engine's keyframe recovery).</summary>
    public bool TakeDecodeError() { bool e = _decodeError; _decodeError = false; return e; }

    /// <summary>One line for the session log: pictures, hidden decoder delay, GPU-to-memory copy cost.</summary>
    public string Summary =>
        $"Decoder: {_decoder.Decoded} pictures; {_decoder.HeldBack} frames held back a frame, {_decoder.Bunched} calls gave several; " +
        $"per picture: send {Per(_decoder.SendMsTotal):F2} ms, receive {Per(_decoder.ReceiveMsTotal):F2} ms, GPU-to-memory copy {Per(_decoder.TransferMsTotal):F2} ms; decode median {DecodeP50Ms:F2} ms";

    private double Per(double total) => _decoder.Decoded > 0 ? total / _decoder.Decoded : 0;

    public double DecodeP50Ms
    {
        get { int k = Math.Min(_n, _decodeMs.Length); if (k == 0) return 0; var s = _decodeMs.Take(k).OrderBy(x => x).ToArray(); return s[k / 2]; }
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "xstream-decode", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Loop()
    {
        using var _games = XStream.Core.Native.WindowsStreaming.GameThread(); // decode on time when Windows is busy
        while (_running)
        {
            bool any = false;
            while (_source.TryDequeue(_clock(), out var au))
            {
                any = true;
                if (_dump is not null)
                {
                    // Note when the recording starts on the stream's clock, so stats can be lined up with it.
                    if (_dump.Position == 0) File.WriteAllText(_dump.Name + ".start", _clock().ToString(System.Globalization.CultureInfo.InvariantCulture));
                    _dump.Write(au.Data);
                }
                long errorsBefore = _decoder.Errors;
                var pic = _decoder.Decode(au);
                _decodeMs[_n++ % _decodeMs.Length] = _decoder.LastDecodeMs;
                if (_decoder.Errors > errorsBefore) _decodeError = true;
                if (pic is not null) { PictureDecoded?.Invoke(pic); _mailbox.Post(pic); }
            }
            if (!any) _wake.WaitOne(1); // woken by Notify(), else re-check due times every 1 ms
        }
    }

    // XSTREAM_DUMP_H264=file (testing): record the stream as decoded (Annex B), to benchmark decoders offline.
    private readonly FileStream? _dump = Environment.GetEnvironmentVariable("XSTREAM_DUMP_H264") is { Length: > 0 } path
        ? new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20) : null;

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(500);
        _dump?.Dispose();
        _decoder.Dispose();
    }
}
