using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace XStream.Core.Video;

/// <summary>
/// XStream's own Direct3D 11 renderer for Windows (the ROG Ally), replacing SDL's renderer for the video:
///  - Zero-copy: the hardware decoder decodes on this device, and each picture is copied GPU-to-GPU from
///    the decoder's texture into ours (about 0.1 ms) instead of GPU -> system memory -> GPU every frame.
///  - Clarity boost runs in the pixel shader (same halo-free formula as <see cref="Sharpen"/>), so it costs
///    the CPU nothing.
///  - BT.709 limited-range colour conversion in the shader, Fit / Fill / Stretch by viewport and UV rect,
///    and the menu overlay alpha-blended on top.
/// Flip-model swap chain, at most one queued frame. All calls on the window's (main) thread, except that
/// the decoder uses the same device from its own thread (the device is multithread-protected).
/// </summary>
internal sealed unsafe class D3D11Renderer : IVideoRenderer
{
    public float ColorFlags { get; set; }
    private const string Shaders = """
        Texture2D<float>  texY  : register(t0);
        Texture2D<float2> texUV : register(t1);
        Texture2D<float>  texU  : register(t1);
        Texture2D<float>  texV  : register(t2);
        Texture2D<float4> texOv : register(t0);
        SamplerState samp : register(s0);
        cbuffer Params : register(b0)
        {
            float2 texel;     // one source pixel in UV units
            float  sharpen;   // k/16 (0 = off)
            float  pad;
            float2 uvScale;   // Fill crops the picture: uv = uvOffset + uv * uvScale
            float2 uvOffset;
            float2 foot;      // the source area one screen pixel covers, in UV units
            float2 pad2;
        };
        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        // One triangle covering the viewport; no vertex buffer.
        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.uv = uv;
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }

        // Clarity boost: c + k/16 * (4c - up - down - left - right), clamped to those five pixels' range
        // (steeper edges, no halos), exactly like the CPU version.
        float Luma(float2 uv)
        {
            float c = texY.SampleLevel(samp, uv, 0);
            if (sharpen <= 0) return c;
            float n = texY.SampleLevel(samp, uv + float2(0, -texel.y), 0);
            float s = texY.SampleLevel(samp, uv + float2(0,  texel.y), 0);
            float e = texY.SampleLevel(samp, uv + float2( texel.x, 0), 0);
            float w = texY.SampleLevel(samp, uv + float2(-texel.x, 0), 0);
            float lo = min(c, min(min(n, s), min(e, w)));
            float hi = max(c, max(max(n, s), max(e, w)));
            return clamp(c + sharpen * (4 * c - n - s - e - w), lo, hi);
        }

        // YUV -> RGB as the stream's SPS says: pad bit 0 = full range (else 16-235 luma, 16-240 chroma),
        // bit 1 = BT.601 matrix (else BT.709). The console normally sends BT.709 limited range.
        float4 ToRgb(float y, float2 cbcr)
        {
            int f = (int)(pad + 0.5);
            if ((f & 1) == 0) { y = (y - 16.0 / 255.0) * (255.0 / 219.0); cbcr = (cbcr - 128.0 / 255.0) * (255.0 / 224.0); }
            else cbcr = cbcr - 128.0 / 255.0;
            float3 rgb = (f & 2) != 0
                ? float3(y + 1.402 * cbcr.y, y - 0.344136 * cbcr.x - 0.714136 * cbcr.y, y + 1.772 * cbcr.x)
                : float3(y + 1.5748 * cbcr.y, y - 0.1873 * cbcr.x - 0.4681 * cbcr.y, y + 1.8556 * cbcr.x);
            return float4(saturate(rgb), 1);
        }

        // Shrinking the picture (1440p on a 1080p screen): one bilinear tap per screen pixel skips source pixels
        // and breaks thin lines and text into jagged bits. Average an n x n grid of taps spread over the area the
        // screen pixel covers instead (n grows with the shrink); 1:1 and enlarging stay one tap.
        int Taps(float f) { return f <= 1.02 ? 1 : f < 1.6 ? 2 : f < 2.6 ? 3 : 4; }

        float4 PSNv12(VSOut i) : SV_Target
        {
            float2 uv = uvOffset + i.uv * uvScale;
            int nx = Taps(foot.x / texel.x), ny = Taps(foot.y / texel.y);
            if (nx == 1 && ny == 1) return ToRgb(Luma(uv), texUV.SampleLevel(samp, uv, 0));
            float y = 0; float2 c = 0;
            for (int a = 0; a < ny; a++)
                for (int b = 0; b < nx; b++)
                {
                    float2 q = uv + foot * (float2((b + 0.5) / nx, (a + 0.5) / ny) - 0.5);
                    y += Luma(q); c += texUV.SampleLevel(samp, q, 0);
                }
            float n = nx * ny;
            return ToRgb(y / n, c / n);
        }

        float4 PSI420(VSOut i) : SV_Target
        {
            float2 uv = uvOffset + i.uv * uvScale;
            int nx = Taps(foot.x / texel.x), ny = Taps(foot.y / texel.y);
            if (nx == 1 && ny == 1) return ToRgb(Luma(uv), float2(texU.SampleLevel(samp, uv, 0), texV.SampleLevel(samp, uv, 0)));
            float y = 0; float2 c = 0;
            for (int a = 0; a < ny; a++)
                for (int b = 0; b < nx; b++)
                {
                    float2 q = uv + foot * (float2((b + 0.5) / nx, (a + 0.5) / ny) - 0.5);
                    y += Luma(q); c += float2(texU.SampleLevel(samp, q, 0), texV.SampleLevel(samp, q, 0));
                }
            float n = nx * ny;
            return ToRgb(y / n, c / n);
        }

        float4 PSOverlay(VSOut i) : SV_Target { return texOv.Sample(samp, i.uv); }
        """;

