using System.Runtime.InteropServices;

namespace XStream.Core.Video;

/// <summary>
/// Vexel's own Metal renderer for macOS (the counterpart of <see cref="D3D11Renderer"/>), replacing SDL's renderer
/// for the video, with latency first:
///  - Zero-copy: pictures from <see cref="VideoToolboxDecoder"/> are IOSurfaces; Core Video's texture cache turns
///    their planes straight into Metal textures, so nothing is copied or uploaded per frame (SDL's renderer copied
///    ~5.5 MB a frame, ~1.5 ms).
///  - Clarity boost in the fragment shader (same halo-free formula as <see cref="Sharpen"/>): ~free on the GPU,
///    where the CPU version cost ~5 ms a frame.
///  - At most two drawables in flight, and with VSync off the layer doesn't wait for the display refresh.
///  - Never blocks on the GPU: a picture's buffers are let go only once the GPU has finished with them.
/// BT.709 limited-range colour conversion, Fit / Fill / Stretch, the menu overlay alpha-blended on top, screenshots.
/// All calls on the window's (main) thread.
/// </summary>
internal sealed unsafe class MetalRenderer : IVideoRenderer
{
    private const string Shaders = """
        #include <metal_stdlib>
        using namespace metal;
        struct VOut { float4 pos [[position]]; float2 uv; };
        struct Params { float2 texel; float sharpen; float pad; float2 uvScale; float2 uvOffset; };
        constexpr sampler samp(filter::linear, address::clamp_to_edge);

        // One triangle covering the viewport; no vertex buffer.
        vertex VOut vs(uint id [[vertex_id]])
        {
            float2 uv = float2((id << 1) & 2, id & 2);
            VOut o;
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Clarity boost: c + k/16 * (4c - up - down - left - right), clamped to those five pixels' range
        // (steeper edges, no halos), exactly like the CPU and Direct3D versions.
        static float luma(texture2d<float> ty, float2 uv, constant Params& p)
        {
            float c = ty.sample(samp, uv).r;
            if (p.sharpen <= 0) return c;
            float n = ty.sample(samp, uv + float2(0, -p.texel.y)).r;
            float s = ty.sample(samp, uv + float2(0,  p.texel.y)).r;
            float e = ty.sample(samp, uv + float2( p.texel.x, 0)).r;
            float w = ty.sample(samp, uv + float2(-p.texel.x, 0)).r;
            float lo = min(c, min(min(n, s), min(e, w)));
            float hi = max(c, max(max(n, s), max(e, w)));
            return clamp(c + p.sharpen * (4 * c - n - s - e - w), lo, hi);
        }

        // BT.709, limited range (16-235 luma, 16-240 chroma): what the console sends.
        static float4 toRgb(float y, float2 cbcr)
        {
            y = (y - 16.0 / 255.0) * (255.0 / 219.0);
            cbcr = (cbcr - 128.0 / 255.0) * (255.0 / 224.0);
            float3 rgb = float3(y + 1.5748 * cbcr.y,
                                y - 0.1873 * cbcr.x - 0.4681 * cbcr.y,
                                y + 1.8556 * cbcr.x);
            return float4(saturate(rgb), 1);
        }

        fragment float4 fsNv12(VOut i [[stage_in]], texture2d<float> ty [[texture(0)]], texture2d<float> tuv [[texture(1)]],
                               constant Params& p [[buffer(0)]])
        {
            float2 uv = p.uvOffset + i.uv * p.uvScale;
            return toRgb(luma(ty, uv, p), tuv.sample(samp, uv).rg);
        }

        fragment float4 fsI420(VOut i [[stage_in]], texture2d<float> ty [[texture(0)]], texture2d<float> tu [[texture(1)]],
                               texture2d<float> tv [[texture(2)]], constant Params& p [[buffer(0)]])
        {
            float2 uv = p.uvOffset + i.uv * p.uvScale;
            return toRgb(luma(ty, uv, p), float2(tu.sample(samp, uv).r, tv.sample(samp, uv).r));
        }

        fragment float4 fsOverlay(VOut i [[stage_in]], texture2d<float> t [[texture(0)]]) { return t.sample(samp, i.uv); }
        """;

    [StructLayout(LayoutKind.Sequential)] private struct Params { public float TexelX, TexelY, Sharpen, Pad, ScaleX, ScaleY, OffsetX, OffsetY; }

    private const string SdlLib = "SDL2";
    [DllImport(SdlLib)] private static extern IntPtr SDL_Metal_CreateView(IntPtr window);
    [DllImport(SdlLib)] private static extern void SDL_Metal_DestroyView(IntPtr view);
    [DllImport(SdlLib)] private static extern IntPtr SDL_Metal_GetLayer(IntPtr view);
    [DllImport(SdlLib)] private static extern void SDL_Metal_GetDrawableSize(IntPtr window, out int w, out int h);

