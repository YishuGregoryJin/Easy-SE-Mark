namespace EasySE.Core;

public sealed record EncodingBudget(long VideoBitsPerSecond, long AudioBitsPerSecond, long TargetBytes);
public static class ExportPolicy
{
    public const long MaxBytes = 100_000_000;
    public const int FramesPerSecond = 30;
    public const int MaxAttempts = 4;
    // Two quick single-pass attempts; only difficult size-control cases pay for two-pass encoding.
    public static IReadOnlyList<int> Passes(int attempt)
    {
        if (attempt < 1 || attempt > MaxAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        return attempt <= 2 ? [0] : [1, 2];
    }
    public static EncodingBudget Budget(double seconds, bool hasAudio, long targetBytes = 94_000_000)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400) throw new InvalidOperationException("视频时长无效");
        if (targetBytes <= 512_000 || targetBytes > MaxBytes) throw new ArgumentOutOfRangeException(nameof(targetBytes));
        var total = (long)Math.Floor((targetBytes - 512_000) * 8 / seconds);
        var audio = hasAudio ? Math.Min(128_000, Math.Max(8_000, (long)Math.Floor(total * .12 / 1000) * 1000)) : 0;
        var video = Math.Min(18_000_000, total - audio);
        if (video < 1000) throw new InvalidOperationException("无法在 100 MB 内保持有效视频码率");
        return new(video, audio, targetBytes);
    }
    public static long RetryTarget(long currentTarget, long actualBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(actualBytes);
        return (long)Math.Floor(currentTarget * Math.Min(.8, MaxBytes * .9 / actualBytes));
    }
    public static void Verify(long actualBytes, string videoCodec, int width, int height, VideoInfo source, double fps, bool allowOversize = false)
    {
        if (actualBytes <= 0 || !allowOversize && actualBytes > MaxBytes || videoCodec != "h264" || width != source.Width || height != source.Height || Math.Abs(fps - 30) > .001)
            throw new InvalidDataException("导出文件未通过规格校验");
    }
    public static string SafeName(string name, string extension)
    {
        var forbidden = "<>:\"/\\|?*";
        var clean = new string(name.Select(c => c < 32 || forbidden.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) clean = clean[..^extension.Length];
        clean = Rules.Limit(clean, 100).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(clean)) clean = "音效标记";
        var stem = clean.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem)) clean = "_" + clean;
        return clean + extension;
    }
    // Reserve with CreateNew, not Exists + Create, to avoid races and any overwrite.
    public static (string Path, FileStream Stream) Reserve(string directory, string name)
    {
        var safe = SafeName(name, ".mp4");
        for (var index = 0; index < 10000; index++)
        {
            var file = index == 0 ? safe : $"{Path.GetFileNameWithoutExtension(safe)} ({index}).mp4";
            var path = Path.Combine(Path.GetFullPath(directory), file);
            try { return (path, new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("同名文件过多，请修改文件名");
    }

    // Publish a fully written, closed staging file by an atomic same-directory rename.
    // A crash during encoding/copying must never leave a broken file named *.mp4.
    public static string Deliver(string stagingPath, string directory, string name)
    {
        directory = Path.GetFullPath(directory);
        stagingPath = Path.GetFullPath(stagingPath);
        if (!string.Equals(Path.GetDirectoryName(stagingPath), Path.TrimEndingDirectorySeparator(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("交付临时文件必须位于目标目录");
        var safe = SafeName(name, ".mp4");
        for (var index = 0; index < 10000; index++)
        {
            var file = index == 0 ? safe : $"{Path.GetFileNameWithoutExtension(safe)} ({index}).mp4";
            var path = Path.Combine(directory, file);
            try { File.Move(stagingPath, path, false); return path; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("同名文件过多，请修改文件名");
    }
}
