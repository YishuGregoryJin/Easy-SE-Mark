using EasySE.Core;
using FFmpeg.AutoGen;

namespace EasySE.Media;

internal static unsafe class SmartVideoExporter
{
    internal static void Render(ExportRequest request, MediaInfo info, SmartPlan plan, string output,
        IProgress<ExportProgress>? progress, CancellationToken cancellation, ExportLog log)
    {
        if (!plan.Eligible) throw new InvalidOperationException("无可用智能导出方案");
        using var input = new MediaInput(info.Path);
        var sourceStream = input.Format->streams[input.VideoIndex]; var par = sourceStream->codecpar;
        var timeBase = sourceStream->time_base;
        var parameters = new SpliceEncoding(par->profile & ~512, par->color_range, par->color_space, par->color_primaries, par->color_trc, par->chroma_location);
        var extra = new ReadOnlySpan<byte>(par->extradata, par->extradata_size).ToArray();
        var budget = ExportPolicy.Budget(request.Project.Video.Duration, info.HasAudio);
        Mp4Muxer? muxer = null;
        using var audio = info.HasAudio ? new AudioPacketCopier(info.Path,
            (packet, audioTimeBase) => muxer!.Write(packet, audioTimeBase, true)) : null;
        using var writer = new Mp4Muxer(output, par, timeBase, audio is null ? null : audio.Stream); muxer = writer;
        var packet = ffmpeg.av_packet_alloc(); if (packet == null) throw new OutOfMemoryException();
        long inputIndex = 0, copied = 0, encoded = 0, nativeFrames = 0, bgraFrames = 0;
        try
        {
            foreach (var block in plan.Blocks)
            {
                cancellation.ThrowIfCancellationRequested();
                if (inputIndex != block.PacketStart) throw new InvalidDataException("源视频区段发生变化");
                log.Write("smart-block", new { block.Start, block.End, block.Render, block.PacketCount });
                for (long i = 0; i < block.PacketCount;)
                {
                    ffmpeg.av_packet_unref(packet);
                    cancellation.ThrowIfCancellationRequested();
                    NativeRuntime.Check(ffmpeg.av_read_frame(input.Format, packet), "读取智能导出区段");
                    if (packet->stream_index != input.VideoIndex) continue;
                    if (packet->dts != (inputIndex - plan.DecodeDelay) * plan.Step || packet->duration != plan.Step)
                        throw new InvalidDataException("源视频时间线发生变化");
                    if (!block.Render)
                    {
                        if (i == 0) AddExtra(packet, extra);
                        writer.Write(packet, timeBase, false); copied++;
                        audio?.WriteUntil(Math.Min(request.Project.Video.Duration, (inputIndex + 1) / 30.0), cancellation);
                        if (i % 30 == 0) Report(inputIndex + 1, "智能导出 · 复制");
                    }
                    i++; inputIndex++;
                }
                if (!block.Render) continue;
                var first = true;
                using var encoder = new H264Encoder(info.Width, info.Height, budget.VideoBitsPerSecond, 0, "", (pkt, codec) =>
                {
                    if (first)
                    {
                        if ((pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0) throw new InvalidDataException("编码区段缺少独立起始帧");
                        AddExtra(pkt, new ReadOnlySpan<byte>(codec->extradata, codec->extradata_size)); first = false;
                    }
                    if (pkt->pts != pkt->dts || pkt->pts < 0) throw new InvalidDataException("编码区段存在帧重排");
                    pkt->pts = (pkt->pts + block.Start) * plan.Step;
                    pkt->dts = pkt->pts - plan.DecodeDelay * plan.Step; pkt->duration = plan.Step;
                    writer.Write(pkt, timeBase, false);
                }, parameters);
                using var decoder = new ExportFrameReader(info.Path, block.End - block.Start,
                    ExportFrameReader.ShouldPrefetch(info.Width, info.Height, block.End - block.Start), cancellation, block.Start, true,
                    encoder.NativeFormat, request.NeedsComposition);
                for (var index = block.Start; index < block.End; index++)
                {
                    cancellation.ThrowIfCancellationRequested(); var time = index / 30.0;
                    using var source = decoder.Read(index);
                    // The final audio-only fraction repeats the last frame, matching full export.
                    if (index < plan.SourceFrames && Math.Abs(source.Time - time) > .00001)
                        throw new InvalidDataException("编码区段定位未达到逐帧精度");
                    VideoFrame? frame = null;
                    if (request.NeedsComposition is null || request.NeedsComposition(time)) frame = request.Compose(source.Pixels, time);
                    cancellation.ThrowIfCancellationRequested();
                    if (frame is not null) encoder.Write(frame, index - block.Start); else encoder.Write(source, index - block.Start);
                    encoded++;
                    audio?.WriteUntil(Math.Min(request.Project.Video.Duration, (index + 1) / 30.0), cancellation);
                    if (index % 5 == 0) Report(index + 1, "智能导出 · 渲染");
                }
                encoder.Flush();
                nativeFrames += encoder.NativeFrames; bgraFrames += encoder.BgraFrames;
            }
            if (inputIndex != plan.SourceFrames || copied + encoded != plan.OutputFrames) throw new InvalidDataException("智能导出帧数不一致");
            audio?.Finish(cancellation); writer.Finish();
            log.Write("smart-rendered", new { copiedFrames = copied, encodedFrames = encoded, nativeFrames, bgraFrames,
                audioPackets = audio?.Packets ?? 0, audioBytes = audio?.Bytes ?? 0, plan.CopyBytes });
        }
        finally { ffmpeg.av_packet_free(&packet); }
        void Report(long frame, string stage) => progress?.Report(new(1, 0, 3 + frame / (double)plan.OutputFrames * 82, stage));
    }

    private static void AddExtra(AVPacket* packet, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw new InvalidDataException("拼接区段缺少编码参数");
        // FFmpeg 9's MP4 muxer records a separate avc1 sample description and stsc mapping
        // on NEW_EXTRADATA. Never concatenate incompatible SPS/PPS under one avcC entry.
        var destination = ffmpeg.av_packet_new_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_NEW_EXTRADATA, (ulong)data.Length);
        if (destination == null) throw new OutOfMemoryException();
        data.CopyTo(new Span<byte>(destination, data.Length));
    }

