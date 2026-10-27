using System.Globalization;
using System.Text.Json.Serialization;

namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志策略（P5.5）：时间格式、缓冲容量、是否记录节点级事件、截图卡片尺寸。
/// 持久化在 <c>config/maayys.json</c> 顶层 <c>log</c> 段（跨配置组共享）。
/// </summary>
public sealed class LogOptions
{
    /// <summary>默认时间格式（毫秒级：长跑排查要看清节点间隔）。</summary>
    public const string DefaultTimeFormat = "HH:mm:ss.fff";

    /// <summary>界面下拉的时间格式预设。</summary>
    public static readonly string[] TimeFormatPresets =
    [
        "HH:mm:ss",
        "HH:mm:ss.fff",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
    ];

    /// <summary>时间格式（.NET 自定义格式串）。</summary>
    [JsonPropertyName("time_format")]
    public string TimeFormat { get; set; } = DefaultTimeFormat;

    /// <summary>环形缓冲容量（超出后从最旧开始丢弃）。</summary>
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; } = 20_000;

    /// <summary>是否记录节点级事件（<c>Node.*</c>）：量大，默认关，排查时再开。</summary>
    [JsonPropertyName("node_events")]
    public bool NodeEvents { get; set; }

    /// <summary>是否为警告/错误附截图卡片。</summary>
    [JsonPropertyName("thumbnails")]
    public bool Thumbnails { get; set; } = true;

    /// <summary>截图卡片目标宽度（像素，等比缩放）。</summary>
    [JsonPropertyName("thumbnail_width")]
    public int ThumbnailWidth { get; set; } = 320;

    /// <summary>同时保留的截图卡片上限（超出后新条目不再附图，避免长跑吃内存）。</summary>
    [JsonPropertyName("max_thumbnails")]
    public int MaxThumbnails { get; set; } = 300;

    public LogOptions Clone() => new()
    {
        TimeFormat = TimeFormat,
        Capacity = Capacity,
        NodeEvents = NodeEvents,
        Thumbnails = Thumbnails,
        ThumbnailWidth = ThumbnailWidth,
        MaxThumbnails = MaxThumbnails,
    };

    /// <summary>
    /// 原地覆盖全部字段（**不换对象**）：引擎日志管线持有同一个实例引用，
    /// 这样界面改设置后无需重连即可生效。
    /// </summary>
    public LogOptions CopyFrom(LogOptions? other)
    {
        if (other is null)
        {
            return this;
        }

        TimeFormat = other.TimeFormat;
        Capacity = other.Capacity;
        NodeEvents = other.NodeEvents;
        Thumbnails = other.Thumbnails;
        ThumbnailWidth = other.ThumbnailWidth;
        MaxThumbnails = other.MaxThumbnails;
        return this;
    }

    /// <summary>夹取到合法区间并修正时间格式（非法的格式串会让 ToString 抛异常）。</summary>
    public LogOptions Sanitized()
    {
        Capacity = Math.Clamp(Capacity, 100, 200_000);
        ThumbnailWidth = Math.Clamp(ThumbnailWidth, 80, 1920);
        MaxThumbnails = Math.Clamp(MaxThumbnails, 0, 5_000);
        if (!IsValidTimeFormat(TimeFormat))
        {
            TimeFormat = DefaultTimeFormat;
        }

        return this;
    }

    /// <summary>格式串是否可用（比抛异常再回退更便宜，也让界面能即时提示）。</summary>
    public static bool IsValidTimeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        try
        {
            _ = DateTimeOffset.Now.ToString(format, CultureInfo.CurrentCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>按当前格式渲染时间；格式非法时回退默认格式（导出/界面都不会因它崩）。</summary>
    public string FormatTime(DateTimeOffset time)
    {
        string format = IsValidTimeFormat(TimeFormat) ? TimeFormat : DefaultTimeFormat;
        return time.ToString(format, CultureInfo.CurrentCulture);
    }
}
