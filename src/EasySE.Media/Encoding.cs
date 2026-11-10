using FFmpeg.AutoGen;

namespace EasySE.Media;

internal unsafe delegate void PacketSink(AVPacket* packet, AVCodecContext* codec);
internal sealed record SpliceEncoding(int Profile, AVColorRange Range, AVColorSpace Space,
    AVColorPrimaries Primaries, AVColorTransferCharacteristic Transfer, AVChromaLocation Chroma);
internal sealed unsafe class H264Encoder : IDisposable
{
    internal const string Preset = "veryfast";
    public AVCodecContext* Context;
    private AVFrame* frame;
    private AVPacket* packet;
    private SwsContext* scaler;
    private BgraBuffer? inputBuffer;
    private readonly PacketSink? sink;
    internal long NativeFrames { get; private set; }
    internal long BgraFrames { get; private set; }
    internal NativeEncoding NativeFormat => new(Context->pix_fmt, Context->color_range, Context->colorspace,
        Context->color_primaries, Context->color_trc);
    public H264Encoder(int width, int height, long bitrate, int pass, string statistics, PacketSink? sink, SpliceEncoding? splice = null,
        SpliceEncoding? sourceColor = null)
    {
        this.sink = sink;
        try
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            Context = ffmpeg.avcodec_alloc_context3(codec); frame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
            if (Context == null || frame == null || packet == null) throw new OutOfMemoryException();
            Context->width = width; Context->height = height;
            Context->time_base = new() { num = 1, den = 30 }; Context->framerate = new() { num = 30, den = 1 };
            Context->sample_aspect_ratio = new() { num = 1, den = 1 };
            Context->pix_fmt = (width % 2 == 0 && height % 2 == 0) ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_YUV444P;
            Context->bit_rate = bitrate; Context->thread_count = Math.Clamp(Environment.ProcessorCount, 1, 8);
            Context->gop_size = 250; Context->max_b_frames = 3;
            Context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            if (pass == 1) Context->flags |= ffmpeg.AV_CODEC_FLAG_PASS1;
            else if (pass == 2) Context->flags |= ffmpeg.AV_CODEC_FLAG_PASS2;
            Context->color_range = AVColorRange.AVCOL_RANGE_MPEG; Context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            Context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709; Context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
            var color = splice ?? sourceColor;
            if (color is not null)
            {
                Context->color_range = color.Range; Context->colorspace = color.Space;
                Context->color_primaries = color.Primaries; Context->color_trc = color.Transfer;
                Context->chroma_sample_location = color.Chroma;
            }
            if (splice is not null)
            {
                // No local reordering. The splice writer applies the source track's constant
                // decode offset, so every replaced GOP occupies the same DTS interval.
                Context->max_b_frames = 0;
                Context->color_range = splice.Range; Context->colorspace = splice.Space;
                Context->color_primaries = splice.Primaries; Context->color_trc = splice.Transfer;
                Context->chroma_sample_location = splice.Chroma;
                NativeRuntime.Check(ffmpeg.av_opt_set(Context->priv_data, "profile", splice.Profile switch { 66 => "baseline", 77 => "main", _ => "high" }, 0), "设置拼接编码规格");
                NativeRuntime.Check(ffmpeg.av_opt_set(Context->priv_data, "x264-params", "open-gop=0:scenecut=0", 0), "设置闭合编码区段");
            }
            NativeRuntime.Check(ffmpeg.av_opt_set(Context->priv_data, "preset", Preset, 0), "设置编码预设");
            if (pass != 0) NativeRuntime.Check(ffmpeg.av_opt_set(Context->priv_data, "stats", statistics, 0), "设置双遍编码");
            NativeRuntime.Check(ffmpeg.avcodec_open2(Context, codec, null), "启动 H.264 编码器");
            frame->width = width; frame->height = height; frame->format = (int)Context->pix_fmt;
            NativeRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 32), "分配编码画面");
            inputBuffer = new BgraBuffer(width, height);
            scaler = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA, width, height, Context->pix_fmt, (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (scaler == null) throw new InvalidOperationException("无法初始化编码颜色转换");
            var coefficients = ffmpeg.sws_getCoefficients(color is null || color.Space == AVColorSpace.AVCOL_SPC_BT709 ? 1 : 5);
            NativeRuntime.Check(ffmpeg.sws_setColorspaceDetails(scaler, *(int_array4*)coefficients, 1, *(int_array4*)coefficients,
                color?.Range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, 0, 1 << 16, 1 << 16), "设置编码色彩");
        }
        catch { Dispose(); throw; }
    }
    public void Write(VideoFrame pixels, long index)
    {
        BgraFrames++;
        inputBuffer!.CopyFrom(pixels);
        NativeRuntime.Check(ffmpeg.av_frame_make_writable(frame), "准备编码画面");
        var input = inputBuffer.Frame;
        NativeRuntime.Check(ffmpeg.sws_scale(scaler, input->data, input->linesize, 0, pixels.Height, frame->data, frame->linesize), "转换编码画面");
        frame->pts = index;
        NativeRuntime.Check(ffmpeg.avcodec_send_frame(Context, frame), "编码视频画面"); Drain();
    }
    internal void Write(ExportPicture picture, long index)
    {
        if (picture.Native == null) { Write(picture.Pixels!, index); return; }
        // The cloned AVFrame owns its buffer references. x264 retains any references it needs;
        // disposing this queue item after send_frame cannot invalidate the encoder or decoder.
        var native = picture.Native;
        if (native->width != Context->width || native->height != Context->height)
            throw new InvalidDataException("原生导出画面尺寸不匹配");
        native->format = (int)Context->pix_fmt; native->pts = index;
        native->time_base = Context->time_base; native->duration = 1;
        native->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        NativeRuntime.Check(ffmpeg.avcodec_send_frame(Context, native), "编码原生视频画面");
        NativeFrames++; Drain();
    }
    public void Flush() { NativeRuntime.Check(ffmpeg.avcodec_send_frame(Context, null), "完成视频编码"); Drain(); }
    private void Drain()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(Context, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            NativeRuntime.Check(result, "读取视频编码结果");
            try { sink?.Invoke(packet, Context); } finally { ffmpeg.av_packet_unref(packet); }
        }
    }
    public void Dispose()
    {
        inputBuffer?.Dispose(); inputBuffer = null;
        if (scaler != null) { ffmpeg.sws_freeContext(scaler); scaler = null; }
        var f = frame; if (f != null) ffmpeg.av_frame_free(&f); frame = null;
        var p = packet; if (p != null) ffmpeg.av_packet_free(&p); packet = null;
        var c = Context; if (c != null) ffmpeg.avcodec_free_context(&c); Context = null;
    }
}

