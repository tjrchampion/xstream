using System.Runtime.InteropServices;
using XStream.Core.Native;

namespace XStream.Core.Video;

/// <summary>
/// Hands the newest decoded picture from the decode thread to the window. Never queues: if a newer
/// picture arrives before the last one was shown, the older one is dropped. Queued frames are where
/// display lag usually hides.
/// </summary>
public sealed class FrameMailbox
{
    private DecodedPicture? _slot;
    private readonly object _lock = new();
    private readonly AutoResetEvent _ready = new(false);
    public long Posted { get; private set; }
    public long DroppedForLatency { get; private set; }

    public void Post(DecodedPicture p)
    {
        lock (_lock)
        {
            if (_slot is not null) { _slot.Dispose(); DroppedForLatency++; }
            _slot = p;
            Posted++;
        }
        _ready.Set();
    }

    public DecodedPicture? Take()
    {
        lock (_lock) { var p = _slot; _slot = null; return p; }
    }

    /// <summary>Block until a picture is posted or the timeout passes.</summary>
    public bool Wait(int timeoutMs) => _ready.WaitOne(timeoutMs);
}

/// <summary>How the picture is fitted to a screen of a different shape (e.g. a 16:9 stream on a 16:10 Mac).</summary>
public enum VideoScaling
{
    /// <summary>Whole picture, black bars where the shapes differ.</summary>
    Fit,
    /// <summary>Cover the screen, trimming the edges that don't fit. No distortion.</summary>
    Fill,
    /// <summary>Cover the screen, nothing trimmed, picture slightly stretched.</summary>
    Stretch,
}

/// <summary>
/// Something drawn over the video (the in-stream menu and stats). The window calls it on the main thread:
/// when <see cref="NeedsRender"/> is true it asks for fresh RGBA pixels (straight alpha, R,G,B,A byte order)
/// and composites them over the picture until the next change.
/// </summary>
public interface IVideoOverlay
{
    bool Visible { get; }
    bool NeedsRender { get; }
    int Width { get; }
    int Height { get; }
    void Render(Span<byte> rgba, int stride);
}

/// <summary>
/// Sees the window's raw SDL events first (keyboard-and-mouse play). Called on the window's thread: OnEvent with each
/// SDL_Event's bytes (true = used, the window ignores it), Tick once per loop.
/// </summary>
public interface IWindowInput
{
    bool OnEvent(ReadOnlySpan<byte> ev);
    void Tick();
}

public sealed record VideoWindowOptions
{
    public string Title { get; init; } = "Vexel";
    /// <summary>Borderless full screen at desktop resolution. This is also what AMD Fluid Motion Frames needs.</summary>
    public bool Fullscreen { get; init; } = true;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    /// <summary>Off by default: present the instant a frame is ready. With VRR (the Ally X's FreeSync) this stays tear-free.</summary>
    public bool VSync { get; init; }
    /// <summary>
    /// Renderer: null = best for the platform: Vexel's own Direct3D 11 renderer on Windows (zero-copy, clarity
    /// on the GPU), Metal on macOS. "sdl" (or an SDL driver name like "direct3d11") forces SDL's renderer.
    /// </summary>
    public string? Renderer { get; init; }
    /// <summary>
    /// Create the window hidden and show it with the first picture (see <see cref="VideoWindow.FirstPicture"/>), so
    /// nothing black covers the launcher's connecting screen while the connection and first keyframe arrive.
    /// </summary>
    public bool StartHidden { get; init; }
    /// <summary>
    /// Where to open, in screen coordinates (null: centred). The launcher passes its own window's spot so the stream
    /// takes over the same window's place (and, full screen, the same display) instead of popping up somewhere else.
    /// </summary>
    public int? X { get; init; }
    public int? Y { get; init; }
}

/// <summary>Live display numbers.</summary>
public readonly record struct DisplayStats(double Fps, double ArrivalToPresentP50Ms, double ArrivalToPresentP99Ms,
    double DecodeToPresentP50Ms, long Presented, long Dropped, string Renderer);

