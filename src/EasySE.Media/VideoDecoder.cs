using FFmpeg.AutoGen;

namespace EasySE.Media;

/// <summary>Thread-confined decoder with a held frame and one look-ahead frame.</summary>
public sealed unsafe class VideoDecoder : IDisposable
{
    private readonly MediaInput input;
    private AVCodecContext* codec;
    private AVPacket* packet;
    private AVFrame* decoded;
    private AVFrame* current;
    private AVFrame* next;
    private SwsContext* scaler;
    private BgraBuffer? outputBuffer;
    private bool draining, eof, hasCurrent, hasNext, changed;
    private double currentTime, nextTime;
    private VideoFrame? cached;
    private readonly int rawWidth, rawHeight;
    public MediaInfo Info => input.Info;
    public int SeekCount { get; private set; }
    public int DecodedFrameCount { get; private set; }
    internal bool PreserveSourceColor { get; init; }
    internal void SeekTo(double seconds) => Seek(seconds);
    public VideoDecoder(string path, int maxWidth = 1920, int maxHeight = 1080)
    {
        input = new(path);
        try
        {
            codec = input.OpenCodec(input.VideoIndex); packet = ffmpeg.av_packet_alloc();
            decoded = ffmpeg.av_frame_alloc(); current = ffmpeg.av_frame_alloc(); next = ffmpeg.av_frame_alloc();
            if (packet == null || decoded == null || current == null || next == null) throw new OutOfMemoryException();
            var scale = Math.Min(1, Math.Min(maxWidth / (double)Info.Width, maxHeight / (double)Info.Height));
            rawWidth = Math.Max(1, (int)Math.Round(codec->width * scale)); rawHeight = Math.Max(1, (int)Math.Round(codec->height * scale));
            outputBuffer = new BgraBuffer(rawWidth, rawHeight);
        }
        catch { Dispose(); throw; }
    }
    public VideoFrame At(double seconds, CancellationToken cancellation = default)
    {
        Locate(seconds, cancellation);
        return CurrentPixels();
    }
    internal ExportPicture AtForExport(double seconds, NativeEncoding? encoding, CancellationToken cancellation)
    {
        Locate(seconds, cancellation);
        // Skip RGB conversion only when format, dimensions and color interpretation agree.
        // Rotated, high-bit-depth, differently ranged/colored frames keep the proven BGRA path.
        if (encoding is not null && Info.Rotation == 0 && current->width == rawWidth && current->height == rawHeight &&
            (current->format == (int)encoding.Format || current->format == (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P &&
                encoding.Format == AVPixelFormat.AV_PIX_FMT_YUV420P && encoding.Range == AVColorRange.AVCOL_RANGE_JPEG) &&
            current->color_range == encoding.Range && current->colorspace == encoding.Space &&
            current->color_primaries == encoding.Primaries && current->color_trc == encoding.Transfer)
            return new ExportPicture(current, currentTime);
        return new ExportPicture(CurrentPixels());
    }
    private VideoFrame CurrentPixels()
    {
        if (cached is null || changed) { cached = ConvertCurrent(); changed = false; }
        return cached;
    }
    private void Locate(double seconds, CancellationToken cancellation)
    {
        if (codec == null) throw new ObjectDisposedException(nameof(VideoDecoder));
        cancellation.ThrowIfCancellationRequested();
        seconds = Math.Clamp(seconds, 0, Info.Duration);
        if (hasCurrent && (seconds + .00001 < currentTime || seconds - currentTime > .75)) Seek(seconds);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (hasNext)
            {
                if (nextTime > seconds + .000001) break;
                ffmpeg.av_frame_unref(current); ffmpeg.av_frame_move_ref(current, next);
                currentTime = nextTime; hasCurrent = true; hasNext = false; changed = true;
            }
            if (!ReadNative(cancellation)) break;
            var pts = decoded->best_effort_timestamp;
            var time = pts == ffmpeg.AV_NOPTS_VALUE ? hasCurrent ? currentTime + 1 / Info.FrameRate : 0 : pts * ffmpeg.av_q2d(input.Format->streams[input.VideoIndex]->time_base) - Info.Origin;
            time = Math.Max(0, time);
            if (time <= seconds + .000001 || !hasCurrent)
            {
                ffmpeg.av_frame_unref(current); ffmpeg.av_frame_move_ref(current, decoded);
                currentTime = time; hasCurrent = true; changed = true;
            }
            else { ffmpeg.av_frame_unref(next); ffmpeg.av_frame_move_ref(next, decoded); nextTime = time; hasNext = true; break; }
        }
        if (!hasCurrent) throw new InvalidOperationException("视频没有可解码的画面");
    }
    private bool ReadNative(CancellationToken cancellation)
    {
        if (eof) return false;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = ffmpeg.avcodec_receive_frame(codec, decoded);
            if (result >= 0) { DecodedFrameCount++; return true; }
            if (result == ffmpeg.AVERROR_EOF) { eof = true; return false; }
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) NativeRuntime.Check(result, "解码视频帧");
            if (draining) { eof = true; return false; }
            do
            {
                ffmpeg.av_packet_unref(packet); result = ffmpeg.av_read_frame(input.Format, packet);
                if (result < 0)
                {
                    if (result != ffmpeg.AVERROR_EOF) NativeRuntime.Check(result, "读取视频数据");
                    NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, null), "完成视频解码"); draining = true; break;
                }
                cancellation.ThrowIfCancellationRequested();
            } while (packet->stream_index != input.VideoIndex);
            if (!draining) NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, packet), "提交视频数据");
        }
    }
    private void Seek(double seconds)
    {
        var stream = input.Format->streams[input.VideoIndex];
        var timestamp = (long)Math.Floor((seconds + Info.Origin) / ffmpeg.av_q2d(stream->time_base));
        NativeRuntime.Check(ffmpeg.av_seek_frame(input.Format, input.VideoIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD), "定位视频");
        ffmpeg.avcodec_flush_buffers(codec); ffmpeg.av_frame_unref(current); ffmpeg.av_frame_unref(next); ffmpeg.av_frame_unref(decoded);
        hasCurrent = hasNext = eof = draining = false; changed = true; SeekCount++;
    }
    private VideoFrame ConvertCurrent()
    {
        scaler = ffmpeg.sws_getCachedContext(scaler, current->width, current->height, (AVPixelFormat)current->format, rawWidth, rawHeight,
            AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (scaler == null) throw new InvalidOperationException("无法转换视频颜色");
        if (PreserveSourceColor)
        {
            var coefficients = ffmpeg.sws_getCoefficients(current->colorspace == AVColorSpace.AVCOL_SPC_BT709 ? 1 : 5);
            NativeRuntime.Check(ffmpeg.sws_setColorspaceDetails(scaler, *(int_array4*)coefficients,
                current->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, *(int_array4*)coefficients, 1, 0, 1 << 16, 1 << 16), "设置拼接解码色彩");
        }
        // Packed rows (e.g. 510 * 4 bytes) do not satisfy swscale SIMD output padding.
        // Convert into an aligned AVFrame, then copy only visible pixels into managed memory.
        var destination = outputBuffer!.Frame;
        NativeRuntime.Check(ffmpeg.sws_scale(scaler, current->data, current->linesize, 0, current->height, destination->data, destination->linesize), "转换视频画面");
        var pixels = outputBuffer.CopyPixels();
        if (Info.Rotation == 0) return new(pixels, rawWidth, rawHeight, currentTime);
        var width = Info.Rotation % 180 == 0 ? rawWidth : rawHeight; var height = Info.Rotation % 180 == 0 ? rawHeight : rawWidth;
        var rotated = new byte[pixels.Length];
        fixed (byte* src = pixels) fixed (byte* dst = rotated)
        {
            for (var y = 0; y < rawHeight; y++) for (var x = 0; x < rawWidth; x++)
            {
                var (dx, dy) = Info.Rotation switch { 90 => (rawHeight - 1 - y, x), 180 => (rawWidth - 1 - x, rawHeight - 1 - y), _ => (y, rawWidth - 1 - x) };
                ((uint*)dst)[dy * width + dx] = ((uint*)src)[y * rawWidth + x];
            }
        }
        return new(rotated, width, height, currentTime);
    }
    public void Dispose()
    {
        outputBuffer?.Dispose(); outputBuffer = null;
        if (scaler != null) { ffmpeg.sws_freeContext(scaler); scaler = null; }
        var p = packet; if (p != null) ffmpeg.av_packet_free(&p); packet = null;
        var f = decoded; if (f != null) ffmpeg.av_frame_free(&f); decoded = null;
        f = current; if (f != null) ffmpeg.av_frame_free(&f); current = null;
        f = next; if (f != null) ffmpeg.av_frame_free(&f); next = null;
        var c = codec; if (c != null) ffmpeg.avcodec_free_context(&c); codec = null;
        input.Dispose();
    }
}