internal sealed unsafe class AacEncoder : IDisposable
{
    public AVCodecContext* Context;
    private AVFrame* frame;
    private AVPacket* packet;
    private readonly PacketSink sink;
    private readonly AudioTimeline timeline;
    private long position;
    public AacEncoder(string path, double duration, long bitrate, PacketSink sink)
    {
        this.sink = sink; timeline = new(path, duration);
        try
        {
            var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            Context = ffmpeg.avcodec_alloc_context3(codec); frame = ffmpeg.av_frame_alloc(); packet = ffmpeg.av_packet_alloc();
            if (Context == null || frame == null || packet == null) throw new OutOfMemoryException();
            Context->sample_rate = 48000; Context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            ffmpeg.av_channel_layout_default(&Context->ch_layout, 2);
            Context->bit_rate = bitrate; Context->time_base = new() { num = 1, den = 48000 }; Context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            NativeRuntime.Check(ffmpeg.avcodec_open2(Context, codec, null), "启动 AAC 编码器");
            frame->sample_rate = 48000; frame->format = (int)Context->sample_fmt; frame->nb_samples = Context->frame_size;
            NativeRuntime.Check(ffmpeg.av_channel_layout_copy(&frame->ch_layout, &Context->ch_layout), "设置导出音频声道");
            NativeRuntime.Check(ffmpeg.av_frame_get_buffer(frame, 0), "分配导出音频");
        }
        catch { Dispose(); throw; }
    }
    public void WriteUntil(double time, CancellationToken cancellation)
    {
        var target = (long)Math.Ceiling(time * 48000 - 1e-6);
        while (position < target)
        {
            cancellation.ThrowIfCancellationRequested();
            var pcm = timeline.Read(Context->frame_size, cancellation); if (pcm is null) return;
            NativeRuntime.Check(ffmpeg.av_frame_make_writable(frame), "准备导出音频");
            var samples = pcm.Length / 4;
            fixed (byte* data = pcm)
            {
                var source = (short*)data; var left = (float*)frame->data[0]; var right = (float*)frame->data[1];
                for (var i = 0; i < samples; i++) { left[i] = source[i * 2] / 32768f; right[i] = source[i * 2 + 1] / 32768f; }
            }
            frame->nb_samples = samples; frame->pts = position; position += samples;
            NativeRuntime.Check(ffmpeg.avcodec_send_frame(Context, frame), "编码导出音频"); Drain();
        }
    }
    public void Flush() { NativeRuntime.Check(ffmpeg.avcodec_send_frame(Context, null), "完成音频编码"); Drain(); }
    private void Drain()
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(Context, packet);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
            NativeRuntime.Check(result, "读取音频编码结果");
            try { sink(packet, Context); } finally { ffmpeg.av_packet_unref(packet); }
        }
    }
    public void Dispose()
    {
        timeline.Dispose();
        var f = frame; if (f != null) ffmpeg.av_frame_free(&f); frame = null;
        var p = packet; if (p != null) ffmpeg.av_packet_free(&p); packet = null;
        var c = Context; if (c != null) ffmpeg.avcodec_free_context(&c); Context = null;
    }
}

