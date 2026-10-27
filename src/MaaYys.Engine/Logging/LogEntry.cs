namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志缩略图（P5.5 截图卡片）：引擎侧只搬运缩小后的原始像素，编码成图片由 GUI 层负责。
/// <para>
/// 为什么不在引擎里编码 PNG：Engine 目标框架是 <c>net9.0</c>（无 WPF / 无 System.Drawing），
/// 而 App 侧已有 <c>FrameBitmapConverter</c> 与 <c>PngBitmapEncoder</c>，编码放那边零成本。
/// </para>
/// </summary>
/// <param name="Pixels">行优先、无 padding 的像素（BGR / BGRA / 灰度，与源帧一致）。</param>
public sealed record LogThumbnail(byte[] Pixels, int Width, int Height, int Channels)
{
    /// <summary>每行字节数。</summary>
    public int Stride => Width * Channels;

    /// <summary>估算占用字节（用于容量统计）。</summary>
    public long ByteSize => (long)Pixels.Length;
}

/// <summary>
/// 一条日志。设计为「构造后不再修改」（<see cref="Seq"/> 由 <see cref="LogBuffer"/> 分配），
/// 这样界面可以安全地跨线程持有引用。
/// </summary>
public sealed class LogEntry
{
    /// <summary>全局单调序号（由 <see cref="LogBuffer"/> 写入；0 表示尚未入缓冲）。</summary>
    public long Seq { get; internal set; }

    /// <summary>时间（本地时区）。</summary>
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    public LogLevel Level { get; init; } = LogLevel.Info;

    /// <summary>来源：Engine / Agent / Runner / Timer / System。</summary>
    public string Source { get; init; } = "Engine";

    /// <summary>归组键：原生消息名（<c>Tasker.Task.Failed</c>）或自定义分类（<c>queue</c>）。</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>人类可读正文。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>原始详情（原生事件的 details_json 等），可为 null。</summary>
    public string? Details { get; init; }

    /// <summary>截图卡片（仅警告/错误级且开启缩略图时有值）。</summary>
    public LogThumbnail? Thumbnail { get; init; }

    /// <summary>所属实例名（多实例合并显示用；系统级日志为空串）。</summary>
    public string Scope { get; set; } = string.Empty;

    public bool HasThumbnail => Thumbnail is not null;

    public override string ToString() => $"{Time:HH:mm:ss.fff} {LogLevels.Abbreviation(Level)} {Message}";
}

/// <summary>日志落点（<see cref="LogBuffer"/> 实现它；引擎只依赖该接口）。</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>日志写入语法糖：空安全 + 异常隔离（写日志失败绝不能影响主流程）。</summary>
public static class LogSinkExtensions
{
    public static void Write(
        this ILogSink? sink,
        LogLevel level,
        string source,
        string message,
        string? category = null,
        string? details = null,
        LogThumbnail? thumbnail = null)
    {
        if (sink is null || string.IsNullOrEmpty(message))
        {
            return;
        }

        try
        {
            sink.Write(new LogEntry
            {
                Level = level,
                Source = source,
                Category = category ?? string.Empty,
                Message = message,
                Details = details,
                Thumbnail = thumbnail,
            });
        }
        catch
        {
            // 日志系统自身异常一律吞掉：不能因为写日志把执行链路带崩
        }
    }

    public static void Info(this ILogSink? sink, string source, string message, string? category = null, string? details = null)
        => sink.Write(LogLevel.Info, source, message, category, details);

    public static void Warn(this ILogSink? sink, string source, string message, string? category = null, string? details = null)
        => sink.Write(LogLevel.Warn, source, message, category, details);

    public static void Error(this ILogSink? sink, string source, string message, string? category = null, string? details = null)
        => sink.Write(LogLevel.Error, source, message, category, details);

    public static void Debug(this ILogSink? sink, string source, string message, string? category = null, string? details = null)
        => sink.Write(LogLevel.Debug, source, message, category, details);
}

/// <summary>
/// 给日志打实例标签：多实例共用一个 <see cref="LogBuffer"/> 时，
/// 每个引擎拿到自己的 scoped sink，条目自动带上实例名。
/// </summary>
/// <param name="inner">真正的落点。</param>
/// <param name="scope">实例名（空串表示系统级）。</param>
public sealed class ScopedLogSink(ILogSink inner, string scope) : ILogSink
{
    private readonly ILogSink _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Scope { get; } = scope ?? string.Empty;

    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (Scope.Length > 0)
        {
            entry.Scope = Scope;
        }

        _inner.Write(entry);
    }
}
