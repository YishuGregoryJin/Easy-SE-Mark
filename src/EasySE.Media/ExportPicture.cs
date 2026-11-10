using FFmpeg.AutoGen;

namespace EasySE.Media;

internal sealed record NativeEncoding(AVPixelFormat Format, AVColorRange Range, AVColorSpace Space,
    AVColorPrimaries Primaries, AVColorTransferCharacteristic Transfer)
{
    internal static unsafe SpliceEncoding? SourceColor(string path)
    {
        using var input = new MediaInput(path);
        var p = input.Format->streams[input.VideoIndex]->codecpar;
        // Preserve conventional SDR color interpretation on both marked and unmarked frames.
        // Unknown/high-dynamic-range matrices keep the existing conversion fallback.
        if (p->color_space is not (AVColorSpace.AVCOL_SPC_UNSPECIFIED or AVColorSpace.AVCOL_SPC_BT709 or
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M) ||
            p->color_trc is not (AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED or AVColorTransferCharacteristic.AVCOL_TRC_BT709 or
            AVColorTransferCharacteristic.AVCOL_TRC_GAMMA28 or AVColorTransferCharacteristic.AVCOL_TRC_SMPTE170M)) return null;
        return new(p->profile, p->color_range, p->color_space, p->color_primaries, p->color_trc, p->chroma_location);
    }
}

/// <summary>A reference-counted decoder picture or independent BGRA pixels, never both.
/// Native references may cross the bounded decode queue; no borrowed decoder pointer escapes.</summary>
internal sealed unsafe class ExportPicture : IDisposable
{
    internal VideoFrame? Pixels { get; }
    internal AVFrame* Native;
    internal double Time { get; }
    internal ExportPicture(VideoFrame pixels) { Pixels = pixels; Time = pixels.Time; }
    internal ExportPicture(AVFrame* source, double time)
    {
        Native = ffmpeg.av_frame_clone(source);
        if (Native == null) throw new OutOfMemoryException();
        Time = time;
    }
    public void Dispose()
    {
        var f = Native; if (f != null) ffmpeg.av_frame_free(&f); Native = null;
    }
}