internal sealed unsafe class Mp4Muxer : IDisposable
{
    private AVFormatContext* format;
    private AVStream* video;
    private AVStream* audio;
    public Mp4Muxer(string path, AVCodecContext* videoCodec, AVStream* sourceAudio)
        : this(path, videoCodec, null, default, sourceAudio) { }
    public Mp4Muxer(string path, AVCodecParameters* sourceVideo, AVRational sourceTimeBase, AVStream* sourceAudio)
        : this(path, null, sourceVideo, sourceTimeBase, sourceAudio) { }
    private Mp4Muxer(string path, AVCodecContext* videoCodec, AVCodecParameters* sourceVideo, AVRational sourceTimeBase, AVStream* sourceAudio)
    {
        try
        {
            AVFormatContext* context = null; NativeRuntime.Check(ffmpeg.avformat_alloc_output_context2(&context, null, "mp4", path), "创建 MP4"); format = context;
            video = ffmpeg.avformat_new_stream(format, null); if (video == null) throw new OutOfMemoryException();
            if (sourceVideo != null)
            {
                video->time_base = sourceTimeBase; video->avg_frame_rate = new() { num = 30, den = 1 };
                NativeRuntime.Check(ffmpeg.avcodec_parameters_copy(video->codecpar, sourceVideo), "复制 MP4 视频规格");
                video->codecpar->codec_tag = 0;
            }
            else
            {
                video->time_base = videoCodec->time_base; video->avg_frame_rate = videoCodec->framerate;
                NativeRuntime.Check(ffmpeg.avcodec_parameters_from_context(video->codecpar, videoCodec), "设置 MP4 视频流");
            }
            if (sourceAudio != null)
            {
                audio = ffmpeg.avformat_new_stream(format, null); if (audio == null) throw new OutOfMemoryException();
                audio->time_base = sourceAudio->time_base;
                audio->disposition = sourceAudio->disposition;
                NativeRuntime.Check(ffmpeg.avcodec_parameters_copy(audio->codecpar, sourceAudio->codecpar), "复制 MP4 音频规格");
                audio->codecpar->codec_tag = 0;
                NativeRuntime.Check(ffmpeg.av_dict_copy(&audio->metadata, sourceAudio->metadata, 0), "复制音频元数据");
            }
            AVIOContext* io = null; NativeRuntime.Check(ffmpeg.avio_open(&io, path, ffmpeg.AVIO_FLAG_WRITE), "打开导出临时文件"); format->pb = io;
            AVDictionary* options = null;
            try { ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0); NativeRuntime.Check(ffmpeg.avformat_write_header(format, &options), "写入 MP4 文件头"); }
            finally { ffmpeg.av_dict_free(&options); }
        }
        catch { Dispose(); throw; }
    }
    public void Write(AVPacket* packet, AVCodecContext* codec, bool isAudio)
        => Write(packet, codec->time_base, isAudio);
    public void Write(AVPacket* packet, AVRational timeBase, bool isAudio)
    {
        var stream = isAudio ? audio : video;
        ffmpeg.av_packet_rescale_ts(packet, timeBase, stream->time_base);
        packet->stream_index = stream->index; packet->pos = -1;
        NativeRuntime.Check(ffmpeg.av_interleaved_write_frame(format, packet), "写入 MP4 数据");
    }
    public void Finish() { NativeRuntime.Check(ffmpeg.av_write_trailer(format), "完成 MP4 文件"); }
    public void Dispose()
    {
        if (format == null) return;
        var io = format->pb; if (io != null) ffmpeg.avio_closep(&io);
        ffmpeg.avformat_free_context(format); format = null; video = audio = null;
    }
}
