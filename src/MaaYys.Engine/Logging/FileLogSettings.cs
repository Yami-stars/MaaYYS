using System.Text.Json.Serialization;

namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志落盘（P6.2）：把内存缓冲里的日志同时写文件，支持按大小滚动与保留份数。
/// 关掉时只有内存日志（P5.5 行为），程序关掉即丢。
/// </summary>
public sealed class FileLogSettings
{
    /// <summary>默认目录名（相对程序根目录）。</summary>
    public const string DefaultDirectoryName = "logs";

    /// <summary>是否落盘。</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>日志目录（空 = <c>程序根目录/logs</c>；相对路径按程序根目录解析）。</summary>
    [JsonPropertyName("directory")]
    public string Directory { get; set; } = string.Empty;

    /// <summary>单个文件大小上限（MB），超出后滚动。</summary>
    [JsonPropertyName("max_file_size_mb")]
    public int MaxFileSizeMb { get; set; } = 5;

    /// <summary>保留的历史文件份数（不含当前文件）。</summary>
    [JsonPropertyName("max_files")]
    public int MaxFiles { get; set; } = 5;

    /// <summary>落盘的最低级别（Debug 会非常大，默认 Info）。</summary>
    [JsonPropertyName("min_level")]
    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public void Sanitize()
    {
        Directory = (Directory ?? string.Empty).Trim();
        MaxFileSizeMb = Math.Clamp(MaxFileSizeMb, 1, 1024);
        MaxFiles = Math.Clamp(MaxFiles, 1, 100);

        if (MinLevel < LogLevel.Debug || MinLevel > LogLevel.Error)
        {
            MinLevel = LogLevel.Info;
        }
    }

    public void CopyFrom(FileLogSettings? other)
    {
        if (other is null)
        {
            return;
        }

        Enabled = other.Enabled;
        Directory = other.Directory;
        MaxFileSizeMb = other.MaxFileSizeMb;
        MaxFiles = other.MaxFiles;
        MinLevel = other.MinLevel;
    }

    /// <summary>解析出实际目录（相对路径基准是程序根目录）。</summary>
    public string ResolveDirectory(string baseDirectory)
        => string.IsNullOrWhiteSpace(Directory)
            ? Path.Combine(baseDirectory, DefaultDirectoryName)
            : Path.IsPathRooted(Directory)
                ? Directory
                : Path.Combine(baseDirectory, Directory);
}
