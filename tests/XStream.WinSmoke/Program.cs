using XStream.Core.Rtp;
using XStream.Core.Stability;
using XStream.Core.Video;

// xstream-smoke [--renderer sdl|d3d11] [--software] [--seconds N] [--out dir]
// Exit code 0 = frames presented and the screenshot has the test pattern's colour; 1 = failure.
string Arg(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
string renderer = Arg("--renderer", "auto");
double seconds = double.Parse(Arg("--seconds", "3"));
string outDir = Arg("--out", ".");
try
{
    var stream = TestPattern.Stream(80, 45, (int)(seconds * 60) + 60, 30); // 1280x720, keyframe every 0.5 s
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double Now() => sw.Elapsed.TotalMilliseconds;
    var mailbox = new FrameMailbox();
    using var window = new VideoWindow(mailbox, Now, new VideoWindowOptions
    {
        Title = "XStream smoke", Fullscreen = false, Width = 1280, Height = 720, StartHidden = true, // like a real stream
        Renderer = renderer == "auto" ? null : renderer,
    });
    window.Sharpen = int.Parse(Arg("--sharpen", "1"));
    window.FirstPicture += () => Console.WriteLine($"First picture: window shown after {Now():F0} ms");
    // A translucent panel over the video: exercises the overlay texture and blending. (A synthetic overlay, not
    // the stream menu: the menu's text rendering trips Rosetta's x86 emulation under Wine.)
    window.Overlay = new PanelOverlay();
    var buffer = new PlayoutBuffer<AccessUnit> { TargetDelayMs = 0 };
    using var decoder = new H264Decoder(!args.Contains("--software"), d3d11Device: window.D3D11Device);
    using var pipe = new VideoPipeline(buffer, decoder, mailbox, Now);
    Console.WriteLine($"Renderer: {window.RendererName}  Decoder: {decoder.Backend} ({H264Decoder.LibVersion})");
    pipe.Start();
    var packets = H264Packetizer.Packetize(H264Packetizer.AccessUnits(stream)).ToList();
    var depack = new H264Depacketizer();
    int i = 0; double next = Now();
    byte[]? shot = null; int sw2 = 0, sh2 = 0;
    bool requested = false;
    while (Now() < seconds * 1000 && !window.QuitRequested)
    {
        if (i < packets.Count && Now() >= next)
        {
            uint ts = packets[i].Timestamp;
            for (; i < packets.Count && packets[i].Timestamp == ts; i++)
                foreach (var au in depack.Push(packets[i].Seq, packets[i].Timestamp, packets[i].Marker, packets[i].Payload, Now()))
                { buffer.Enqueue(au, au.RtpTimestamp, Now()); pipe.Notify(); }
            next += 1000.0 / 60;
        }
        if (!requested && Now() > seconds * 1000 * 0.6) { requested = true; window.RequestScreenshot((px, w, h) => { shot = px; sw2 = w; sh2 = h; }); }
        window.PumpOnce(1);
    }
    var d = window.TakeStats();
    Console.WriteLine($"Presented {d.Presented} frames, decoded {decoder.Decoded}, decode errors {decoder.Errors}");
    if (shot is null) { Console.WriteLine("FAIL: no screenshot"); return 1; }
    var path = XStream.Player.Screenshots.SaveTo(shot, sw2, sh2, Path.Combine(outDir, $"smoke-{renderer}.png"));
    // The pattern is one flat colour per keyframe: the centre pixel must be a real colour, not black.
    int c = ((sh2 / 2) * sw2 + sw2 / 2) * 4;
    Console.WriteLine($"Screenshot {sw2}x{sh2} -> {path}; centre RGB {shot[c]},{shot[c + 1]},{shot[c + 2]}");
    // Expected colours: the pattern's Y/Cb/Cr per keyframe through BT.709 limited-range conversion.
    int minFrames = int.Parse(Arg("--min-frames", "30"));
    double best = double.MaxValue;
    for (int idr = 0; idr < 64; idr++)
    {
        double y = (40 + (idr * 50) % 180 - 16) / 219.0, cb = (60 + (idr * 30) % 140 - 128) / 224.0, cr = (200 - (idr * 40) % 150 - 128) / 224.0;
        double r = Math.Clamp(y + 1.5748 * cr, 0, 1) * 255, g = Math.Clamp(y - 0.1873 * cb - 0.4681 * cr, 0, 1) * 255, b = Math.Clamp(y + 1.8556 * cb, 0, 1) * 255;
        best = Math.Min(best, Math.Abs(r - shot[c]) + Math.Abs(g - shot[c + 1]) + Math.Abs(b - shot[c + 2]));
    }
    Console.WriteLine($"Colour error vs BT.709 expectation: {best:F1} (sum of |dR|+|dG|+|dB|)");
    if (decoder.FallbackReason is { } why) Console.WriteLine("Decoder: " + why);
    bool ok = d.Presented >= minFrames && best <= 9;
    Console.WriteLine(ok ? "PASS" : "FAIL");
    return ok ? 0 : 1;
}
catch (Exception ex) { Console.WriteLine("FAIL: " + ex); return 1; }

sealed class PanelOverlay : IVideoOverlay
{
    private bool _drawn;
    public bool Visible => true;
    public bool NeedsRender => !_drawn;
    public int Width => 1920;
    public int Height => 1080;
    public void Render(Span<byte> rgba, int stride)
    {
        rgba.Clear();
        for (int y = 100; y < 300; y++)
            for (int x = 100; x < 700; x++) { int i = y * stride + x * 4; rgba[i] = 255; rgba[i + 1] = 255; rgba[i + 2] = 255; rgba[i + 3] = 128; }
        _drawn = true;
    }
}
