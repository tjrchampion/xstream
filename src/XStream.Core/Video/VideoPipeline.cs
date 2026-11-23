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
    private readonly H264Decoder _decoder;
    private readonly FrameMailbox _mailbox;
    private readonly Func<double> _clock;
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _running;
    private volatile bool _decodeError;
    private readonly double[] _decodeMs = new double[256];
    private int _n;

    public H264Decoder Decoder => _decoder;

    public VideoPipeline(PlayoutBuffer<AccessUnit> source, H264Decoder decoder, FrameMailbox mailbox, Func<double> clock)
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
                long errorsBefore = _decoder.Errors;
                var pic = _decoder.Decode(au);
                _decodeMs[_n++ % _decodeMs.Length] = _decoder.LastDecodeMs;
                if (_decoder.Errors > errorsBefore) _decodeError = true;
                if (pic is not null) _mailbox.Post(pic);
            }
            if (!any) _wake.WaitOne(1); // woken by Notify(), else re-check due times every 1 ms
        }
    }

    public void Dispose()
    {
        _running = false;
        _wake.Set();
        _thread?.Join(500);
        _decoder.Dispose();
    }
}