    /// <summary>XSTREAM_SCALE=simple: one tap per screen pixel when shrinking (the old way; for comparison).</summary>
    private static readonly bool SimpleScaling = Environment.GetEnvironmentVariable("XSTREAM_SCALE") == "simple";

    [StructLayout(LayoutKind.Sequential)]
    private struct Params { public float TexelX, TexelY, Sharpen, Pad, ScaleX, ScaleY, OffsetX, OffsetY, FootX, FootY, Pad2, Pad3; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RawRect rect);
    [StructLayout(LayoutKind.Sequential)] private struct RawRect { public int Left, Top, Right, Bottom; }

    private readonly IntPtr _hwnd;
    private readonly bool _vsync;
    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _ctx = null!;
    private IDXGISwapChain1 _swap = null!;
    // With VSync off, show each frame the moment it's ready. Without this a flip-model swap chain still waits for the
    // next refresh (half a refresh on average: ~8 ms at 60 Hz) even with a sync interval of 0.
    private bool _tearing;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11VertexShader _vs = null!;
    private ID3D11PixelShader _psNv12 = null!, _psI420 = null!, _psOverlay = null!;
    private ID3D11SamplerState _sampler = null!;
    private ID3D11BlendState _blend = null!;
    private ID3D11Buffer _params = null!;
    private int _outW, _outH;

    // The current picture: NV12 (GPU copy target, or uploaded NV12) or three planes (uploaded I420).
    private ID3D11Texture2D? _nv12;           // Default usage, filled by CopySubresourceRegion from the decoder
    private ID3D11Texture2D? _y, _uv, _u, _v; // Dynamic, filled from system memory (software decode)
    private ID3D11ShaderResourceView? _srvY, _srvUV, _srvU, _srvV;
    private PlaneLayout _layout = PlaneLayout.Unsupported;
    private int _w, _h;

    private ID3D11Texture2D? _ovTex;
    private ID3D11ShaderResourceView? _ovSrv;
    private int _ovW, _ovH;

    public string Name { get; private set; } = "direct3d11";
    /// <summary>Native ID3D11Device pointer, for the decoder to decode on.</summary>
    public IntPtr DevicePointer => _device.NativePointer;
    public (int W, int H) OutputSize => (_outW, _outH);
    public bool HasPicture => _layout != PlaneLayout.Unsupported;

    private D3D11Renderer(IntPtr hwnd, bool vsync) { _hwnd = hwnd; _vsync = vsync; }

