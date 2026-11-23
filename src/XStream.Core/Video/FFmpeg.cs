using System.Runtime.InteropServices;
using XStream.Core.Native;

namespace XStream.Core.Video;

/// <summary>
/// The small slice of FFmpeg (libavcodec / libavutil, versions 6.x to 8.x) XStream calls.
/// Struct fields are never read through version-specific layouts except a few that have been
/// fixed for many releases (AVFrame planes/size/format, AVPacket data/size); the codec context's
/// hardware-device field is located through the AVOption table at runtime.
/// </summary>
internal static unsafe class FF
{
    static FF() => NativeLibs.Ensure();

    private const string Codec = "avcodec", Util = "avutil";

    [DllImport(Codec)] public static extern uint avcodec_version();
    [DllImport(Codec)] public static extern IntPtr avcodec_find_decoder_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Codec)] public static extern IntPtr avcodec_alloc_context3(IntPtr codec);
    [DllImport(Codec)] public static extern int avcodec_open2(IntPtr ctx, IntPtr codec, IntPtr options);
    [DllImport(Codec)] public static extern void avcodec_free_context(ref IntPtr ctx);
    [DllImport(Codec)] public static extern int avcodec_send_packet(IntPtr ctx, IntPtr pkt);
    [DllImport(Codec)] public static extern int avcodec_receive_frame(IntPtr ctx, IntPtr frame);
    [DllImport(Codec)] public static extern void avcodec_flush_buffers(IntPtr ctx);
    [DllImport(Codec)] public static extern IntPtr av_packet_alloc();
    [DllImport(Codec)] public static extern void av_packet_free(ref IntPtr pkt);
    [DllImport(Codec)] public static extern int av_new_packet(IntPtr pkt, int size);
    [DllImport(Codec)] public static extern void av_packet_unref(IntPtr pkt);

    [DllImport(Util)] public static extern uint avutil_version();
    [DllImport(Util)] public static extern IntPtr av_frame_alloc();
    [DllImport(Util)] public static extern void av_frame_free(ref IntPtr frame);
    [DllImport(Util)] public static extern void av_frame_unref(IntPtr frame);
    [DllImport(Util)] public static extern int av_hwdevice_find_type_by_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Util)] public static extern int av_hwdevice_ctx_create(out IntPtr deviceCtx, int type, IntPtr device, IntPtr opts, int flags);
    [DllImport(Util)] public static extern IntPtr av_hwdevice_ctx_alloc(int type);
    [DllImport(Util)] public static extern int av_hwdevice_ctx_init(IntPtr deviceCtx);
    [DllImport(Util)] public static extern void av_frame_move_ref(IntPtr dst, IntPtr src);
    [DllImport(Util)] public static extern IntPtr av_buffer_ref(IntPtr buf);
    [DllImport(Util)] public static extern void av_buffer_unref(ref IntPtr buf);
    [DllImport(Util)] public static extern int av_hwframe_transfer_data(IntPtr dst, IntPtr src, int flags);
    [DllImport(Util)] public static extern IntPtr av_get_pix_fmt_name(int fmt);
    [DllImport(Util)] public static extern int av_opt_set(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val, int searchFlags);
    [DllImport(Util)] public static extern IntPtr av_opt_find(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr unit, int optFlags, int searchFlags);
    [DllImport(Util)] public static extern int av_strerror(int err, byte* buf, nuint size);
    [DllImport(Util)] public static extern void av_log_set_level(int level);

    // Stable leading fields; format is a pixel format for video, a sample format for audio. (unchanged from FFmpeg 4 through 8).
    public static byte** FrameData(IntPtr f) => (byte**)f;
    public static int* FrameLinesize(IntPtr f) => (int*)((byte*)f + 64);
    public static int FrameWidth(IntPtr f) => *(int*)((byte*)f + 104);
    public static int FrameHeight(IntPtr f) => *(int*)((byte*)f + 108);
    public static int FrameSamples(IntPtr f) => *(int*)((byte*)f + 112); // nb_samples (audio)
    public static int FrameFormat(IntPtr f) => *(int*)((byte*)f + 116);
    public static byte** PacketData(IntPtr p) => (byte**)((byte*)p + 24);

    /// <summary>
    /// AVD3D11VADeviceContext of a D3D11VA hardware device: AVBufferRef.data is the AVHWDeviceContext,
    /// whose hwctx pointer follows av_class and type (FFmpeg 7+, after the internal field was removed).
    /// Its first field is the ID3D11Device.
    /// </summary>
    public static IntPtr* D3D11DeviceSlot(IntPtr deviceRef)
    {
        byte* hwDeviceCtx = *(byte**)((byte*)deviceRef + IntPtr.Size); // AVBufferRef { buffer, data, size }
        byte* hwctx = *(byte**)(hwDeviceCtx + 2 * IntPtr.Size);       // { av_class, type (+pad), hwctx, ... }
        return (IntPtr*)hwctx;
    }

    public static string PixFmtName(int fmt) => Marshal.PtrToStringUTF8(av_get_pix_fmt_name(fmt)) ?? $"fmt{fmt}";

    public static string Error(int err)
    {
        byte* buf = stackalloc byte[256];
        av_strerror(err, buf, 256);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? err.ToString();
    }

    /// <summary>
    /// Offset of AVCodecContext.hw_device_ctx. It sits directly before hwaccel_flags (an AVOption,
    /// so its offset is published) in every FFmpeg release since 4.0.
    /// </summary>
    public static int HwDeviceCtxOffset(IntPtr codecCtx)
    {
        const int AV_OPT_SEARCH_CHILDREN = 1;
        var opt = av_opt_find(codecCtx, "hwaccel_flags", IntPtr.Zero, 0, 0 & AV_OPT_SEARCH_CHILDREN);
        if (opt == IntPtr.Zero) return -1;
        int offset = *(int*)((byte*)opt + 2 * IntPtr.Size); // AVOption { name, help, offset, ... }
        return offset - IntPtr.Size;
    }
}