/// <summary>
/// SDL2 window that shows each decoded picture the moment it's available. Must be created and run on
/// the main thread (required on macOS). Uses Direct3D 11 on Windows (so AMD Fluid Motion Frames can be
/// switched on in AMD Software for the Ally X if wanted), Metal on macOS.
/// </summary>
public sealed unsafe class VideoWindow : IDisposable
{
    private const string Lib = "SDL2";
    private const uint InitVideo = 0x20;
    private const uint WindowHidden = 0x8;
    private const uint WindowShown = 0x4, WindowResizable = 0x20, WindowFullscreenDesktop = 0x1001, WindowAllowHighDpi = 0x2000;
    private const uint WindowMetal = 0x20000000;
    private const uint RendererAccelerated = 0x2, RendererPresentVsync = 0x4;
    private const int TextureStreaming = 1;
    private const uint PixelFormatNv12 = 0x3231564E, PixelFormatIyuv = 0x56555949;
    private const uint EventQuit = 0x100, EventKeyDown = 0x300, EventKeyUp = 0x301, EventTextInput = 0x303, EventMouseButtonDown = 0x401;
    private const uint PixelFormatAbgr8888 = 0x16762004; // R,G,B,A bytes in memory on little-endian
    private const int WindowPosCentered = 0x2FFF0000;

