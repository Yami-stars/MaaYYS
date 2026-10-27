namespace MaaYys.Engine.Logging;

/// <summary>
/// 引擎日志管线（P5.5）：把「原生事件 / agent 输出 / 引擎自身状态」统一成日志条目，
/// 并按策略决定级别、是否记录、是否附截图卡片。
/// <para>
/// 为什么要这一层：<see cref="MaaYysEngine"/> 只该负责「我收到了什么」，
/// 翻译规则、节点事件开关、缩略图阈值都放在这里 —— 于是这些规则可以脱离引擎实例单测。
/// </para>
/// </summary>
public sealed class EngineLogPipeline
{
    private readonly ILogSink _sink;

    public EngineLogPipeline(ILogSink sink, LogOptions options, Func<LogThumbnail?>? thumbnailProvider = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        ThumbnailProvider = thumbnailProvider;
    }

    /// <summary>最终落点（通常是被 <see cref="ScopedLogSink"/> 包过的 <see cref="LogBuffer"/>）。</summary>
    public ILogSink Sink => _sink;

    /// <summary>策略（与界面共享同一实例，改开关立即生效）。</summary>
    public LogOptions Options { get; }

    /// <summary>
    /// 截图来源：返回「最近一帧」的缩略图。挂在帧回调上取缓存，
    /// 不在框架回调线程里现抓图（那会在任务执行中重入控制器）。
    /// </summary>
    public Func<LogThumbnail?>? ThumbnailProvider { get; set; }

    /// <summary>写入一条原生框架事件（节点级事件按 <see cref="LogOptions.NodeEvents"/> 过滤）。</summary>
    public void Framework(string? message, string? detailsJson)
    {
        LogEntry? entry = FrameworkLogTranslator.Translate(message, detailsJson, Options);
        if (entry is null)
        {
            return;
        }

        Write(entry.Level, entry.Source, entry.Message, entry.Category, entry.Details);
    }

    /// <summary>写入一行 agent 子进程输出（stdout / stderr）。</summary>
    public void Agent(string? line, bool isError = false)
    {
        if (!AgentLogParser.IsMeaningful(line))
        {
            return;
        }

        LogLevel level = AgentLogParser.DetectLevel(line, isError ? LogLevel.Error : LogLevel.Info);
        Write(level, "Agent", AgentLogParser.Clean(line), "agent");
    }

    /// <summary>写入引擎自身日志（连接、资源、启停等）。</summary>
    public void Write(LogLevel level, string source, string message, string? category = null, string? details = null)
        => _sink.Write(level, source, message, category, details, Shot(level));

    public void Info(string source, string message, string? category = null, string? details = null)
        => Write(LogLevel.Info, source, message, category, details);

    public void Warn(string source, string message, string? category = null, string? details = null)
        => Write(LogLevel.Warn, source, message, category, details);

    public void Error(string source, string message, string? category = null, string? details = null)
        => Write(LogLevel.Error, source, message, category, details);

    public void Debug(string source, string message, string? category = null, string? details = null)
        => Write(LogLevel.Debug, source, message, category, details);

    /// <summary>只有警告/错误才附图：信息级配图会在长跑里把内存吃光。</summary>
    private LogThumbnail? Shot(LogLevel level)
    {
        if (level < LogLevel.Warn || !Options.Thumbnails || ThumbnailProvider is null)
        {
            return null;
        }

        try
        {
            return ThumbnailProvider();
        }
        catch
        {
            // 取图失败不能拖累日志本身
            return null;
        }
    }
}
