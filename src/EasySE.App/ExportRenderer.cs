using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasySE.Core;
using EasySE.Media;

namespace EasySE.App;

/// <summary>STA-only, bounded label raster cache. Source frames are never modified.
/// Composed pixels are borrowed until the next Compose call; the exporter consumes each synchronously.</summary>
internal sealed class ExportRenderer : IDisposable
{
    private sealed record Request(VideoFrame? Frame, double Time, TaskCompletionSource<VideoFrame> Result);
    private sealed record Tile(int X, int Y, int Width, int Height, byte[] Pixels);
    private const long CacheLimit = 64 * 1024 * 1024;
    private readonly BlockingCollection<Request> requests = new(1);
    private readonly Thread thread;
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly (double Start, double End)[] intervals;
    public ExportRenderer(Project project)
    {
        var merged = new List<(double Start, double End)>();
        foreach (var marker in project.Markers.OrderBy(m => m.Start))
        {
            if (merged.Count > 0 && marker.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, marker.End));
            else merged.Add((marker.Start, marker.End));
        }
        intervals = merged.ToArray();
        thread = new Thread(() => Work(project)) { IsBackground = true, Name = "Easy SE Mark export compositor" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); initialized.Task.GetAwaiter().GetResult();
    }
    public bool NeedsComposition(double time)
    {
        var target = time + .000001; var low = 0; var high = intervals.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (intervals[middle].Start <= target) low = middle + 1; else high = middle;
        }
        return low > 0 && intervals[low - 1].End > target;
    }
    public VideoFrame Compose(VideoFrame? source, double time)
    {
        var result = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Add(new(source, time, result)); return result.Task.GetAwaiter().GetResult();
    }
    private void Work(Project project)
    {
        try
        {
            var width = project.Video.Width; var height = project.Video.Height;
            var boxes = LabelLayout.CalculateAll(project, width, height, Draw.Measure);
            Int32Rect Bounds(LabelBox box)
            {
                var margin = Math.Ceiling(box.FontSize * .5) + 2;
                var x = Math.Max(0, (int)Math.Floor(box.X - box.Width / 2 - margin));
                var y = Math.Max(0, (int)Math.Floor(box.Y - box.Height / 2 - margin));
                return new(x, y, Math.Min(width, (int)Math.Ceiling(box.X + box.Width / 2 + margin)) - x,
                    Math.Min(height, (int)Math.Ceiling(box.Y + box.Height / 2 + margin)) - y);
            }
            var regions = boxes.ToDictionary(box => box, Bounds);
            var cache = new Dictionary<LabelBox, (Tile Tile, LinkedListNode<LabelBox> Node)>();
            var recent = new LinkedList<LabelBox>(); long cacheBytes = 0;
            var active = new List<LabelBox>(); LabelBox[] groupBoxes = []; Tile? groupTile = null;
            byte[]? output = null;
            Tile Raster(IEnumerable<LabelBox> labels, Int32Rect region)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
                    foreach (var box in labels) Draw.Label(dc, box, false);
                    dc.Pop();
                }
                var bitmap = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var pixels = new byte[checked(region.Width * region.Height * 4)]; bitmap.CopyPixels(pixels, region.Width * 4, 0);
                return new(region.X, region.Y, region.Width, region.Height, pixels);
            }
            Tile GetTile(LabelBox box)
            {
                if (cache.TryGetValue(box, out var hit))
                { recent.Remove(hit.Node); recent.AddLast(hit.Node); return hit.Tile; }
                // Integer cropping preserves subpixel text placement. Extra margin includes glyph overhang.
                var tile = Raster([box], regions[box]); var pixels = tile.Pixels;
                // Oversized individual labels are temporary, not multiplied by the 5000-marker limit.
                if (pixels.LongLength <= CacheLimit)
                {
                    while (cacheBytes + pixels.LongLength > CacheLimit && recent.First is { } oldest)
                    { cacheBytes -= cache[oldest.Value].Tile.Pixels.LongLength; cache.Remove(oldest.Value); recent.RemoveFirst(); }
                    cache.Add(box, (tile, recent.AddLast(box))); cacheBytes += pixels.LongLength;
                }
                return tile;
            }
            bool MergeRegion(double stableFor, out Int32Rect region)
            {
                region = regions[active[0]]; long pixels = 0;
                foreach (var box in active)
                {
                    var bounds = regions[box]; pixels += (long)bounds.Width * bounds.Height;
                    var x = Math.Min(region.X, bounds.X); var y = Math.Min(region.Y, bounds.Y);
                    region = new(x, y, Math.Max(region.X + region.Width, bounds.X + bounds.Width) - x,
                        Math.Max(region.Y + region.Height, bounds.Y + bounds.Height) - y);
                }
                var merged = (long)region.Width * region.Height;
                // Briefly changing sets that already fit the cache should reuse their individual
                // tiles, not rerasterize every unchanged label whenever a short marker appears.
                return merged * 4 <= CacheLimit && (pixels * 4 > CacheLimit ||
                    pixels > merged * 1.15 && (stableFor >= .2 || groupTile is not null && active.SequenceEqual(groupBoxes)));
            }
            initialized.TrySetResult();
            foreach (var request in requests.GetConsumingEnumerable())
            {
                try
                {
                    var frame = request.Frame ?? throw new InvalidOperationException("缺少源视频帧，请重新连接视频");
                    if (frame.Width != width || frame.Height != height || frame.Pixels.Length != checked(width * height * 4))
                        throw new InvalidDataException("导出画面尺寸不匹配");
                    active.Clear(); var nextChange = double.PositiveInfinity;
                    foreach (var box in boxes)
                    {
                        if (box.Active(request.Time)) { active.Add(box); nextChange = Math.Min(nextChange, box.End); }
                        else if (box.Start > request.Time + .000001) nextChange = Math.Min(nextChange, box.Start);
                    }
                    // No labels: no full-frame copy, WPF render, pixel readback or output allocation.
                    if (active.Count == 0) { request.Result.SetResult(frame); continue; }
                    output ??= GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
                    frame.Pixels.CopyTo(output, 0);
                    if (active.Count > 1 && MergeRegion(nextChange - request.Time, out var region))
                    {
                        // Large overlapping sets otherwise evict each other's tiles on every frame.
                        // A single merged transparent tile preserves draw order and the 64 MiB cache bound.
                        if (groupTile is null || !active.SequenceEqual(groupBoxes))
                        {
                            groupTile = null; groupBoxes = []; cache.Clear(); recent.Clear(); cacheBytes = 0;
                            groupTile = Raster(active, region); groupBoxes = active.ToArray();
                        }
                        Blend(output, width, groupTile);
                    }
                    else
                    {
                        groupTile = null; groupBoxes = [];
                        foreach (var box in active) Blend(output, width, GetTile(box));
                    }
                    request.Result.SetResult(new(output, width, height, request.Time));
                }
                catch (Exception ex) { request.Result.SetException(ex); }
            }
        }
        catch (Exception ex)
        {
            initialized.TrySetException(ex);
            requests.CompleteAdding();
            while (requests.TryTake(out var pending)) pending.Result.TrySetException(ex);
        }
    }
    private static void Blend(byte[] output, int width, Tile tile)
    {
        // Cached Pbgra32 already contains premultiplied color. Decoded video is opaque BGRA.
        for (var row = 0; row < tile.Height; row++)
        {
            var source = row * tile.Width * 4; var destination = ((tile.Y + row) * width + tile.X) * 4;
            LabelBlend.Row(output, destination, tile.Pixels, source, tile.Width * 4);
        }
    }
    public void Dispose() { requests.CompleteAdding(); thread.Join(); requests.Dispose(); }
}
