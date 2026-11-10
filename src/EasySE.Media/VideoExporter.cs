using EasySE.Core;
using System.Diagnostics;

namespace EasySE.Media;

public sealed record ExportProgress(int Attempt, int Pass, double Percent, string Stage);
public sealed record ExportResult(string Path, long Bytes, int Attempts)
{
    public string Mode { get; init; } = "full";
    public long CopiedFrames { get; init; }
    public long RenderedFrames { get; init; }
}
public sealed record ExportRequest(Project Project, string? SourcePath, string Directory, string Name,
    Func<VideoFrame?, double, VideoFrame> Compose)
{
    // Optional, immutable/thread-safe schedule. Null keeps custom compositors on the BGRA path.
    public Func<double, bool>? NeedsComposition { get; init; }
}

public static class VideoExporter
{
    public static Task<ExportResult> RunAsync(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken cancellation) =>
        Task.Run(() => Run(request, progress, cancellation), cancellation);
    private static unsafe ExportResult Run(ExportRequest request, IProgress<ExportProgress>? progress, CancellationToken cancellation)
    {
        NativeRuntime.Initialize(); cancellation.ThrowIfCancellationRequested();
        var project = request.Project; var duration = project.Video.Duration;
        MediaInfo? info = request.SourcePath is null ? null : MediaProbe.Read(request.SourcePath);
        if (info is null) throw new InvalidOperationException("请连接视频");
        if (info.Width != project.Video.Width || info.Height != project.Video.Height || Math.Abs(info.Duration - duration) > .25)
            throw new InvalidOperationException("项目与源视频不匹配，请重新连接");
        var sourceBytes = new FileInfo(info.Path).Length;
        // Small originals and original-audio preservation explicitly permit larger deliveries.
        var allowOversize = sourceBytes <= ExportPolicy.MaxBytes || info.HasAudio;
        var sourceColor = NativeEncoding.SourceColor(info.Path);
        var cache = Environment.GetEnvironmentVariable("EASYSE_CACHE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasySEMark", "cache");
        var jobId = Guid.NewGuid().ToString("N");
        var job = Path.Combine(Path.GetFullPath(cache), "exports", jobId); Directory.CreateDirectory(job);
        using var log = new ExportLog(Path.GetFullPath(cache), jobId);
        var totalWatch = Stopwatch.StartNew();
        string? pendingDelivery = null;
        try
        {
            log.Write("start", new { source = Path.GetFileName(info.Path), sourceBytes, allowOversize, audioMode = info.HasAudio ? "copy" : "none",
                info.Width, info.Height, duration, info.VideoCodec, info.HasAudio, native = NativeRuntime.Version });
            var smartReady = false; SmartPlan? smartPlan = null;
            var smartWatch = Stopwatch.StartNew();
            try
            {
                progress?.Report(new(1, 0, 0, "分析"));
                smartPlan = SmartExportPlan.Analyze(info.Path, project, cancellation);
                log.Write("smart-analysis", new { smartPlan.Eligible, smartPlan.Reason, smartPlan.SourceFrames,
                    smartPlan.OutputFrames, smartPlan.RenderFrames, smartPlan.CopyBytes, blocks = smartPlan.Blocks.Count,
                    elapsedSeconds = smartWatch.Elapsed.TotalSeconds });
                if (smartPlan.Eligible)
                {
                    var output = Path.Combine(job, "output.mp4");
                    SmartVideoExporter.Render(request, info, smartPlan, output, progress, cancellation, log);
                    var bytes = new FileInfo(output).Length; var actual = MediaProbe.Read(output);
                    ExportPolicy.Verify(bytes, actual.VideoCodec, actual.Width, actual.Height, project.Video, actual.FrameRate, allowOversize);
                    if (actual.HasAudio != info.HasAudio) throw new InvalidDataException("智能导出音频流校验失败");
                    SmartVideoExporter.Validate(output, smartPlan.OutputFrames, info.Width, info.Height, progress, cancellation);
                    smartReady = true;
                    log.Write("smart-validated", new { bytes, elapsedSeconds = smartWatch.Elapsed.TotalSeconds });
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                cancellation.ThrowIfCancellationRequested();
                log.Write("smart-fallback", new { error = ex.ToString(), elapsedSeconds = smartWatch.Elapsed.TotalSeconds });
                progress?.Report(new(1, 0, 0, "渲染"));
            }
            {
                var target = 94_000_000L;
                for (var attempt = 1; attempt <= ExportPolicy.MaxAttempts; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var budget = ExportPolicy.Budget(duration, info.HasAudio, target);
                    var output = Path.Combine(job, "output.mp4"); var stats = Path.Combine(job, "x264-" + attempt + ".log");
                    var passes = ExportPolicy.Passes(attempt);
                    for (var passIndex = 0; !smartReady && passIndex < passes.Count; passIndex++)
                    {
                        var pass = passes[passIndex]; var finalPass = pass != 1;
                        var passWatch = Stopwatch.StartNew();
                        long composeTicks = 0, videoTicks = 0, audioTicks = 0;
                        var count = (long)Math.Ceiling(duration * 30 - .000001);
                        var prefetch = ExportFrameReader.ShouldPrefetch(info.Width, info.Height, count);
                        log.Write("pass-start", new { attempt, pass, preset = H264Encoder.Preset, prefetch, queuedFrames = prefetch ? 1 : 0, budget.VideoBitsPerSecond, budget.AudioBitsPerSecond });
                        Mp4Muxer? muxer = null;
                        using var video = new H264Encoder(project.Video.Width, project.Video.Height, budget.VideoBitsPerSecond, pass, stats,
                            !finalPass ? null : (packet, codec) => muxer!.Write(packet, codec, false), sourceColor: sourceColor);
                        using var decoder = new ExportFrameReader(info.Path, count, prefetch, cancellation,
                            preserveColor: sourceColor is not null, nativeEncoding: video.NativeFormat, needsComposition: request.NeedsComposition);
                        using var audio = finalPass && info.HasAudio ? new AudioPacketCopier(info.Path, (packet, timeBase) => muxer!.Write(packet, timeBase, true)) : null;
                        using var outputMuxer = !finalPass ? null : new Mp4Muxer(output, video.Context, audio is null ? null : audio.Stream);
                        muxer = outputMuxer;
                        for (long index = 0; index < count; index++)
                        {
                            cancellation.ThrowIfCancellationRequested(); var time = index / 30.0;
                            using var source = decoder.Read(index);
                            var tick = Stopwatch.GetTimestamp();
                            VideoFrame? frame = null;
                            if (request.NeedsComposition is null || request.NeedsComposition(time))
                                frame = request.Compose(source.Pixels, time);
                            composeTicks += Stopwatch.GetTimestamp() - tick;
                            cancellation.ThrowIfCancellationRequested();
                            tick = Stopwatch.GetTimestamp();
                            if (frame is not null) video.Write(frame, index); else video.Write(source, index);
                            videoTicks += Stopwatch.GetTimestamp() - tick;
                            tick = Stopwatch.GetTimestamp(); audio?.WriteUntil(Math.Min(duration, (index + 1) / 30.0), cancellation);
                            audioTicks += Stopwatch.GetTimestamp() - tick;
                            if (index % 30 == 0 || index == count - 1) log.Write("frame", new { attempt, pass, index, count });
                            if (index % 5 == 0 || index == count - 1) progress?.Report(new(attempt, pass,
                                (passIndex + (index + 1) / (double)count) / passes.Count * 98,
                                pass == 0 ? (attempt == 1 ? "渲染" : $"第 {attempt} 次 · 渲染") : $"第 {attempt} 次 · {pass}/2"));
                        }
                        video.Flush(); audio?.Finish(cancellation); outputMuxer?.Finish();
                        log.Write("pass-finished", new { attempt, pass, elapsedSeconds = passWatch.Elapsed.TotalSeconds,
                            decodeSeconds = decoder.DecodeTicks / (double)Stopwatch.Frequency, composeSeconds = composeTicks / (double)Stopwatch.Frequency,
                            videoSeconds = videoTicks / (double)Stopwatch.Frequency, audioSeconds = audioTicks / (double)Stopwatch.Frequency,
                            nativeFrames = video.NativeFrames, bgraFrames = video.BgraFrames, audioPackets = audio?.Packets ?? 0, audioBytes = audio?.Bytes ?? 0,
                            framesPerSecond = count / passWatch.Elapsed.TotalSeconds });
                    }
                    cancellation.ThrowIfCancellationRequested(); var bytes = new FileInfo(output).Length;
                    if (!allowOversize && bytes > ExportPolicy.MaxBytes)
                    {
                        if (attempt == ExportPolicy.MaxAttempts) throw new InvalidOperationException("导出文件无法控制在 100 MB 内，未交付超限文件");
                        log.Write("size-retry", new { attempt, bytes });
                        target = ExportPolicy.RetryTarget(target, bytes); continue;
                    }
                    progress?.Report(new(attempt, passes[^1], 98, "校验"));
                    var actual = MediaProbe.Read(output);
                    ExportPolicy.Verify(bytes, actual.VideoCodec, actual.Width, actual.Height, project.Video, actual.FrameRate, allowOversize);
                    if (actual.HasAudio != info.HasAudio) throw new InvalidDataException("导出音频流校验失败");
                    cancellation.ThrowIfCancellationRequested();
                    log.Write("validated", new { bytes, actual.Width, actual.Height, actual.FrameRate, actual.HasAudio });
                    // Stage on the destination volume. The final MP4 name exists only after a
                    // complete, flushed copy has been atomically renamed, never at export start.
                    pendingDelivery = Path.Combine(Path.GetFullPath(request.Directory), ".easyse-export-" + jobId + ".partial");
                    using (var input = File.OpenRead(output))
                    using (var delivery = new FileStream(pendingDelivery, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var buffer = new byte[128 * 1024]; int read;
                        while ((read = input.Read(buffer)) > 0) { cancellation.ThrowIfCancellationRequested(); delivery.Write(buffer, 0, read); }
                        delivery.Flush(true);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    var path = ExportPolicy.Deliver(pendingDelivery, request.Directory, request.Name); pendingDelivery = null;
                    log.Write("delivered", new { file = Path.GetFileName(path), bytes, attempt, elapsedSeconds = totalWatch.Elapsed.TotalSeconds });
                    progress?.Report(new(attempt, passes[^1], 100, "完成"));
                    return new(path, bytes, attempt)
                    {
                        Mode = smartReady ? smartPlan!.Reason : "full",
                        CopiedFrames = smartReady ? smartPlan!.OutputFrames - smartPlan.RenderFrames : 0,
                        RenderedFrames = smartReady ? smartPlan!.RenderFrames : (long)Math.Ceiling(duration * 30 - .000001)
                    };
                }
                throw new InvalidOperationException("导出失败");
            }
        }
        catch (OperationCanceledException) { log.Write("canceled"); throw; }
        catch (Exception ex)
        {
            log.Write("failed", new { error = ex.ToString() }); ex.Data["EasySE.ExportLog"] = log.Path; throw;
        }
        finally
        {
            if (pendingDelivery is not null) { try { File.Delete(pendingDelivery); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            // Flat, privately-created job directory: no recursive delete or computed external target.
            try { foreach (var file in Directory.GetFiles(job)) File.Delete(file); Directory.Delete(job); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
