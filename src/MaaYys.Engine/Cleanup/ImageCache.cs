using System.Text.Json.Serialization;

namespace MaaYys.Engine.Cleanup;

/// <summary>图片缓存的当前占用。</summary>
public sealed class ImageCacheSnapshot
{
    /// <summary>程序目录。</summary>
    public required string Root { get; init; }

    /// <summary>实际扫描到的目录（相对程序目录，例如 <c>debug</c>）。</summary>
    public IReadOnlyList<string> Directories { get; init; } = [];

    public int FileCount { get; init; }

    public long Bytes { get; init; }

    /// <summary>最新一张图的时间（没有图时为 null）。</summary>
    public DateTimeOffset? Newest { get; init; }

    [JsonIgnore]
    public bool IsEmpty => FileCount == 0;

    /// <summary>给界面直接用的一句话。</summary>
    public string Summary => FileCount == 0
        ? "暂无图片缓存"
        : $"{FileCount} 张图片 · {ResourceAuditResult.FormatBytes(Bytes)}"
          + (Directories.Count > 0 ? $"（{string.Join(" / ", Directories)}）" : string.Empty);
}

/// <summary>清空图片缓存的结果。</summary>
public sealed record ImageCacheClearOutcome(
    int Deleted,
    long FreedBytes,
    int Failed,
    IReadOnlyList<string> Errors)
{
    [JsonIgnore]
    public string Summary => Deleted == 0 && Failed == 0
        ? "图片缓存已经是空的"
        : $"已删除 {Deleted} 张图片，释放 {ResourceAuditResult.FormatBytes(FreedBytes)}"
          + (Failed > 0 ? $"；{Failed} 个文件被占用，跳过" : string.Empty);
}

/// <summary>
/// 图片缓存清理（设置页「清理图片缓存」）。
/// <para>
/// 只删图片，不删日志：<c>debug/</c> 里混着 <c>*.log</c> 与截图，日志是排查问题的唯一线索，
/// 用户点这个按钮的诉求是「把图清掉腾空间」，不是「把现场抹掉」。
/// </para>
/// <para>
/// 与资源整理（<see cref="ResourcePruner"/>）完全分开：这里删的是<b>运行时产物</b>，
/// 误删最多丢几张截图；那边删的是<b>资源包内容</b>，性质不同，绝不能共用一个入口。
/// </para>
/// </summary>
public static class ImageCache
{
    /// <summary>扫描的目录（相对程序目录）。不存在的直接跳过。</summary>
    public static IReadOnlyList<string> DefaultDirectories { get; } = ["debug", "cache"];

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif", ".tif", ".tiff",
    };

    public static ImageCacheSnapshot Scan(string baseDirectory, IEnumerable<string>? directories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        List<string> scanned = [];
        int count = 0;
        long bytes = 0;
        DateTimeOffset? newest = null;

        foreach (string file in EnumerateImages(baseDirectory, directories, scanned))
        {
            count++;
            try
            {
                var info = new FileInfo(file);
                bytes += info.Length;
                if (newest is null || info.LastWriteTimeUtc > newest.Value.UtcDateTime)
                {
                    newest = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToLocalTime();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不到大小就只计数
            }
        }

        return new ImageCacheSnapshot
        {
            Root = Path.GetFullPath(baseDirectory),
            Directories = scanned,
            FileCount = count,
            Bytes = bytes,
            Newest = newest,
        };
    }

    /// <summary>直接删除（不进回收区）：缓存是可以重新生成的产物，恢复没有意义。</summary>
    public static ImageCacheClearOutcome Clear(string baseDirectory, IEnumerable<string>? directories = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        List<string> errors = [];
        List<string> scanned = [];
        int deleted = 0;
        long freed = 0;

        foreach (string file in EnumerateImages(baseDirectory, directories, scanned))
        {
            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        // 顺手收掉删空的子目录，避免 debug/ 里留一堆空壳
        foreach (string directory in scanned)
        {
            TryPruneEmptyDirectories(Path.Combine(baseDirectory, directory));
        }

        return new ImageCacheClearOutcome(deleted, freed, errors.Count, errors);
    }

    private static IEnumerable<string> EnumerateImages(
        string baseDirectory, IEnumerable<string>? directories, List<string> scanned)
    {
        IEnumerable<string> names = directories ?? DefaultDirectories;

        foreach (string name in names)
        {
            string directory = Path.Combine(baseDirectory, name);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            scanned.Add(name);

            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                if (ImageExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private static void TryPruneEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            foreach (string directory in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 收不掉空目录不影响结果
        }
    }
}
