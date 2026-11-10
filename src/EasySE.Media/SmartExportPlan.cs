using EasySE.Core;
using FFmpeg.AutoGen;

namespace EasySE.Media;

internal sealed record SmartBlock(long PacketStart, long PacketCount, long Start, long End, long Bytes, bool Render);
internal sealed record SmartPlan(bool Eligible, string Reason, IReadOnlyList<SmartBlock> Blocks,
    long SourceFrames, long OutputFrames, long DecodeDelay, long Step, long CopyBytes, long RenderFrames)
{
    public static SmartPlan Skip(string reason) => new(false, reason, [], 0, 0, 0, 0, 0, 0);
}

/// <summary>Conservative, packet-only eligibility scan. No compressed frame is retained in memory.
/// Each splice is an actual IDR, not merely a container key-frame flag. Every GOP must contain
/// exactly its own contiguous presentation interval; open-GOP leading pictures are rejected.</summary>
internal static unsafe class SmartExportPlan
{
    internal static SmartPlan Analyze(string path, Project project, CancellationToken cancellation)
    {
        using var input = new MediaInput(path);
        var stream = input.Format->streams[input.VideoIndex]; var par = stream->codecpar; var info = input.Info;
        if (par->codec_id != AVCodecID.AV_CODEC_ID_H264) return SmartPlan.Skip("codec-not-h264");
        if (info.Rotation != 0 || Math.Abs(info.Origin) > .000001 || stream->start_time != 0)
            return SmartPlan.Skip("rotation-or-nonzero-origin");
        if (par->format is not ((int)AVPixelFormat.AV_PIX_FMT_YUV420P or (int)AVPixelFormat.AV_PIX_FMT_YUVJ420P) || par->profile is not (66 or 578 or 77 or 100) ||
            par->field_order != AVFieldOrder.AV_FIELD_PROGRESSIVE ||
            par->sample_aspect_ratio.num != par->sample_aspect_ratio.den || par->sample_aspect_ratio.num <= 0)
            return SmartPlan.Skip("pixel-format-profile-or-aspect");
        if (par->color_space is not (AVColorSpace.AVCOL_SPC_UNSPECIFIED or AVColorSpace.AVCOL_SPC_BT709 or AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M) ||
            par->color_primaries is not (AVColorPrimaries.AVCOL_PRI_UNSPECIFIED or AVColorPrimaries.AVCOL_PRI_BT709 or AVColorPrimaries.AVCOL_PRI_BT470BG or AVColorPrimaries.AVCOL_PRI_SMPTE170M) ||
            par->color_trc is not (AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED or AVColorTransferCharacteristic.AVCOL_TRC_BT709 or AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28 or AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M) ||
            par->color_range is not (AVColorRange.AVCOL_RANGE_UNSPECIFIED or AVColorRange.AVCOL_RANGE_MPEG or AVColorRange.AVCOL_RANGE_JPEG) ||
            par->chroma_location is not (AVChromaLocation.AVCHROMA_LOC_UNSPECIFIED or AVChromaLocation.AVCHROMA_LOC_LEFT))
            return SmartPlan.Skip("unsupported-color");
        // Reject transformations/encryption and arbitrary display matrices, including sub-90° rotation.
        for (var i = 0; i < par->nb_coded_side_data; i++)
            if (par->coded_side_data[i].type != AVPacketSideDataType.AV_PKT_DATA_CPB_PROPERTIES)
                return SmartPlan.Skip("stream-side-data");
        if (par->extradata == null || par->extradata_size < 7 || par->extradata[0] != 1 || (par->extradata[4] & 3) != 3)
            return SmartPlan.Skip("unsupported-avcc");
        var timeBase = stream->time_base;
        if (timeBase.num <= 0 || timeBase.den <= 0 || timeBase.den % ((long)timeBase.num * 30) != 0 || Math.Abs(info.FrameRate - 30) > .000001)
            return SmartPlan.Skip("not-exact-30fps");
        var step = timeBase.den / ((long)timeBase.num * 30);
        var groups = new List<SmartBlock>(); var presentation = new HashSet<long>();
        long count = 0, start = 0, bytes = 0, firstPacket = 0, delay = -1;
        var packet = ffmpeg.av_packet_alloc(); if (packet == null) throw new OutOfMemoryException();
        bool CloseGroup()
        {
            var n = count - firstPacket;
            if (n <= 0 || presentation.Count != n || presentation.Min() != start || presentation.Max() != start + n - 1) return false;
            groups.Add(new(firstPacket, n, start, start + n, bytes, false));
            return true;
        }
        try
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                ffmpeg.av_packet_unref(packet); var result = ffmpeg.av_read_frame(input.Format, packet);
                if (result == ffmpeg.AVERROR_EOF) break;
                NativeRuntime.Check(result, "分析视频数据");
                if (packet->stream_index != input.VideoIndex) continue;
                if (count >= 600_000) return SmartPlan.Skip("analysis-frame-limit");
                if (packet->pts == ffmpeg.AV_NOPTS_VALUE || packet->dts == ffmpeg.AV_NOPTS_VALUE || packet->pts < 0 ||
                    packet->pts % step != 0 || packet->dts % step != 0 || packet->duration != step ||
                    (packet->flags & (ffmpeg.AV_PKT_FLAG_CORRUPT | ffmpeg.AV_PKT_FLAG_DISCARD)) != 0 || packet->side_data_elems != 0)
                    return SmartPlan.Skip("variable-or-unsupported-packet-timing");
                var pts = packet->pts / step; var dts = packet->dts / step;
                if (count == 0) delay = -dts;
                if (delay < 0 || delay > 16 || dts != count - delay || pts < dts)
                    return SmartPlan.Skip("nonuniform-decode-timing");
                var idr = PacketKind(new ReadOnlySpan<byte>(packet->data, packet->size));
                if (idr < 0 || (idr == 1) != ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0))
                    return SmartPlan.Skip("not-closed-idr-or-changing-parameters");
                if (count == 0 && (idr != 1 || pts != 0)) return SmartPlan.Skip("missing-initial-idr");
                if (count > 0 && idr == 1)
                {
                    if (!CloseGroup() || pts != count) return SmartPlan.Skip("open-or-overlapping-gop");
                    presentation.Clear(); firstPacket = count; start = pts; bytes = 0;
                }
                if (presentation.Count >= 30_000 || !presentation.Add(pts)) return SmartPlan.Skip("duplicate-or-unbounded-gop");
                bytes += packet->size; count++;
            }
            if (!CloseGroup()) return SmartPlan.Skip("incomplete-final-gop");
        }
        finally { ffmpeg.av_packet_free(&packet); }
        var outputCount = (long)Math.Ceiling(project.Video.Duration * 30 - .000001);
        // A short audio tail may require repeating the last video frame, just as the full renderer does.
        if (outputCount < count || outputCount > count + 1) return SmartPlan.Skip("video-duration-mismatch");
        long Boundary(double time)
        {
            var index = Math.Clamp((long)Math.Ceiling((time - .000001) * 30), 0, outputCount);
            // Match LabelBox.Active's floating-point comparison, including epsilon/ULP ties.
            while (index > 0 && time <= (index - 1) / 30.0 + .000001) index--;
            while (index < outputCount && time > index / 30.0 + .000001) index++;
            return index;
        }
        var intervals = project.Markers.Select(m => (Start: Boundary(m.Start), End: Boundary(m.End)))
            .Where(m => m.End > m.Start).OrderBy(m => m.Start).ToArray();
        var merged = new List<(long Start, long End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            else merged.Add(interval);
        }
        long render = 0, copyBytes = 0;
        var blocks = new List<SmartBlock>(); var intervalIndex = 0;
        foreach (var gop in groups)
        {
            var end = gop.End == count ? outputCount : gop.End;
            while (intervalIndex < merged.Count && merged[intervalIndex].End <= gop.Start) intervalIndex++;
            var needsRender = end != gop.End || intervalIndex < merged.Count && merged[intervalIndex].Start < end;
            var block = gop with { End = end, Render = needsRender };
            if (needsRender) render += end - gop.Start; else copyBytes += gop.Bytes;
            if (blocks.Count > 0 && blocks[^1].Render == needsRender)
            {
                var previous = blocks[^1];
                blocks[^1] = previous with { End = end, PacketCount = previous.PacketCount + block.PacketCount, Bytes = previous.Bytes + block.Bytes };
            }
            else blocks.Add(block);
        }
        // Scanning, decoder startup and the final full-track verification also have a cost.
        // Prefer the existing pipelined full renderer when key-frame expansion saves too little.
        if (render > 0 && (render > outputCount * .6 || outputCount - render < 60 ||
            render + blocks.Count(b => b.Render) * 15 > outputCount * .7)) return SmartPlan.Skip("insufficient-unmarked-gops");
        // Small sources favor packet reuse. Their final size is intentionally not capped:
        // replacing a GOP (and preserving the original audio) may increase the result.
        if (new FileInfo(path).Length <= ExportPolicy.MaxBytes)
            return new(true, render == 0 ? "video-remux" : "closed-gop-splice", blocks, count, outputCount, delay, step, copyBytes, render);
        var budget = ExportPolicy.Budget(project.Video.Duration, info.HasAudio);
        var estimate = copyBytes + render / 30.0 * budget.VideoBitsPerSecond / 8 * 1.15 +
            project.Video.Duration * budget.AudioBitsPerSecond / 8 * 1.1 + 512_000 + outputCount * 32;
        if (estimate > 94_000_000) return SmartPlan.Skip("copied-packets-exceed-size-budget");
        return new(true, render == 0 ? "video-remux" : "closed-gop-splice", blocks, count, outputCount, delay, step, copyBytes, render);
    }

    // Length-prefixed AVC only; IDR slices cannot be mixed with ordinary slices.
    // Changing in-band SPS/PPS, partitions, extension views and recovery-point GOPs use full export.
    internal static int PacketKind(ReadOnlySpan<byte> data)
    {
        var kind = -1;
        while (data.Length > 0)
        {
            if (data.Length < 5) return -1;
            var size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
            if (size == 0 || size > data.Length - 4) return -1;
            var type = data[4] & 31;
            if ((data[4] & 128) != 0) return -1;
            if (type is 1 or 5)
            { var current = type == 5 ? 1 : 0; if (kind >= 0 && kind != current) return -1; kind = current; }
            else if (type is not (6 or 9 or 12)) return -1;
            data = data[(4 + (int)size)..];
        }
        return kind;
    }
}
