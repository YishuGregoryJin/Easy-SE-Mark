using FFmpeg.AutoGen;

namespace EasySE.Media;

internal unsafe delegate void TimedPacketSink(AVPacket* packet, AVRational timeBase);

/// <summary>Preserves compressed audio, codec parameters, priming and relative timestamps.
/// Owns a separate demuxer so packet copying can follow either video export path.</summary>
internal sealed unsafe class AudioPacketCopier : IDisposable
{
    private readonly MediaInput input;
    private readonly TimedPacketSink sink;
    private readonly long origin;
    private AVPacket* packet;
    private bool pending, ended;
    internal AVStream* Stream => input.Format->streams[input.AudioIndex];
    internal long Packets { get; private set; }
    internal long Bytes { get; private set; }

    internal AudioPacketCopier(string path, TimedPacketSink sink)
    {
        input = new MediaInput(path); this.sink = sink;
        try
        {
            if (input.AudioIndex < 0) throw new InvalidDataException("源视频没有音频流");
            origin = ffmpeg.av_rescale_q((long)Math.Round(input.Info.Origin * ffmpeg.AV_TIME_BASE),
                new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, Stream->time_base);
            packet = ffmpeg.av_packet_alloc();
            if (packet == null) throw new OutOfMemoryException();
        }
        catch { Dispose(); throw; }
    }

    internal void WriteUntil(double time, CancellationToken cancellation)
    {
        while (Next(cancellation))
        {
            var timestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
            if (timestamp == ffmpeg.AV_NOPTS_VALUE) throw new InvalidDataException("源音频缺少时间戳，无法直接复制");
            if (timestamp * ffmpeg.av_q2d(Stream->time_base) > time) return;
            Deliver();
        }
    }

    internal void Finish(CancellationToken cancellation)
    {
        while (Next(cancellation)) Deliver();
    }

    private bool Next(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (pending) return true;
        while (!ended)
        {
            cancellation.ThrowIfCancellationRequested(); ffmpeg.av_packet_unref(packet);
            var result = ffmpeg.av_read_frame(input.Format, packet);
            if (result == ffmpeg.AVERROR_EOF) { ended = true; return false; }
            NativeRuntime.Check(result, "读取原始音频");
            if (packet->stream_index != input.AudioIndex) continue;
            if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts -= origin;
            if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts -= origin;
            pending = true; return true;
        }
        return false;
    }

    private void Deliver()
    {
        var size = packet->size;
        sink(packet, Stream->time_base); pending = false;
        Packets++; Bytes += size;
    }

    public void Dispose()
    {
        var p = packet; if (p != null) ffmpeg.av_packet_free(&p); packet = null;
        input.Dispose();
    }
}