    /// <summary>Before delivery, decode the entire mixed track without BGRA conversion.
    /// This catches lost delayed pictures, invalid SPS changes and timestamp gaps at all splices.</summary>
    internal static void Validate(string path, long expectedFrames, int width, int height,
        IProgress<ExportProgress>? progress, CancellationToken cancellation)
    {
        using var input = new MediaInput(path);
        var codec = input.OpenCodec(input.VideoIndex);
        var packet = ffmpeg.av_packet_alloc(); var frame = ffmpeg.av_frame_alloc();
        try
        {
            if (packet == null || frame == null) throw new OutOfMemoryException();
            codec->err_recognition = ffmpeg.AV_EF_EXPLODE | ffmpeg.AV_EF_BITSTREAM | ffmpeg.AV_EF_BUFFER;
            long count = 0;
            void Drain()
            {
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = ffmpeg.avcodec_receive_frame(codec, frame);
                    if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) return;
                    NativeRuntime.Check(result, "校验拼接画面");
                    var pts = frame->best_effort_timestamp;
                    var seconds = pts * ffmpeg.av_q2d(input.Format->streams[input.VideoIndex]->time_base) - input.Info.Origin;
                    if (frame->width != width || frame->height != height || frame->decode_error_flags != 0 ||
                        (frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0 || pts == ffmpeg.AV_NOPTS_VALUE ||
                        Math.Abs(seconds - count / 30.0) > .00001 || count >= expectedFrames)
                        throw new InvalidDataException("拼接画面或逐帧时间线校验失败");
                    count++; ffmpeg.av_frame_unref(frame);
                    if (count % 30 == 0) progress?.Report(new(1, 0, 85 + count / (double)expectedFrames * 13, "校验"));
                }
            }
            while (true)
            {
                cancellation.ThrowIfCancellationRequested(); ffmpeg.av_packet_unref(packet);
                var result = ffmpeg.av_read_frame(input.Format, packet);
                if (result == ffmpeg.AVERROR_EOF) break;
                NativeRuntime.Check(result, "读取拼接校验数据");
                if (packet->stream_index != input.VideoIndex) continue;
                NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, packet), "提交拼接校验数据"); Drain();
            }
            NativeRuntime.Check(ffmpeg.avcodec_send_packet(codec, null), "完成拼接校验"); Drain();
            if (count != expectedFrames) throw new InvalidDataException("拼接画面总帧数校验失败");
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            var f = frame; ffmpeg.av_frame_free(&f); var c = codec; ffmpeg.avcodec_free_context(&c);
        }
    }
}