    private const string MetalLib = "/System/Library/Frameworks/Metal.framework/Metal";
    private const string CVLib = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";
    [DllImport(MetalLib)] private static extern IntPtr MTLCreateSystemDefaultDevice();
    [DllImport(CVLib)] private static extern int CVMetalTextureCacheCreate(IntPtr alloc, IntPtr cacheAttrs, IntPtr device, IntPtr textureAttrs, out IntPtr cache);
    [DllImport(CVLib)] private static extern int CVMetalTextureCacheCreateTextureFromImage(IntPtr alloc, IntPtr cache, IntPtr image, IntPtr attrs, nuint pixelFormat, nuint w, nuint h, nuint plane, out IntPtr texture);
    [DllImport(CVLib)] private static extern IntPtr CVMetalTextureGetTexture(IntPtr texture);
    [DllImport(CVLib)] private static extern void CVMetalTextureCacheFlush(IntPtr cache, ulong options);
    [DllImport(CVLib)] private static extern IntPtr CVPixelBufferRetain(IntPtr pb);
    [DllImport(CVLib)] private static extern void CVPixelBufferRelease(IntPtr pb);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr cf);

    // MTLPixelFormat / enums
    private const nuint R8Unorm = 10, RG8Unorm = 30, RGBA8Unorm = 70, BGRA8Unorm = 80;
    private const nuint LoadClear = 2, StoreStore = 1, PrimitiveTriangle = 3, CommandBufferCompleted = 4;
    private const nuint BlendSourceAlpha = 4, BlendOneMinusSourceAlpha = 5, BlendOne = 1;
    private const nuint UsageShaderRead = 1, UsageRenderTarget = 4, StorageManaged = 1;

    private readonly IntPtr _window, _view, _layer, _device, _queue, _cache;
    private readonly IntPtr _pipeNv12, _pipeI420, _pipeOverlay, _pipeNv12Shot, _pipeI420Shot;
    private int _outW, _outH;
    private readonly bool _vsync;

    // The picture on screen: IOSurface planes (zero-copy) or our own textures (FFmpeg pictures).
    private sealed class Frame
    {
        public IntPtr PixelBuffer, CvY, CvUV;  // zero-copy picture (Core Video objects we hold)
        public IntPtr LastCommandBuffer;        // the last GPU work that read it (retained)
    }
    private Frame? _current;
    private readonly List<Frame> _retired = [];
    private IntPtr _texY, _texUV, _texU, _texV; // owned textures for system-memory pictures
    private PlaneLayout _layout = PlaneLayout.Unsupported;
    private int _w, _h;
    private IntPtr _ovTex;
    private int _ovW, _ovH;

    public string Name { get; }
    public bool HasPicture => _layout != PlaneLayout.Unsupported;
    public (int W, int H) OutputSize => (_outW, _outH);

    private MetalRenderer(IntPtr window, bool vsync)
    {
        _window = window;
        _vsync = vsync;
        _device = MTLCreateSystemDefaultDevice();
        if (_device == IntPtr.Zero) throw new InvalidOperationException("no Metal device");
        _view = SDL_Metal_CreateView(window);
        if (_view == IntPtr.Zero) throw new InvalidOperationException("SDL_Metal_CreateView failed");
        _layer = SDL_Metal_GetLayer(_view);
        ObjC.Void(_layer, S.setDevice, _device);
        ObjC.VoidU(_layer, S.setPixelFormat, BGRA8Unorm);
        ObjC.VoidB(_layer, S.setFramebufferOnly, true);
        ObjC.VoidU(_layer, S.setMaximumDrawableCount, 2);      // never more than one frame queued behind the newest
        ObjC.VoidB(_layer, S.setDisplaySyncEnabled, vsync);   // VSync off: show frames without waiting for the refresh
        _queue = ObjC.Call(_device, S.newCommandQueue);
        if (CVMetalTextureCacheCreate(IntPtr.Zero, IntPtr.Zero, _device, IntPtr.Zero, out _cache) != 0)
            throw new InvalidOperationException("CVMetalTextureCacheCreate failed");

        var src = ObjC.NSString(Shaders);
        IntPtr err = IntPtr.Zero;
        var lib = ObjC.Call(_device, S.newLibraryWithSource_options_error, src, IntPtr.Zero, &err);
        if (lib == IntPtr.Zero) throw new InvalidOperationException("Metal shaders: " + ObjC.Describe(err));
        try
        {
            _pipeNv12 = Pipeline(lib, "fsNv12", BGRA8Unorm, blend: false);
            _pipeI420 = Pipeline(lib, "fsI420", BGRA8Unorm, blend: false);
            _pipeOverlay = Pipeline(lib, "fsOverlay", BGRA8Unorm, blend: true);
            _pipeNv12Shot = Pipeline(lib, "fsNv12", RGBA8Unorm, blend: false);
            _pipeI420Shot = Pipeline(lib, "fsI420", RGBA8Unorm, blend: false);
        }
        finally { ObjC.Release(lib); }

        Name = "metal (Vexel renderer: zero-copy, clarity on the GPU" + (vsync ? "" : ", immediate present") + ") - " +
               ObjC.String(ObjC.Call(_device, S.name));
        SyncSize();
    }

    /// <summary>Our Metal renderer on the SDL window (created with SDL_WINDOW_METAL), or null with the reason.</summary>
    public static MetalRenderer? TryCreate(IntPtr window, bool vsync, out string? error)
    {
        try { error = null; return new MetalRenderer(window, vsync); }
        catch (Exception e) { error = e.Message; return null; }
    }

    private IntPtr Pipeline(IntPtr lib, string fragment, nuint format, bool blend)
    {
        var vs = ObjC.Call(lib, S.newFunctionWithName, ObjC.NSString("vs"));
        var fs = ObjC.Call(lib, S.newFunctionWithName, ObjC.NSString(fragment));
        var desc = ObjC.Call(ObjC.Class("MTLRenderPipelineDescriptor"), S.@new);
        try
        {
            ObjC.Void(desc, S.setVertexFunction, vs);
            ObjC.Void(desc, S.setFragmentFunction, fs);
            var ca = ObjC.CallU(ObjC.Call(desc, S.colorAttachments), S.objectAtIndexedSubscript, 0);
            ObjC.VoidU(ca, S.setPixelFormat, format);
            if (blend)
            {
                ObjC.VoidB(ca, S.setBlendingEnabled, true);
                ObjC.VoidU(ca, S.setSourceRGBBlendFactor, BlendSourceAlpha);
                ObjC.VoidU(ca, S.setDestinationRGBBlendFactor, BlendOneMinusSourceAlpha);
                ObjC.VoidU(ca, S.setSourceAlphaBlendFactor, BlendOne);
                ObjC.VoidU(ca, S.setDestinationAlphaBlendFactor, BlendOneMinusSourceAlpha);
            }
            IntPtr err = IntPtr.Zero;
            var state = ObjC.Call(_device, S.newRenderPipelineStateWithDescriptor_error, desc, &err);
            if (state == IntPtr.Zero) throw new InvalidOperationException($"Metal pipeline {fragment}: " + ObjC.Describe(err));
            return state;
        }
        finally { ObjC.Release(desc); ObjC.Release(vs); ObjC.Release(fs); }
    }

    private void SyncSize()
    {
        SDL_Metal_GetDrawableSize(_window, out int w, out int h);
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (w == _outW && h == _outH) return;
        _outW = w; _outH = h;
        ObjC.VoidSize(_layer, S.setDrawableSize, new CGSize { W = w, H = h });
    }

    // ---- Pictures -----------------------------------------------------------------------------------

    /// <summary>Take the newest decoded picture: zero-copy for VideoToolbox pictures, an upload for others.</summary>
    public void Upload(DecodedPicture p)
    {
        ReleaseFinished();
        if (p.PixelBuffer != IntPtr.Zero)
        {
            var f = new Frame { PixelBuffer = CVPixelBufferRetain(p.PixelBuffer) };
            int w = p.Width, h = p.Height;
            if (CVMetalTextureCacheCreateTextureFromImage(IntPtr.Zero, _cache, f.PixelBuffer, IntPtr.Zero, R8Unorm, (nuint)w, (nuint)h, 0, out f.CvY) != 0 ||
                CVMetalTextureCacheCreateTextureFromImage(IntPtr.Zero, _cache, f.PixelBuffer, IntPtr.Zero, RG8Unorm, (nuint)(w / 2), (nuint)(h / 2), 1, out f.CvUV) != 0)
            {
                Free(f);
                return;
            }
            Retire();
            ReleaseOwnTextures();
            _current = f;
            _layout = PlaneLayout.Nv12; _w = w; _h = h;
            return;
        }

        // System-memory picture (FFmpeg fallback): our own textures.
        Retire();
        if (_layout != p.Layout || _w != p.Width || _h != p.Height || _texY == IntPtr.Zero)
        {
            ReleaseOwnTextures();
            _w = p.Width; _h = p.Height; _layout = p.Layout;
            _texY = Texture(R8Unorm, _w, _h, UsageShaderRead);
            if (p.Layout == PlaneLayout.Nv12) _texUV = Texture(RG8Unorm, _w / 2, _h / 2, UsageShaderRead);
            else { _texU = Texture(R8Unorm, _w / 2, _h / 2, UsageShaderRead); _texV = Texture(R8Unorm, _w / 2, _h / 2, UsageShaderRead); }
        }
        Replace(_texY, p.Plane(0), p.Pitch(0), _w, _h);
        if (p.Layout == PlaneLayout.Nv12) Replace(_texUV, p.Plane(1), p.Pitch(1), _w / 2, _h / 2);
        else { Replace(_texU, p.Plane(1), p.Pitch(1), _w / 2, _h / 2); Replace(_texV, p.Plane(2), p.Pitch(2), _w / 2, _h / 2); }
    }

    private IntPtr Texture(nuint format, int w, int h, nuint usage, nuint storage = 0)
    {
        var desc = ObjC.TextureDescriptor(format, (nuint)w, (nuint)h);
        ObjC.VoidU(desc, S.setUsage, usage);
        if (storage != 0) ObjC.VoidU(desc, S.setStorageMode, storage);
        return ObjC.Call(_device, S.newTextureWithDescriptor, desc);
    }

    private static void Replace(IntPtr tex, byte* src, int pitch, int w, int h) =>
        ObjC.ReplaceRegion(tex, new Region { W = (nuint)w, H = (nuint)h, D = 1 }, src, (nuint)pitch);

    /// <summary>The picture being replaced stays alive until the GPU has finished drawing it.</summary>
    private void Retire()
    {
        if (_current is null) return;
        _retired.Add(_current);
        _current = null;
    }

    private void ReleaseFinished()
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
        {
            var f = _retired[i];
            if (f.LastCommandBuffer != IntPtr.Zero && ObjC.CallU(f.LastCommandBuffer, S.status) < CommandBufferCompleted) continue;
            Free(f);
            _retired.RemoveAt(i);
        }
        if (_retired.Count > 8) CVMetalTextureCacheFlush(_cache, 0);
    }

    private static void Free(Frame f)
    {
        if (f.CvY != IntPtr.Zero) CFRelease(f.CvY);
        if (f.CvUV != IntPtr.Zero) CFRelease(f.CvUV);
        if (f.PixelBuffer != IntPtr.Zero) CVPixelBufferRelease(f.PixelBuffer);
        if (f.LastCommandBuffer != IntPtr.Zero) ObjC.Release(f.LastCommandBuffer);
        f.CvY = f.CvUV = f.PixelBuffer = f.LastCommandBuffer = IntPtr.Zero;
    }

    private void ReleaseOwnTextures()
    {
        foreach (var t in new[] { _texY, _texUV, _texU, _texV }) if (t != IntPtr.Zero) ObjC.Release(t);
        _texY = _texUV = _texU = _texV = IntPtr.Zero;
    }

    // ---- Drawing ------------------------------------------------------------------------------------

    /// <summary>Draw the current picture (Fit / Fill / Stretch, clarity level 0-3) and the overlay, then present.</summary>
    public void Render(int sharpenLevel, VideoScaling scaling, IVideoOverlay? overlay, Func<IVideoOverlay, byte[]?>? overlayPixels,
        Action<byte[], int, int>? screenshot, Func<int, int, int, int, (int X, int Y, int W, int H)> fit)
    {
        var pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            SyncSize();
            var (videoRect, prm) = Layout(sharpenLevel, scaling, fit);
            if (screenshot is not null && HasPicture) Screenshot(screenshot, prm, scaling == VideoScaling.Fit ? videoRect : (0, 0, _outW, _outH));

            var drawable = ObjC.Call(_layer, S.nextDrawable);
            if (drawable == IntPtr.Zero) return;
            var target = ObjC.Call(drawable, S.texture);
            var cb = ObjC.Call(_queue, S.commandBuffer);
            var enc = ObjC.Call(cb, S.renderCommandEncoderWithDescriptor, PassDescriptor(target));

            if (HasPicture)
            {
                ObjC.SetViewport(enc, new MtlViewport { X = videoRect.X, Y = videoRect.Y, W = videoRect.W, H = videoRect.H, ZFar = 1 });
                DrawPicture(enc, prm, forScreenshot: false);
            }

            if (overlay is { Visible: true })
            {
                if (_ovTex == IntPtr.Zero || _ovW != overlay.Width || _ovH != overlay.Height)
                {
                    if (_ovTex != IntPtr.Zero) ObjC.Release(_ovTex);
                    _ovW = overlay.Width; _ovH = overlay.Height;
                    _ovTex = Texture(RGBA8Unorm, _ovW, _ovH, UsageShaderRead);
                    if (overlayPixels?.Invoke(overlay) is { } px) UploadOverlay(px);
                }
                else if (overlay.NeedsRender && overlayPixels?.Invoke(overlay) is { } px) UploadOverlay(px);
                var r = fit(overlay.Width, overlay.Height, _outW, _outH);
                ObjC.SetViewport(enc, new MtlViewport { X = r.X, Y = r.Y, W = r.W, H = r.H, ZFar = 1 });
                ObjC.Void(enc, S.setRenderPipelineState, _pipeOverlay);
                ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _ovTex, 0);
                ObjC.Draw(enc, PrimitiveTriangle, 0, 3);
            }

            ObjC.Call(enc, S.endEncoding);
            // Tried presentDrawable:afterMinimumDuration: (1/120) and presentDrawable:atTime: (now): neither got the
            // display past 60 Hz, and both had most frames replaced before they were shown (judder). Plain present wins.
            ObjC.Void(cb, S.presentDrawable, drawable);
            ObjC.Call(cb, S.commit);
            if (HasPicture) _shown.Enqueue((ObjC.Call(drawable, S.retain), CACurrentMediaTime()));
            CollectPresented();
            if (_current is not null)
            {
                if (_current.LastCommandBuffer != IntPtr.Zero) ObjC.Release(_current.LastCommandBuffer);
                _current.LastCommandBuffer = ObjC.Call(cb, S.retain);
            }
        }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    // ---- When frames actually reach the display -----------------------------------------------------
    // Our net->screen time stops when we hand the frame to Core Animation. What happens after (the window
    // server compositing it, waiting for the next refresh) is invisible to it, so read it back: each drawable's
    // presentedTime is when it appeared on the display, on the same clock as CACurrentMediaTime.

    [DllImport("/System/Library/Frameworks/QuartzCore.framework/QuartzCore")] private static extern double CACurrentMediaTime();
    private static readonly IntPtr SelPresentedTime = ObjC.Sel("presentedTime");
    private readonly Queue<(IntPtr Drawable, double HandedAt)> _shown = new();
    private readonly double[] _glassMs = new double[4096];
    private long _glassCount, _neverShown;
    private double _lastShownAt = double.NaN, _intervalSum; private long _intervals;

    private void CollectPresented()
    {
        double now = CACurrentMediaTime();
        while (_shown.Count > 0)
        {
            var (d, handed) = _shown.Peek();
            double at = ObjC.CallD(d, SelPresentedTime);
            if (at > 0)
            {
                _glassMs[_glassCount++ % _glassMs.Length] = (at - handed) * 1000;
                if (!double.IsNaN(_lastShownAt) && at - _lastShownAt < 0.1) { _intervalSum += at - _lastShownAt; _intervals++; }
                _lastShownAt = at;
            }
            else if (now - handed > 0.25) _neverShown++; // replaced by a newer frame before the display took it
            else break;
            ObjC.Release(d);
            _shown.Dequeue();
        }
    }

    /// <summary>One line for the session log: hand-off to display, from the drawables' presentedTime.</summary>
    public string PresentSummary
    {
        get
        {
            int k = (int)Math.Min(_glassCount, _glassMs.Length);
            if (k == 0) return "On screen: no presentation times reported";
            var v = _glassMs.Take(k).OrderBy(x => x).ToArray();
            string hz = _intervals > 0 ? $"{1 / (_intervalSum / _intervals):F0} Hz" : "?";
            return $"On screen: present -> display p50 {v[k / 2]:F2} ms, p90 {v[(int)(k * 0.9)]:F2} ms, p99 {v[Math.Min(k - 1, (int)(k * 0.99))]:F2} ms " +
                   $"({k} frames, {_neverShown} replaced before shown, display {hz})";
        }
    }

    private ((int X, int Y, int W, int H) rect, Params prm) Layout(int sharpenLevel, VideoScaling scaling, Func<int, int, int, int, (int X, int Y, int W, int H)> fit)
    {
        (int X, int Y, int W, int H) rect = (0, 0, _outW, _outH);
        var prm = new Params { TexelX = 1f / Math.Max(1, _w), TexelY = 1f / Math.Max(1, _h), Sharpen = Sharpen.Strength(sharpenLevel) / 16f, ScaleX = 1, ScaleY = 1 };
        if (!HasPicture) return (rect, prm);
        if (scaling == VideoScaling.Fill)
        {
            var src = fit(_outW, _outH, _w, _h); // crop the picture to the screen's shape (centred)
            prm.ScaleX = src.W / (float)_w; prm.ScaleY = src.H / (float)_h;
            prm.OffsetX = src.X / (float)_w; prm.OffsetY = src.Y / (float)_h;
        }
        else if (scaling == VideoScaling.Fit) rect = fit(_w, _h, _outW, _outH);
        return (rect, prm);
    }

    private void DrawPicture(IntPtr enc, Params prm, bool forScreenshot)
    {
        if (_current is not null)
        {
            ObjC.Void(enc, S.setRenderPipelineState, forScreenshot ? _pipeNv12Shot : _pipeNv12);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, CVMetalTextureGetTexture(_current.CvY), 0);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, CVMetalTextureGetTexture(_current.CvUV), 1);
        }
        else if (_layout == PlaneLayout.Nv12)
        {
            ObjC.Void(enc, S.setRenderPipelineState, forScreenshot ? _pipeNv12Shot : _pipeNv12);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _texY, 0);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _texUV, 1);
        }
        else
        {
            ObjC.Void(enc, S.setRenderPipelineState, forScreenshot ? _pipeI420Shot : _pipeI420);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _texY, 0);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _texU, 1);
            ObjC.VoidPU(enc, S.setFragmentTexture_atIndex, _texV, 2);
        }
        ObjC.SetFragmentBytes(enc, &prm, (nuint)sizeof(Params), 0);
        ObjC.Draw(enc, PrimitiveTriangle, 0, 3);
    }

    private static IntPtr PassDescriptor(IntPtr target)
    {
        var rpd = ObjC.Call(ObjC.Class("MTLRenderPassDescriptor"), S.renderPassDescriptor);
        var ca = ObjC.CallU(ObjC.Call(rpd, S.colorAttachments), S.objectAtIndexedSubscript, 0);
        ObjC.Void(ca, S.setTexture, target);
        ObjC.VoidU(ca, S.setLoadAction, LoadClear);
        ObjC.VoidU(ca, S.setStoreAction, StoreStore);
        ObjC.SetClearColor(ca, new ClearColor { A = 1 });
        return rpd;
    }

    private void UploadOverlay(byte[] rgba)
    {
        fixed (byte* p = rgba) Replace(_ovTex, p, _ovW * 4, _ovW, _ovH);
    }

    /// <summary>The picture as shown (scaled, without the menu), read back as RGBA. Rare, so it may wait for the GPU.</summary>
    private void Screenshot(Action<byte[], int, int> done, Params prm, (int X, int Y, int W, int H) r)
    {
        r.W = Math.Min(r.W, _outW - r.X); r.H = Math.Min(r.H, _outH - r.Y);
        if (r.W <= 0 || r.H <= 0) return;
        var tex = Texture(RGBA8Unorm, r.W, r.H, UsageRenderTarget | UsageShaderRead, StorageManaged);
        try
        {
            var cb = ObjC.Call(_queue, S.commandBuffer);
            var enc = ObjC.Call(cb, S.renderCommandEncoderWithDescriptor, PassDescriptor(tex));
            ObjC.SetViewport(enc, new MtlViewport { W = r.W, H = r.H, ZFar = 1 });
            DrawPicture(enc, prm, forScreenshot: true);
            ObjC.Call(enc, S.endEncoding);
            var blit = ObjC.Call(cb, S.blitCommandEncoder);
            ObjC.Void(blit, S.synchronizeResource, tex);
            ObjC.Call(blit, S.endEncoding);
            ObjC.Call(cb, S.commit);
            ObjC.Call(cb, S.waitUntilCompleted);
            var px = new byte[r.W * r.H * 4];
            fixed (byte* dst = px) ObjC.GetBytes(tex, dst, (nuint)(r.W * 4), new Region { W = (nuint)r.W, H = (nuint)r.H, D = 1 });
            done(px, r.W, r.H);
        }
        finally { ObjC.Release(tex); }
    }

    public void Dispose()
    {
        // Let in-flight GPU work finish before freeing what it reads.
        foreach (var f in _retired.Append(_current))
            if (f?.LastCommandBuffer is { } cb && cb != IntPtr.Zero) ObjC.Call(cb, S.waitUntilCompleted);
        foreach (var f in _retired) Free(f);
        _retired.Clear();
        if (_current is not null) { Free(_current); _current = null; }
        ReleaseOwnTextures();
        while (_shown.Count > 0) ObjC.Release(_shown.Dequeue().Drawable);
        if (_ovTex != IntPtr.Zero) ObjC.Release(_ovTex);
        foreach (var o in new[] { _pipeNv12, _pipeI420, _pipeOverlay, _pipeNv12Shot, _pipeI420Shot, _queue })
            if (o != IntPtr.Zero) ObjC.Release(o);
        if (_cache != IntPtr.Zero) CFRelease(_cache);
        if (_view != IntPtr.Zero) SDL_Metal_DestroyView(_view);
        if (_device != IntPtr.Zero) ObjC.Release(_device);
    }

    /// <summary>Selectors, registered once.</summary>
    private static class S
    {
        public static readonly IntPtr setDevice = ObjC.Sel("setDevice:"), setPixelFormat = ObjC.Sel("setPixelFormat:"),
            setFramebufferOnly = ObjC.Sel("setFramebufferOnly:"), setMaximumDrawableCount = ObjC.Sel("setMaximumDrawableCount:"),
            setDisplaySyncEnabled = ObjC.Sel("setDisplaySyncEnabled:"), setDrawableSize = ObjC.Sel("setDrawableSize:"),
            newCommandQueue = ObjC.Sel("newCommandQueue"), newLibraryWithSource_options_error = ObjC.Sel("newLibraryWithSource:options:error:"),
            newFunctionWithName = ObjC.Sel("newFunctionWithName:"), @new = ObjC.Sel("new"),
            setVertexFunction = ObjC.Sel("setVertexFunction:"), setFragmentFunction = ObjC.Sel("setFragmentFunction:"),
            colorAttachments = ObjC.Sel("colorAttachments"), objectAtIndexedSubscript = ObjC.Sel("objectAtIndexedSubscript:"),
            setBlendingEnabled = ObjC.Sel("setBlendingEnabled:"), setSourceRGBBlendFactor = ObjC.Sel("setSourceRGBBlendFactor:"),
            setDestinationRGBBlendFactor = ObjC.Sel("setDestinationRGBBlendFactor:"), setSourceAlphaBlendFactor = ObjC.Sel("setSourceAlphaBlendFactor:"),
            setDestinationAlphaBlendFactor = ObjC.Sel("setDestinationAlphaBlendFactor:"),
            newRenderPipelineStateWithDescriptor_error = ObjC.Sel("newRenderPipelineStateWithDescriptor:error:"),
            name = ObjC.Sel("name"), setUsage = ObjC.Sel("setUsage:"), setStorageMode = ObjC.Sel("setStorageMode:"),
            newTextureWithDescriptor = ObjC.Sel("newTextureWithDescriptor:"), status = ObjC.Sel("status"),
            nextDrawable = ObjC.Sel("nextDrawable"), texture = ObjC.Sel("texture"), commandBuffer = ObjC.Sel("commandBuffer"),
            renderCommandEncoderWithDescriptor = ObjC.Sel("renderCommandEncoderWithDescriptor:"),
            setRenderPipelineState = ObjC.Sel("setRenderPipelineState:"), setFragmentTexture_atIndex = ObjC.Sel("setFragmentTexture:atIndex:"),
            endEncoding = ObjC.Sel("endEncoding"), presentDrawable = ObjC.Sel("presentDrawable:"), commit = ObjC.Sel("commit"),
            retain = ObjC.Sel("retain"), renderPassDescriptor = ObjC.Sel("renderPassDescriptor"), setTexture = ObjC.Sel("setTexture:"),
            setLoadAction = ObjC.Sel("setLoadAction:"), setStoreAction = ObjC.Sel("setStoreAction:"),
            blitCommandEncoder = ObjC.Sel("blitCommandEncoder"), synchronizeResource = ObjC.Sel("synchronizeResource:"),
            waitUntilCompleted = ObjC.Sel("waitUntilCompleted");
    }
}