    public static D3D11Renderer? TryCreate(IntPtr hwnd, bool vsync, out string? error)
    {
        var r = new D3D11Renderer(hwnd, vsync);
        try { r.Init(); error = null; return r; }
        catch (Exception ex) { error = ex.Message; r.Dispose(); return null; }
    }

    private void Init()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        // VideoSupport lets the hardware decoder use this device; retry without it (and on WARP) if refused.
        var attempts = new (DriverType type, DeviceCreationFlags flags)[]
        {
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport),
            (DriverType.Hardware, DeviceCreationFlags.BgraSupport),
            (DriverType.Warp, DeviceCreationFlags.BgraSupport),
        };
        Exception? last = null;
        foreach (var (type, flags) in attempts)
        {
            try
            {
                D3D11.D3D11CreateDevice((IDXGIAdapter?)null, type, flags, levels, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
                _device = dev!; _ctx = ctx!;
                if (type == DriverType.Warp) Name = "direct3d11 (WARP software)";
                break;
            }
            catch (Exception ex) { last = ex; }
        }
        if (_device is null) throw new InvalidOperationException("No Direct3D 11 device: " + last?.Message);

        // The decoder thread uses this device too.
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>()) mt?.SetMultithreadProtected(true);

        using (var dxgiDevice = _device.QueryInterface<IDXGIDevice1>())
        {
            dxgiDevice.MaximumFrameLatency = 1; // never queue frames behind the newest one
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var desc = new SwapChainDescription1
            {
                Width = 0, Height = 0, // from the window
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                SwapEffect = SwapEffect.FlipDiscard,
                Scaling = Scaling.Stretch,
                AlphaMode = AlphaMode.Ignore,
            };
            using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
                _tearing = !_vsync && f5 is not null && f5.PresentAllowTearing;
            if (_tearing) desc.Flags = SwapChainFlags.AllowTearing;
            _swap = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter); // full screen is SDL's borderless window
            using var adapterDesc = adapter.QueryInterfaceOrNull<IDXGIAdapter1>();
            if (adapterDesc is not null) Name += " - " + adapterDesc.Description1.Description.Trim();
            if (_tearing) Name += ", immediate present";
        }
        CreateTarget();

        var vs = Compile("VSMain", "vs_4_0");
        _vs = _device.CreateVertexShader(vs.Span);
        _psNv12 = _device.CreatePixelShader(Compile("PSNv12", "ps_4_0").Span);
        _psI420 = _device.CreatePixelShader(Compile("PSI420", "ps_4_0").Span);
        _psOverlay = _device.CreatePixelShader(Compile("PSOverlay", "ps_4_0").Span);
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha, DestinationBlend = Blend.InverseSourceAlpha, BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One, DestinationBlendAlpha = Blend.InverseSourceAlpha, BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        _blend = _device.CreateBlendState(blend);
        _params = _device.CreateBuffer(new BufferDescription((uint)sizeof(Params), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    }

    private static ReadOnlyMemory<byte> Compile(string entry, string profile) =>
        Compiler.Compile(Shaders, entry, "xstream.hlsl", profile, ShaderFlags.OptimizationLevel3);

    private void CreateTarget()
    {
        GetClientRect(_hwnd, out var rc);
        _outW = Math.Max(1, rc.Right - rc.Left);
        _outH = Math.Max(1, rc.Bottom - rc.Top);
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(back);
    }

    /// <summary>Follow the window's size (full screen toggles, resizes).</summary>
    private void SyncSize()
    {
        GetClientRect(_hwnd, out var rc);
        int w = Math.Max(1, rc.Right - rc.Left), h = Math.Max(1, rc.Bottom - rc.Top);
        if (w == _outW && h == _outH) return;
        _ctx.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        _rtv?.Dispose(); _rtv = null;
        _swap.ResizeBuffers(0, (uint)w, (uint)h, Format.Unknown, _tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();
        CreateTarget();
    }

    // ---- Pictures -----------------------------------------------------------------------------------

    /// <summary>Take the newest decoded picture: a GPU copy for hardware frames, an upload for software ones.</summary>
    public void Upload(DecodedPicture p)
    {
        switch (p.Layout)
        {
            case PlaneLayout.D3D11:
            {
                EnsureNv12Target(p.Width, p.Height);
                var (texPtr, slice) = p.D3D11Surface;
                // Borrow the decoder's texture: add a reference for the wrapper so disposing it is balanced.
                Marshal.AddRef(texPtr);
                using var src = new ID3D11Texture2D(texPtr);
                var box = new Box(0, 0, 0, p.Width & ~1, p.Height & ~1, 1);
                _ctx.CopySubresourceRegion(_nv12!, 0, 0, 0, 0, src, (uint)slice, box);
                break;
            }
            case PlaneLayout.Nv12:
                EnsurePlanes(PlaneLayout.Nv12, p.Width, p.Height);
                Write(_y!, p.Plane(0), p.Pitch(0), p.Width, p.Height);
                Write(_uv!, p.Plane(1), p.Pitch(1), p.Width / 2 * 2, p.Height / 2); // R8G8: 2 bytes per chroma pixel
                break;
            case PlaneLayout.Yuv420p:
                EnsurePlanes(PlaneLayout.Yuv420p, p.Width, p.Height);
                Write(_y!, p.Plane(0), p.Pitch(0), p.Width, p.Height);
                Write(_u!, p.Plane(1), p.Pitch(1), p.Width / 2, p.Height / 2);
                Write(_v!, p.Plane(2), p.Pitch(2), p.Width / 2, p.Height / 2);
                break;
        }
    }

    private void EnsureNv12Target(int w, int h)
    {
        if (_layout == PlaneLayout.D3D11 && _w == w && _h == h) return;
        ReleasePicture();
        _nv12 = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)(w & ~1), Height = (uint)(h & ~1), MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _srvY = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
        _srvUV = _device.CreateShaderResourceView(_nv12, new ShaderResourceViewDescription(_nv12, ShaderResourceViewDimension.Texture2D, Format.R8G8_UNorm));
        _layout = PlaneLayout.D3D11; _w = w; _h = h;
    }

    private void EnsurePlanes(PlaneLayout layout, int w, int h)
    {
        if (_layout == layout && _w == w && _h == h) return;
        ReleasePicture();
        (_y, _srvY) = Plane(Format.R8_UNorm, w, h);
        if (layout == PlaneLayout.Nv12) (_uv, _srvUV) = Plane(Format.R8G8_UNorm, w / 2, h / 2);
        else { (_u, _srvU) = Plane(Format.R8_UNorm, w / 2, h / 2); (_v, _srvV) = Plane(Format.R8_UNorm, w / 2, h / 2); }
        _layout = layout; _w = w; _h = h;
    }

    private (ID3D11Texture2D, ID3D11ShaderResourceView) Plane(Format f, int w, int h)
    {
        var t = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = f, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic, BindFlags = BindFlags.ShaderResource, CPUAccessFlags = CpuAccessFlags.Write,
        });
        return (t, _device.CreateShaderResourceView(t));
    }

    private void Write(ID3D11Texture2D tex, byte* src, int srcPitch, int rowBytes, int rows)
    {
        var m = _ctx.Map(tex, 0, MapMode.WriteDiscard);
        byte* dst = (byte*)m.DataPointer;
        for (int y = 0; y < rows; y++) Buffer.MemoryCopy(src + (long)y * srcPitch, dst + (long)y * m.RowPitch, m.RowPitch, rowBytes);
        _ctx.Unmap(tex, 0);
    }

    // ---- Drawing ------------------------------------------------------------------------------------

    /// <summary>Draw the current picture (Fit / Fill / Stretch, clarity level 0-3) and the overlay, then present.</summary>
    public void Render(int sharpenLevel, VideoScaling scaling, IVideoOverlay? overlay, Func<IVideoOverlay, byte[]?>? overlayPixels,
        Action<byte[], int, int>? screenshot, Func<int, int, int, int, (int X, int Y, int W, int H)> fit)
    {
        SyncSize();
        _ctx.OMSetRenderTargets(_rtv!);
        _ctx.ClearRenderTargetView(_rtv!, new Color4(0, 0, 0, 1));
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetSampler(0, _sampler);

        (int X, int Y, int W, int H) videoRect = (0, 0, _outW, _outH);
        if (HasPicture)
        {
            var prm = new Params
            {
                TexelX = 1f / _w, TexelY = 1f / _h,
                Sharpen = Sharpen.Strength(sharpenLevel) / 16f, Pad = ColorFlags,
                ScaleX = 1, ScaleY = 1,
            };
            switch (scaling)
            {
                case VideoScaling.Fill:
                {
                    // Crop the picture to the screen's shape (centred), then cover the screen.
                    var src = fit(_outW, _outH, _w, _h);
                    prm.ScaleX = src.W / (float)_w; prm.ScaleY = src.H / (float)_h;
                    prm.OffsetX = src.X / (float)_w; prm.OffsetY = src.Y / (float)_h;
                    break;
                }
                case VideoScaling.Fit:
                    videoRect = fit(_w, _h, _outW, _outH);
                    break;
            }
            // How much of the picture one screen pixel covers (more than one source pixel when shrinking).
            if (!SimpleScaling) { prm.FootX = prm.ScaleX / Math.Max(1, videoRect.W); prm.FootY = prm.ScaleY / Math.Max(1, videoRect.H); }
            SetParams(prm);
            _ctx.RSSetViewport(new Viewport(videoRect.X, videoRect.Y, videoRect.W, videoRect.H));
            _ctx.OMSetBlendState(null);
            if (_layout == PlaneLayout.Yuv420p)
            {
                _ctx.PSSetShader(_psI420);
                _ctx.PSSetShaderResources(0, [_srvY!, _srvU!, _srvV!]);
            }
            else
            {
                _ctx.PSSetShader(_psNv12);
                _ctx.PSSetShaderResources(0, [_srvY!, _srvUV!]);
            }
            _ctx.Draw(3, 0);
        }

        if (screenshot is not null) Capture(screenshot, scaling == VideoScaling.Fit ? videoRect : (0, 0, _outW, _outH));

        if (overlay is { Visible: true })
        {
            if (_ovTex is null || _ovW != overlay.Width || _ovH != overlay.Height)
            {
                _ovSrv?.Dispose(); _ovTex?.Dispose();
                _ovW = overlay.Width; _ovH = overlay.Height;
                _ovTex = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)_ovW, Height = (uint)_ovH, MipLevels = 1, ArraySize = 1, Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
                _ovSrv = _device.CreateShaderResourceView(_ovTex);
                var px = overlayPixels?.Invoke(overlay) ?? null;
                if (px is not null) UploadOverlay(px);
            }
            else if (overlay.NeedsRender && overlayPixels?.Invoke(overlay) is { } px) UploadOverlay(px);
            var r = fit(overlay.Width, overlay.Height, _outW, _outH);
            _ctx.RSSetViewport(new Viewport(r.X, r.Y, r.W, r.H));
            _ctx.OMSetBlendState(_blend);
            _ctx.PSSetShader(_psOverlay);
            _ctx.PSSetShaderResources(0, [_ovSrv!]);
            _ctx.Draw(3, 0);
        }

        _swap.Present(_vsync ? 1u : 0u, _tearing ? PresentFlags.AllowTearing : PresentFlags.None);
        if (HasPicture) TrackPresent();
    }

    // ---- When frames actually reach the display ------------------------------------------------------
    // Like the Metal renderer's "On screen" line: DXGI frame statistics give the QPC time of the refresh that showed
    // a given present, on the same clock as Stopwatch. With tearing there's no refresh boundary to wait for, so the
    // figure is the time to the next scan-out DXGI reports (an upper bound for the torn part of the frame).
    private readonly Queue<(uint Count, long At)> _presents = new();
    private readonly double[] _glassMs = new double[4096];
    private long _glassCount, _statsFailures;
    private uint _lastMatched;
    private uint _lastSyncRefresh; private long _lastSyncQpc; private double _refreshSum; private long _refreshN;

    private void TrackPresent()
    {
        try
        {
            _presents.Enqueue((_swap.LastPresentCount, System.Diagnostics.Stopwatch.GetTimestamp()));
            while (_presents.Count > 32) _presents.Dequeue();
            if (_swap.GetFrameStatistics(out var st).Failure) { _statsFailures++; return; }
            if (st.PresentCount == _lastMatched) return;
            _lastMatched = st.PresentCount;
            foreach (var (count, at) in _presents)
                if (count == st.PresentCount)
                {
                    double ms = (st.SyncQPCTime - at) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    if (ms >= 0 && ms < 250) _glassMs[_glassCount++ % _glassMs.Length] = ms;
                    break;
                }
            if (_lastSyncRefresh != 0 && st.SyncRefreshCount > _lastSyncRefresh)
            {
                _refreshSum += (st.SyncQPCTime - _lastSyncQpc) * 1000.0 / System.Diagnostics.Stopwatch.Frequency / (st.SyncRefreshCount - _lastSyncRefresh);
                _refreshN++;
            }
            _lastSyncRefresh = st.SyncRefreshCount; _lastSyncQpc = st.SyncQPCTime;
        }
        catch { _statsFailures++; }
    }

    /// <summary>One line for the session log: hand-off to display, from DXGI frame statistics.</summary>
    public string PresentSummary
    {
        get
        {
            int k = (int)Math.Min(_glassCount, _glassMs.Length);
            if (k == 0) return $"On screen: no DXGI frame statistics ({_statsFailures} failed queries)";
            var v = _glassMs.Take(k).OrderBy(x => x).ToArray();
            string hz = _refreshN > 0 ? $"{1000 / (_refreshSum / _refreshN):F0} Hz" : "?";
            return $"On screen: present -> display p50 {v[k / 2]:F2} ms, p90 {v[(int)(k * 0.9)]:F2} ms, p99 {v[Math.Min(k - 1, (int)(k * 0.99))]:F2} ms " +
                   $"({k} frames sampled, display {hz}{(_tearing ? ", tearing" : "")}{(_vsync ? ", vsync" : "")})";
        }
    }

    private void UploadOverlay(byte[] rgba)
    {
        fixed (byte* p = rgba) _ctx.UpdateSubresource(_ovTex!, 0, null, (IntPtr)p, (uint)(_ovW * 4), 0);
    }

    private void SetParams(Params p)
    {
        var m = _ctx.Map(_params, 0, MapMode.WriteDiscard);
        *(Params*)m.DataPointer = p;
        _ctx.Unmap(_params, 0);
        _ctx.PSSetConstantBuffer(0, _params);
    }

    /// <summary>Read back what's been drawn so far (the picture, before the menu) as RGBA.</summary>
    private void Capture(Action<byte[], int, int> done, (int X, int Y, int W, int H) r)
    {
        r.W = Math.Min(r.W, _outW - r.X); r.H = Math.Min(r.H, _outH - r.Y);
        if (r.W <= 0 || r.H <= 0) return;
        using var back = _swap.GetBuffer<ID3D11Texture2D>(0);
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)r.W, Height = (uint)r.H, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, back, 0, new Box(r.X, r.Y, 0, r.X + r.W, r.Y + r.H, 1));
        var m = _ctx.Map(staging, 0, MapMode.Read);
        var px = new byte[r.W * r.H * 4];
        fixed (byte* dst = px)
        {
            for (int y = 0; y < r.H; y++)
            {
                byte* s = (byte*)m.DataPointer + (long)y * m.RowPitch;
                byte* d = dst + (long)y * r.W * 4;
                for (int x = 0; x < r.W; x++, s += 4, d += 4) { d[0] = s[2]; d[1] = s[1]; d[2] = s[0]; d[3] = 255; } // BGRA -> RGBA
            }
        }
        _ctx.Unmap(staging, 0);
        done(px, r.W, r.H);
    }

    private void ReleasePicture()
    {
        foreach (var d in new IDisposable?[] { _srvY, _srvUV, _srvU, _srvV, _nv12, _y, _uv, _u, _v }) d?.Dispose();
        _srvY = _srvUV = _srvU = _srvV = null;
        _nv12 = _y = _uv = _u = _v = null;
        _layout = PlaneLayout.Unsupported;
    }

    public void Dispose()
    {
        ReleasePicture();
        foreach (var d in new IDisposable?[] { _ovSrv, _ovTex, _rtv, _params, _blend, _sampler, _psOverlay, _psI420, _psNv12, _vs, _swap })
            try { d?.Dispose(); } catch { }
        try { _ctx?.ClearState(); _ctx?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
    }
}