    [DllImport(Lib)] private static extern int SDL_InitSubSystem(uint flags);
    [DllImport(Lib)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Lib)] private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern IntPtr SDL_CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title, int x, int y, int w, int h, uint flags);
    [DllImport(Lib)] private static extern void SDL_DestroyWindow(IntPtr w);
    [DllImport(Lib)] private static extern void SDL_SetWindowTitle(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Lib)] private static extern IntPtr SDL_CreateRenderer(IntPtr w, int index, uint flags);
    [DllImport(Lib)] private static extern void SDL_DestroyRenderer(IntPtr r);
    [DllImport(Lib)] private static extern int SDL_GetRendererInfo(IntPtr r, out RendererInfo info);
    [DllImport(Lib)] private static extern IntPtr SDL_CreateTexture(IntPtr r, uint format, int access, int w, int h);
    [DllImport(Lib)] private static extern void SDL_DestroyTexture(IntPtr t);
    [DllImport(Lib)] private static extern int SDL_UpdateNVTexture(IntPtr t, IntPtr rect, byte* y, int yPitch, byte* uv, int uvPitch);
    [DllImport(Lib)] private static extern int SDL_UpdateYUVTexture(IntPtr t, IntPtr rect, byte* y, int yPitch, byte* u, int uPitch, byte* v, int vPitch);
    [DllImport(Lib)] private static extern int SDL_SetRenderDrawColor(IntPtr r, byte red, byte g, byte b, byte a);
    [DllImport(Lib)] private static extern int SDL_RenderClear(IntPtr r);
    [DllImport(Lib)] private static extern int SDL_RenderCopy(IntPtr r, IntPtr t, IntPtr src, IntPtr dst);
    [DllImport(Lib)] private static extern int SDL_GetRendererOutputSize(IntPtr r, out int w, out int h);
    [DllImport(Lib)] private static extern void SDL_GetWindowSize(IntPtr w, out int width, out int height);
    [DllImport(Lib)] private static extern void SDL_GetWindowPosition(IntPtr w, out int x, out int y);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int X, Y, W, H; public Rect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; } }
    [DllImport(Lib)] private static extern void SDL_RenderPresent(IntPtr r);
    [DllImport(Lib)] private static extern int SDL_PollEvent(byte* ev);
    [DllImport(Lib)] private static extern int SDL_ShowCursor(int toggle);
    [DllImport(Lib)] private static extern IntPtr SDL_GetError();
    [DllImport(Lib)] private static extern int SDL_SetWindowFullscreen(IntPtr w, uint flags);
    [DllImport(Lib)] private static extern void SDL_RaiseWindow(IntPtr w);
    [DllImport(Lib)] private static extern void SDL_ShowWindow(IntPtr w);
    [DllImport(Lib)] private static extern void SDL_RestoreWindow(IntPtr w);
    [DllImport(Lib)] private static extern int SDL_SetTextureBlendMode(IntPtr t, int mode);
    [DllImport(Lib)] private static extern int SDL_UpdateTexture(IntPtr t, IntPtr rect, byte* pixels, int pitch);
    [DllImport(Lib)] private static extern void SDL_SetYUVConversionMode(int mode);
    [DllImport(Lib)] private static extern void SDL_GetVersion(byte* ver);
    [DllImport(Lib)] private static extern int SDL_GetWindowWMInfo(IntPtr window, byte* info);
    [DllImport(Lib)] private static extern int SDL_RenderReadPixels(IntPtr r, Rect* rect, uint format, byte* pixels, int pitch);

    [StructLayout(LayoutKind.Sequential)]
    private struct RendererInfo { public IntPtr Name; public uint Flags; public uint NumFormats; public fixed uint Formats[16]; public int MaxW, MaxH; }

    static VideoWindow() => NativeLibs.Ensure();

    private readonly FrameMailbox _mailbox;
    private readonly Func<double> _clock;
    private IntPtr _win, _ren, _tex;
    private int _texW, _texH;
    private PlaneLayout _texLayout = PlaneLayout.Unsupported;
    private readonly double[] _a2p = new double[512], _d2p = new double[512];
    private int _n;
    private long _presented, _presentedWindow;
    private double _windowStart;
    private readonly object _statLock = new();

    private D3D11Renderer? _d3d;
    // Our own GPU renderer: Direct3D 11 on Windows, Metal on macOS (null = SDL's renderer).
    private IVideoRenderer? _gpu;
    private bool _hidden;

    /// <summary>Raised once, on the window's thread, right after the first picture was presented.</summary>
    public event Action? FirstPicture;

    /// <summary>Show a window created with StartHidden (and bring it to the front).</summary>
    public void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        SDL_ShowWindow(_win);
        SDL_RaiseWindow(_win);
    }

    private void AfterPresent()
    {
        if (_presented != 1) return;
        Show();
        FirstPicture?.Invoke();
    }
    /// <summary>The Direct3D 11 device the video is drawn with (Windows), for the decoder to decode on. Zero otherwise.</summary>
    public IntPtr D3D11Device => _d3d?.DevicePointer ?? IntPtr.Zero;

    private IntPtr _ovTex;
    private int _ovW, _ovH;
    private byte[] _ovPixels = [];
    private bool _fullscreen;

    public string RendererName { get; }
    public bool QuitRequested { get; private set; }
    /// <summary>Drawn over the video when visible.</summary>
    public IVideoOverlay? Overlay { get; set; }
    /// <summary>Key presses (SDL keycode). Return true if handled; otherwise Esc quits.</summary>
    public Func<int, bool>? KeyDown { get; set; }
    /// <summary>Key released (SDL keycode): push-to-talk.</summary>
    public Action<int>? KeyUp { get; set; }
    /// <summary>Text typed in the window (UTF-8 from SDL text input: the keyboard's own layout, IME and touch keyboard included).</summary>
    public Action<string>? TextInput { get; set; }
    /// <summary>Left clicks, in overlay coordinates (0..Overlay.Width/Height).</summary>
    public Action<int, int>? Click { get; set; }
    /// <summary>Gets every event before the window does (keyboard and mouse driving the controller).</summary>
    public IWindowInput? Input { get; set; }
    /// <summary>Size of the stream's pictures (0 until the first one).</summary>
    public (int Width, int Height) VideoSize => (_texW, _texH);
    public bool IsFullscreen => _fullscreen;
    /// <summary>Clarity boost level 0-3 (see <see cref="Video.Sharpen"/>). Safe to change from any thread.</summary>
    public int Sharpen { get => _sharpen; set => _sharpen = Math.Clamp(value, 0, 3); }
    /// <summary>Edge-aware upscaling when the picture is enlarged (GPU renderers). Safe to change from any thread.</summary>
    public bool Enhance { get => _enhance; set { _enhance = value; if (_gpu is { } g) g.Enhance = value; } }
    private volatile bool _enhance = true;

    /// <summary>The stream's own colour signalling (from its SPS): the GPU renderers convert to RGB to match. Any thread.</summary>
    public H264Format? Format
    {
        set { if (value is not null && _gpu is { } g) g.ColorFlags = (value.FullRange ? 1 : 0) + (value.Bt601 ? 2 : 0); }
    }
    /// <summary>Fit / Fill / Stretch. Safe to change from any thread; applies on the next frame.</summary>
    public VideoScaling Scaling { get => (VideoScaling)_scaling; set { _scaling = (int)value; _rescaled = true; } }
    private volatile int _scaling;
    private volatile bool _rescaled;
    private volatile int _sharpen;
    private byte[] _luma = [];
    private volatile Action<byte[], int, int>? _screenshot;

    /// <summary>
    /// Capture the next picture as shown (scaled to the screen, without the menu over it). The callback gets
    /// RGBA pixels, width and height, on the window's thread. Safe to call from any thread.
    /// </summary>
    public void RequestScreenshot(Action<byte[], int, int> done) => _screenshot = done;

    public VideoWindow(FrameMailbox mailbox, Func<double> clock, VideoWindowOptions? options = null)
    {
        var o = options ?? new VideoWindowOptions();
        _mailbox = mailbox;
        _clock = clock;
        string renderer = o.Renderer is { } r && r is not ("sdl" or "d3d11" or "xstream") ? r
            : OperatingSystem.IsWindows() ? "direct3d11" : OperatingSystem.IsMacOS() ? "metal" : "opengl";
        SDL_SetHint("SDL_RENDER_DRIVER", renderer);
        SDL_SetHint("SDL_RENDER_VSYNC", o.VSync ? "1" : "0");
        SDL_SetHint("SDL_VIDEO_MINIMIZE_ON_FOCUS_LOSS", "0");
        SDL_SetHint("SDL_RENDER_SCALE_QUALITY", "1");
        if (SDL_InitSubSystem(InitVideo) != 0) throw new InvalidOperationException("SDL video init failed: " + Err());
        // The console sends HD video in BT.709 colour. SDL's default converts YUV with SD (BT.601) maths, which
        // shifts reds and greens; Automatic picks BT.709 for anything taller than 576 lines.
        SDL_SetYUVConversionMode(3);

        // macOS: our own Metal renderer unless SDL's was asked for (Renderer "sdl"/"metal", or XSTREAM_RENDERER=sdl).
        bool ownMetal = OperatingSystem.IsMacOS() && o.Renderer is null or "xstream" or "vexel"
                        && Environment.GetEnvironmentVariable("XSTREAM_RENDERER") is not "sdl";
        uint flags = (o.StartHidden ? WindowHidden : WindowShown) | WindowAllowHighDpi | (o.Fullscreen ? WindowFullscreenDesktop : WindowResizable)
                     | (ownMetal ? WindowMetal : 0);
        _hidden = o.StartHidden;
        _win = SDL_CreateWindow(o.Title, o.X ?? WindowPosCentered, o.Y ?? WindowPosCentered, o.Width, o.Height, flags);
        if (_win == IntPtr.Zero) throw new InvalidOperationException("Could not create window: " + Err());
        _fullscreen = o.Fullscreen;
        if (o.Fullscreen) SDL_ShowCursor(0);

        // Windows: our own Direct3D 11 renderer unless SDL's was asked for. Falls back to SDL's if it can't start.
        string? d3dError = null;
        if (OperatingSystem.IsWindows() && o.Renderer is null or "d3d11" or "xstream" && Hwnd() is var hwnd && hwnd != IntPtr.Zero)
            _d3d = D3D11Renderer.TryCreate(hwnd, o.VSync, out d3dError);
        string? metalError = null;
        if (ownMetal && _d3d is null)
            _gpu = MetalRenderer.TryCreate(_win, o.VSync, out metalError);
        _gpu ??= _d3d;
        if (_gpu is not null)
        {
            RendererName = _gpu.Name;
            _gpu.Render(0, VideoScaling.Fit, null, null, null, Fit); // black until the first picture
        }
        else
        {
            _ren = SDL_CreateRenderer(_win, -1, RendererAccelerated | (o.VSync ? RendererPresentVsync : 0));
            if (_ren == IntPtr.Zero) _ren = SDL_CreateRenderer(_win, -1, 0); // software fallback
            if (_ren == IntPtr.Zero) throw new InvalidOperationException("Could not create renderer: " + Err());
            SDL_GetRendererInfo(_ren, out var info);
            string? why = d3dError ?? metalError;
            RendererName = (Marshal.PtrToStringUTF8(info.Name) ?? "unknown") + (why is null ? "" : $" (Vexel renderer unavailable: {why})");
            SDL_SetRenderDrawColor(_ren, 0, 0, 0, 255);
            SDL_RenderClear(_ren);
            SDL_RenderPresent(_ren);
        }
        _windowStart = _clock();
    }

    /// <summary>The window's HWND (Windows), from SDL_GetWindowWMInfo.</summary>
    private IntPtr Hwnd()
    {
        byte* info = stackalloc byte[256];
        new Span<byte>(info, 256).Clear();
        SDL_GetVersion(info);                       // SDL_SysWMinfo.version must be filled in
        if (SDL_GetWindowWMInfo(_win, info) == 0) return IntPtr.Zero;
        return *(IntPtr*)(info + 8);                // { version (3 bytes), subsystem (int), info.win.window, ... }
    }

    private static (int X, int Y, int W, int H) Fit(int w, int h, int bw, int bh)
    {
        var r = FitRect(w, h, bw, bh);
        return (r.X, r.Y, r.W, r.H);
    }

    private (int W, int H) OutputSize()
    {
        if (_gpu is not null) return _gpu.OutputSize;
        SDL_GetRendererOutputSize(_ren, out int w, out int h);
        return (w, h);
    }

    /// <summary>Run until <paramref name="keepRunning"/> returns false or the window is closed (Esc also quits).</summary>
    public void Run(Func<bool> keepRunning)
    {
        while (keepRunning() && !QuitRequested) PumpOnce(4);
    }

    /// <summary>Handle window events and present a new picture if one arrived within <paramref name="waitMs"/>.</summary>
    private static readonly bool NoRender = Environment.GetEnvironmentVariable("XSTREAM_NO_RENDER") is "1";

    public void PumpOnce(int waitMs)
    {
        byte* ev = stackalloc byte[64];
        var input = Input;
        while (SDL_PollEvent(ev) != 0)
        {
            uint type = *(uint*)ev;
            if (input?.OnEvent(new ReadOnlySpan<byte>(ev, 64)) == true) continue;
            if (type == EventQuit) QuitRequested = true;
            else if (type == EventKeyDown)
            {
                int sym = *(int*)(ev + 20); // keysym.sym
                if (KeyDown?.Invoke(sym) != true && sym == 27) QuitRequested = true; // Esc
            }
            else if (type == EventKeyUp) KeyUp?.Invoke(*(int*)(ev + 20));
            else if (type == EventTextInput && TextInput is { } typed)
            {
                var text = Marshal.PtrToStringUTF8((IntPtr)(ev + 12)) ?? ""; // SDL_TextInputEvent.text[32]
                if (text.Length > 0) typed(text);
            }
            else if (type == EventMouseButtonDown && *(ev + 16) == 1 && Click is { } click)
            {
                // Mouse positions are in window points; the overlay is drawn in output pixels (differs on HiDPI).
                int x = *(int*)(ev + 20), y = *(int*)(ev + 24);
                var ov = Overlay;
                if (ov is not null)
                {
                    SDL_GetWindowSize(_win, out int ww, out int wh);
                    var (ow, oh) = OutputSize();
                    double px = x * (double)ow / Math.Max(1, ww), py = y * (double)oh / Math.Max(1, wh);
                    var r = FitRect(ov.Width, ov.Height, ow, oh);
                    click((int)((px - r.X) * ov.Width / Math.Max(1, r.W)), (int)((py - r.Y) * ov.Height / Math.Max(1, r.H)));
                }
            }
        }
        input?.Tick();
        UpdateCursor();
        bool redraw = Overlay?.NeedsRender == true || _rescaled;
        if (!_mailbox.Wait(redraw ? 0 : waitMs))
        {
            if (redraw) Present(null); // menu or scaling changed but no new picture: redraw the last one
            // Tried: showing the last picture again so a ProMotion display runs at 120 Hz. It did (117 Hz), but the
            // repeat took the drawable the next real frame needed: net->screen went from ~3 to ~30 ms.
            return;
        }
        using var p = _mailbox.Take();
        if (p is null || p.Layout == PlaneLayout.Unsupported) return;
        if (NoRender) return; // XSTREAM_NO_RENDER=1 (testing): decode but don't draw
        Present(p);
    }

    private double _sharpenMs, _uploadMs, _presentMs;
    private long _sharpened, _uploads;

    /// <summary>Render-thread costs per picture (SDL path): sharpen, texture upload, draw + present.</summary>
    public string RenderSummary
    {
        get
        {
            long n = Math.Max(1, _sharpened + _uploads);
            return $"Render: sharpen {(_sharpened > 0 ? _sharpenMs / _sharpened : 0):F2} ms, upload {_uploadMs / n:F2} ms, " +
                   $"draw + present {_presentMs / n:F2} ms per picture ({n} pictures)" +
                   (_gpu is MetalRenderer m ? "\n" + m.PresentSummary : _gpu is D3D11Renderer d ? "\n" + d.PresentSummary : "");
        }
    }

    private void Present(DecodedPicture? p)
    {
        if (_gpu is not null) { PresentGpu(p); return; }
        if (p is null)
        {
            SDL_RenderClear(_ren);
            if (_tex != IntPtr.Zero) DrawVideo();
            DrawOverlay();
            SDL_RenderPresent(_ren);
            return;
        }
        if (_tex == IntPtr.Zero || p.Width != _texW || p.Height != _texH || p.Layout != _texLayout)
        {
            if (_tex != IntPtr.Zero) SDL_DestroyTexture(_tex);
            _tex = SDL_CreateTexture(_ren, p.Layout == PlaneLayout.Nv12 ? PixelFormatNv12 : PixelFormatIyuv, TextureStreaming, p.Width, p.Height);
            _texW = p.Width; _texH = p.Height; _texLayout = p.Layout;
            // No logical size: DrawVideo places the picture itself (Fit / Fill / Stretch).
        }
        byte* y = p.Plane(0);
        int yPitch = p.Pitch(0);
        int level = _sharpen;
        if (level > 0)
        {
            // Sharpen into our own buffer: the decoder's frame may be a reference picture for later frames.
            int need = p.Width * p.Height;
            if (_luma.Length < need) _luma = new byte[need];
            fixed (byte* dst = _luma)
            {
                double s0 = _clock();
                Video.Sharpen.Apply(y, yPitch, dst, p.Width, p.Width, p.Height, level);
                double s1 = _clock();
                Upload(p, dst, p.Width);
                double s2 = _clock();
                _sharpenMs += s1 - s0; _uploadMs += s2 - s1; _sharpened++;
            }
        }
        else
        {
            double u0 = _clock();
            Upload(p, y, yPitch);
            _uploadMs += _clock() - u0; _uploads++;
        }
        double r0 = _clock();
        SDL_RenderClear(_ren);
        DrawVideo();
        if (_screenshot is not null) Capture();
        DrawOverlay();
        SDL_RenderPresent(_ren);
        _presentMs += _clock() - r0;

        double now = _clock();
        lock (_statLock)
        {
            _a2p[_n % _a2p.Length] = now - p.ArrivalMs;
            _d2p[_n % _d2p.Length] = now - p.DecodedMs;
            _n++;
            _presented++;
            _presentedWindow++;
        }
        AfterPresent();
    }

    /// <summary>Our GPU path (Direct3D 11 / Metal): GPU copy or zero-copy, clarity in the shader, overlay, present.</summary>
    private void PresentGpu(DecodedPicture? p)
    {
        if (p is not null)
        {
            double u0 = _clock();
            _gpu!.Upload(p);
            _uploadMs += _clock() - u0; _uploads++;
            _texW = p.Width; _texH = p.Height; _texLayout = p.Layout;
        }
        _rescaled = false;
        var shot = _screenshot is not null && _gpu!.HasPicture ? Interlocked.Exchange(ref _screenshot, null) : null;
        double r0 = _clock();
        _gpu!.Render(_sharpen, (VideoScaling)_scaling, Overlay, OverlayPixels, shot, Fit);
        if (p is not null) _presentMs += _clock() - r0;
        if (p is null) return;
        double now = _clock();
        lock (_statLock)
        {
            _a2p[_n % _a2p.Length] = now - p.ArrivalMs;
            _d2p[_n % _d2p.Length] = now - p.DecodedMs;
            _n++;
            _presented++;
            _presentedWindow++;
        }
        AfterPresent();
    }

    private byte[]? OverlayPixels(IVideoOverlay ov)
    {
        if (_ovPixels.Length != ov.Width * ov.Height * 4) _ovPixels = new byte[ov.Width * ov.Height * 4];
        ov.Render(_ovPixels, ov.Width * 4);
        return _ovPixels;
    }

    private void DrawVideo()
    {
        _rescaled = false;
        SDL_GetRendererOutputSize(_ren, out int ow, out int oh);
        if (ow <= 0 || oh <= 0 || _texW <= 0) return;
        switch ((VideoScaling)_scaling)
        {
            case VideoScaling.Stretch:
                SDL_RenderCopy(_ren, _tex, IntPtr.Zero, IntPtr.Zero);
                break;
            case VideoScaling.Fill:
            {
                // Crop the picture to the screen's shape, then scale it to cover the screen.
                var src = FitRect(ow, oh, _texW, _texH);
                SDL_RenderCopy(_ren, _tex, (IntPtr)(&src), IntPtr.Zero);
                break;
            }
            default:
            {
                var dst = FitRect(_texW, _texH, ow, oh);
                SDL_RenderCopy(_ren, _tex, IntPtr.Zero, (IntPtr)(&dst));
                break;
            }
        }
    }

    private void Capture()
    {
        var done = Interlocked.Exchange(ref _screenshot, null);
        if (done is null) return;
        SDL_GetRendererOutputSize(_ren, out int ow, out int oh);
        // Just the picture: the video's rect for Fit, the whole screen when it's filled or stretched.
        var r = (VideoScaling)_scaling == VideoScaling.Fit ? FitRect(_texW, _texH, ow, oh) : new Rect(0, 0, ow, oh);
        if (r.W <= 0 || r.H <= 0) return;
        var px = new byte[r.W * r.H * 4];
        fixed (byte* p = px)
            if (SDL_RenderReadPixels(_ren, &r, PixelFormatAbgr8888, p, r.W * 4) != 0) return;
        done(px, r.W, r.H);
    }

    /// <summary>Largest rect with the shape <paramref name="w"/>:<paramref name="h"/> centred inside <paramref name="bw"/> x <paramref name="bh"/>.</summary>
    private static Rect FitRect(int w, int h, int bw, int bh)
    {
        if (w <= 0 || h <= 0) return new Rect(0, 0, bw, bh);
        double scale = Math.Min(bw / (double)w, bh / (double)h);
        int rw = (int)Math.Round(w * scale), rh = (int)Math.Round(h * scale);
        return new Rect((bw - rw) / 2, (bh - rh) / 2, rw, rh);
    }

    private void Upload(DecodedPicture p, byte* y, int yPitch)
    {
        if (p.Layout == PlaneLayout.Nv12) SDL_UpdateNVTexture(_tex, IntPtr.Zero, y, yPitch, p.Plane(1), p.Pitch(1));
        else SDL_UpdateYUVTexture(_tex, IntPtr.Zero, y, yPitch, p.Plane(1), p.Pitch(1), p.Plane(2), p.Pitch(2));
    }

    private void DrawOverlay()
    {
        var ov = Overlay;
        if (ov is null || !ov.Visible) return;
        if (_ovTex == IntPtr.Zero || _ovW != ov.Width || _ovH != ov.Height)
        {
            if (_ovTex != IntPtr.Zero) SDL_DestroyTexture(_ovTex);
            _ovW = ov.Width; _ovH = ov.Height;
            _ovTex = SDL_CreateTexture(_ren, PixelFormatAbgr8888, TextureStreaming, _ovW, _ovH);
            SDL_SetTextureBlendMode(_ovTex, 1); // alpha blend
            _ovPixels = new byte[_ovW * _ovH * 4];
            Render();
        }
        else if (ov.NeedsRender) Render();
        // The menu always keeps its own shape (fitted, never cropped or stretched), whatever the picture does.
        SDL_GetRendererOutputSize(_ren, out int ow, out int oh);
        var dst = FitRect(ov.Width, ov.Height, ow, oh);
        SDL_RenderCopy(_ren, _ovTex, IntPtr.Zero, (IntPtr)(&dst));

        void Render()
        {
            ov.Render(_ovPixels, _ovW * 4);
            fixed (byte* px = _ovPixels) SDL_UpdateTexture(_ovTex, IntPtr.Zero, px, _ovW * 4);
        }
    }

    /// <summary>Bring the video window back to the front (after the launcher's Settings were shown over it).</summary>
    public void Raise()
    {
        SDL_RestoreWindow(_win);
        SDL_RaiseWindow(_win);
    }

    public void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        SDL_SetWindowFullscreen(_win, on ? WindowFullscreenDesktop : 0);
        _fullscreen = on;
        UpdateCursor();
    }

    private int _cursor = -1;
    private void UpdateCursor()
    {
        int want = !_fullscreen || Overlay?.Visible == true ? 1 : 0;
        if (want != _cursor) { SDL_ShowCursor(want); _cursor = want; }
    }

    public void SetTitle(string title) => SDL_SetWindowTitle(_win, title);

    /// <summary>Where the window is (screen coordinates), or null when it's full screen. The launcher reopens there.</summary>
    public (int X, int Y, int W, int H)? WindowedRect
    {
        get
        {
            if (_win == IntPtr.Zero || _fullscreen) return null;
            SDL_GetWindowPosition(_win, out int x, out int y);
            SDL_GetWindowSize(_win, out int w, out int h);
            return (x, y, w, h);
        }
    }

    /// <summary>Stats since the last call (fps) and over the last 512 frames (latency).</summary>
    public DisplayStats TakeStats()
    {
        lock (_statLock)
        {
            int k = Math.Min(_n, _a2p.Length);
            var a = _a2p.Take(k).OrderBy(x => x).ToArray();
            var d = _d2p.Take(k).OrderBy(x => x).ToArray();
            double now = _clock(), secs = Math.Max(0.001, (now - _windowStart) / 1000);
            var s = new DisplayStats(_presentedWindow / secs,
                k > 0 ? a[k / 2] : 0, k > 0 ? a[(int)(k * 0.99)] : 0, k > 0 ? d[k / 2] : 0,
                _presented, _mailbox.DroppedForLatency, RendererName);
            _presentedWindow = 0;
            _windowStart = now;
            return s;
        }
    }

    private static string Err() => Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";

    public void Dispose()
    {
        _gpu?.Dispose(); _gpu = null; _d3d = null;
        if (_ovTex != IntPtr.Zero) { SDL_DestroyTexture(_ovTex); _ovTex = IntPtr.Zero; }
        if (_tex != IntPtr.Zero) { SDL_DestroyTexture(_tex); _tex = IntPtr.Zero; }
        if (_ren != IntPtr.Zero) { SDL_DestroyRenderer(_ren); _ren = IntPtr.Zero; }
        if (_win != IntPtr.Zero) { SDL_DestroyWindow(_win); _win = IntPtr.Zero; }
        SDL_QuitSubSystem(InitVideo);
    }
}