/// <summary>Our GPU renderers (Direct3D 11 on Windows, Metal on macOS), as the video window uses them.</summary>
internal interface IVideoRenderer : IDisposable
{
    string Name { get; }
    bool HasPicture { get; }
    (int W, int H) OutputSize { get; }
    void Upload(DecodedPicture p);
    void Render(int sharpenLevel, VideoScaling scaling, IVideoOverlay? overlay, Func<IVideoOverlay, byte[]?>? overlayPixels,
        Action<byte[], int, int>? screenshot, Func<int, int, int, int, (int X, int Y, int W, int H)> fit);
}

[StructLayout(LayoutKind.Sequential)] internal struct CGSize { public double W, H; }
[StructLayout(LayoutKind.Sequential)] internal struct ClearColor { public double R, G, B, A; }
[StructLayout(LayoutKind.Sequential)] internal struct MtlViewport { public double X, Y, W, H, ZNear, ZFar; }
[StructLayout(LayoutKind.Sequential)] internal struct Region { public nuint X, Y, Z, W, H, D; }

/// <summary>Minimal Objective-C runtime access: typed objc_msgSend calls (each signature its own function pointer).</summary>
internal static unsafe class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    [DllImport(Lib)] private static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] private static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);
    private static readonly IntPtr Send = NativeLibrary.GetExport(NativeLibrary.Load(Lib), "objc_msgSend");
    private static readonly IntPtr SelRelease = sel_registerName("release"), SelUtf8 = sel_registerName("UTF8String"),
        SelStringWithUtf8 = sel_registerName("stringWithUTF8String:"), SelDescription = sel_registerName("localizedDescription"),
        SelTexDesc = sel_registerName("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
        SelReplace = sel_registerName("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
        SelGetBytes = sel_registerName("getBytes:bytesPerRow:fromRegion:mipmapLevel:"),
        SelViewport = sel_registerName("setViewport:"), SelClear = sel_registerName("setClearColor:"),
        SelBytes = sel_registerName("setFragmentBytes:length:atIndex:"),
        SelDraw = sel_registerName("drawPrimitives:vertexStart:vertexCount:");

    public static IntPtr Class(string name) => objc_getClass(name);
    public static IntPtr Sel(string name) => sel_registerName(name);

    public static IntPtr Call(IntPtr o, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Send)(o, s);
    public static IntPtr Call(IntPtr o, IntPtr s, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr>)Send)(o, s, a);
    public static IntPtr Call(IntPtr o, IntPtr s, IntPtr a, IntPtr* err) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)Send)(o, s, a, err);
    public static IntPtr Call(IntPtr o, IntPtr s, IntPtr a, IntPtr b, IntPtr* err) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr>)Send)(o, s, a, b, err);
    public static IntPtr CallU(IntPtr o, IntPtr s, nuint a) => ((delegate* unmanaged<IntPtr, IntPtr, nuint, IntPtr>)Send)(o, s, a);
    public static nuint CallU(IntPtr o, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, nuint>)Send)(o, s);
    public static double CallD(IntPtr o, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, double>)Send)(o, s);
    public static void Void(IntPtr o, IntPtr s, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)Send)(o, s, a);
    public static void VoidU(IntPtr o, IntPtr s, nuint a) => ((delegate* unmanaged<IntPtr, IntPtr, nuint, void>)Send)(o, s, a);
    public static void VoidB(IntPtr o, IntPtr s, bool a) => ((delegate* unmanaged<IntPtr, IntPtr, byte, void>)Send)(o, s, a ? (byte)1 : (byte)0);
    public static void VoidPU(IntPtr o, IntPtr s, IntPtr a, nuint b) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint, void>)Send)(o, s, a, b);
    public static void VoidSize(IntPtr o, IntPtr s, CGSize size) => ((delegate* unmanaged<IntPtr, IntPtr, CGSize, void>)Send)(o, s, size);
    public static void SetViewport(IntPtr enc, MtlViewport vp) => ((delegate* unmanaged<IntPtr, IntPtr, MtlViewport, void>)Send)(enc, SelViewport, vp);
    public static void SetClearColor(IntPtr ca, ClearColor c) => ((delegate* unmanaged<IntPtr, IntPtr, ClearColor, void>)Send)(ca, SelClear, c);
    public static void SetFragmentBytes(IntPtr enc, void* bytes, nuint len, nuint index) =>
        ((delegate* unmanaged<IntPtr, IntPtr, void*, nuint, nuint, void>)Send)(enc, SelBytes, bytes, len, index);
    public static void Draw(IntPtr enc, nuint type, nuint start, nuint count) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint, nuint, nuint, void>)Send)(enc, SelDraw, type, start, count);
    public static IntPtr TextureDescriptor(nuint format, nuint w, nuint h) =>
        ((delegate* unmanaged<IntPtr, IntPtr, nuint, nuint, nuint, byte, IntPtr>)Send)(Class("MTLTextureDescriptor"), SelTexDesc, format, w, h, 0);
    public static void ReplaceRegion(IntPtr tex, Region region, byte* bytes, nuint bytesPerRow) =>
        ((delegate* unmanaged<IntPtr, IntPtr, Region, nuint, byte*, nuint, void>)Send)(tex, SelReplace, region, 0, bytes, bytesPerRow);
    public static void GetBytes(IntPtr tex, byte* bytes, nuint bytesPerRow, Region region) =>
        ((delegate* unmanaged<IntPtr, IntPtr, byte*, nuint, Region, nuint, void>)Send)(tex, SelGetBytes, bytes, bytesPerRow, region, 0);

    public static void Release(IntPtr o) { if (o != IntPtr.Zero) Call(o, SelRelease); }
    public static IntPtr NSString(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes) return Call(Class("NSString"), SelStringWithUtf8, (IntPtr)p);
    }
    public static string String(IntPtr nsString) => nsString == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(Call(nsString, SelUtf8)) ?? "";
    public static string Describe(IntPtr nsError) => nsError == IntPtr.Zero ? "unknown error" : String(Call(nsError, SelDescription));
}
