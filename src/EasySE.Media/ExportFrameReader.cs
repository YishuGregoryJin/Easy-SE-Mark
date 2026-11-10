using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace EasySE.Media;

/// <summary>One thread-confined decoder; at most one queued frame. No compositor output crosses threads.</summary>
internal sealed class ExportFrameReader : IDisposable
{
    private readonly record struct Decoded(long Index, ExportPicture Frame, long Ticks);
    private readonly BlockingCollection<Decoded>? frames;
    private readonly CancellationTokenSource cancellation;
    private readonly Thread? worker;
    private readonly VideoDecoder? serial;
    private ExceptionDispatchInfo? failure;
    private bool disposed;
    private readonly NativeEncoding? nativeEncoding;
    private readonly Func<double, bool>? needsComposition;
    internal long DecodeTicks { get; private set; }
    internal bool PrefetchEnabled => worker is not null;
    internal bool WorkerAlive => worker?.IsAlive == true;
    internal int BufferedFrames => frames?.Count ?? 0;
    internal static bool ShouldPrefetch(int width, int height, long count) =>
        Environment.ProcessorCount >= 4 && count >= 60 && (long)width * height * 4 <= 32 * 1024 * 1024;

    internal ExportFrameReader(string path, long count, bool prefetch, CancellationToken cancellation, long startIndex = 0, bool preserveColor = false,
        NativeEncoding? nativeEncoding = null, Func<double, bool>? needsComposition = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        this.nativeEncoding = nativeEncoding; this.needsComposition = needsComposition;
        this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (!prefetch)
        {
            try
            {
                serial = new VideoDecoder(path, int.MaxValue, int.MaxValue) { PreserveSourceColor = preserveColor };
                if (startIndex > 0) serial.SeekTo(startIndex / 30.0);
            }
            catch { serial?.Dispose(); this.cancellation.Dispose(); throw; }
            return;
        }
        frames = new BlockingCollection<Decoded>(1);
        worker = new Thread(() => Produce(path, count, startIndex, preserveColor)) { IsBackground = true, Name = "Easy SE Mark export decoder" };
        worker.Start();
    }
    private void Produce(string path, long count, long startIndex, bool preserveColor)
    {
        try
        {
            using var decoder = new VideoDecoder(path, int.MaxValue, int.MaxValue) { PreserveSourceColor = preserveColor };
            if (startIndex > 0) decoder.SeekTo(startIndex / 30.0);
            for (long index = startIndex; index < startIndex + count; index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var start = Stopwatch.GetTimestamp(); var frame = Decode(decoder, index);
                try { frames!.Add(new(index, frame, Stopwatch.GetTimestamp() - start), cancellation.Token); }
                catch { frame.Dispose(); throw; }
            }
        }
        catch (Exception ex) { Volatile.Write(ref failure, ExceptionDispatchInfo.Capture(ex)); }
        finally { frames!.CompleteAdding(); }
    }
    internal VideoFrame At(long index)
    {
        using var picture = Read(index);
        return picture.Pixels ?? throw new InvalidOperationException("原生导出帧不能作为 BGRA 读取");
    }
    private ExportPicture Decode(VideoDecoder decoder, long index)
    {
        var time = index / 30.0;
        var encoding = needsComposition is not null && !needsComposition(time) ? nativeEncoding : null;
        return decoder.AtForExport(time, encoding, cancellation.Token);
    }
    internal ExportPicture Read(long index)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (serial is not null)
        {
            var start = Stopwatch.GetTimestamp(); var frame = Decode(serial, index);
            DecodeTicks += Stopwatch.GetTimestamp() - start; return frame;
        }
        if (frames!.TryTake(out var decoded, Timeout.Infinite, cancellation.Token))
        {
            if (decoded.Index != index) { decoded.Frame.Dispose(); throw new InvalidOperationException("导出解码帧顺序异常"); }
            DecodeTicks += decoded.Ticks; return decoded.Frame;
        }
        Volatile.Read(ref failure)?.Throw();
        throw new InvalidDataException("导出解码提前结束");
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        cancellation.Cancel(); worker?.Join(); serial?.Dispose();
        if (frames is not null) { while (frames.TryTake(out var pending)) pending.Frame.Dispose(); frames.Dispose(); }
        cancellation.Dispose();
    }
}
