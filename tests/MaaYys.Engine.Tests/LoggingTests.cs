using MaaYys.Engine.Config;
using MaaYys.Engine.Devices;
using MaaYys.Engine.Logging;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>日志环形缓冲（P5.5）：容量淘汰、增量查询、过滤、截图卡片上限。</summary>
public sealed class LogBufferTests
{
    private static LogBuffer Buffer(int capacity = 100, int maxThumbnails = 300) => new(new LogOptions
    {
        Capacity = capacity,
        MaxThumbnails = maxThumbnails,
    });

    private static LogThumbnail Thumb() => new(new byte[4 * 3], 2, 2, 3);

    [Fact]
    public void Write_AssignsMonotonicSeqAndCounts()
    {
        LogBuffer buffer = Buffer();

        buffer.Info("Engine", "第一条");
        buffer.Warn("Runner", "第二条");

        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.TotalAppended);
        Assert.Equal(new[] { 1L, 2L }, buffer.Snapshot().Select(e => e.Seq).ToArray());
        Assert.Equal(0, buffer.Dropped);
    }

    [Fact]
    public void Write_DropsOldestBeyondCapacity()
    {
        LogBuffer buffer = Buffer(capacity: 100);

        for (int i = 0; i < 105; i++)
        {
            buffer.Info("Engine", $"第 {i} 条");
        }

        Assert.Equal(100, buffer.Count);
        Assert.Equal(5, buffer.Dropped);

        LogEntry[] snapshot = buffer.Snapshot();
        Assert.Equal("第 5 条", snapshot[0].Message);
        Assert.Equal("第 104 条", snapshot[^1].Message);
    }

    [Fact]
    public void SnapshotSince_ReturnsOnlyNewerEntries()
    {
        LogBuffer buffer = Buffer();
        buffer.Info("Engine", "A");
        buffer.Info("Engine", "B");
        long mark = buffer.LastSeq;

        buffer.Info("Engine", "C");

        LogEntry[] fresh = buffer.SnapshotSince(mark);
        Assert.Equal("C", Assert.Single(fresh).Message);
    }

    [Fact]
    public void Query_FiltersByLevelSourceAndKeyword()
    {
        LogBuffer buffer = Buffer();
        buffer.Debug("Engine", "识别未命中：主界面");
        buffer.Info("Runner", "完成：自动御魂");
        buffer.Warn("Timer", "跳过：每日御魂");
        buffer.Error("Runner", "失败：自动御魂");

        Assert.Equal(2, buffer.Query(LogLevel.Warn).Length);
        Assert.Single(buffer.Query(LogLevel.Trace, source: "Timer"));
        Assert.Equal(2, buffer.Query(LogLevel.Trace, keyword: "自动御魂").Length);
        Assert.Equal(2, buffer.Query(LogLevel.Trace, source: "Runner").Length);
        Assert.Single(buffer.Query(LogLevel.Trace, keyword: "识别"));
    }

    [Fact]
    public void Query_TakesNewestWhenLimited()
    {
        LogBuffer buffer = Buffer();
        for (int i = 0; i < 10; i++)
        {
            buffer.Info("Engine", $"第 {i} 条");
        }

        LogEntry[] lastTwo = buffer.Query(LogLevel.Trace, max: 2);

        Assert.Equal(2, lastTwo.Length);
        Assert.Equal("第 8 条", lastTwo[0].Message);
        Assert.Equal("第 9 条", lastTwo[1].Message);
    }

    [Fact]
    public void Thumbnails_AreDroppedBeyondLimitButEntryKept()
    {
        LogBuffer buffer = Buffer(maxThumbnails: 2);

        buffer.Write(LogLevel.Error, "Engine", "失败 1", thumbnail: Thumb());
        buffer.Write(LogLevel.Error, "Engine", "失败 2", thumbnail: Thumb());
        buffer.Write(LogLevel.Error, "Engine", "失败 3", thumbnail: Thumb());

        LogEntry[] snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Length);
        Assert.NotNull(snapshot[0].Thumbnail);
        Assert.NotNull(snapshot[1].Thumbnail);
        Assert.Null(snapshot[2].Thumbnail);   // 超上限只丢图，不丢日志
    }

    [Fact]
    public void Thumbnails_DisabledByOption()
    {
        LogBuffer buffer = new(new LogOptions { Thumbnails = false });

        buffer.Write(LogLevel.Error, "Engine", "失败", thumbnail: Thumb());

        Assert.Null(Assert.Single(buffer.Snapshot()).Thumbnail);
    }

    [Fact]
    public void Clear_KeepsSeqMonotonic()
    {
        LogBuffer buffer = Buffer();
        buffer.Info("Engine", "A");
        buffer.Info("Engine", "B");

        buffer.Clear();
        Assert.Equal(0, buffer.Count);

        buffer.Info("Engine", "C");

        LogEntry entry = Assert.Single(buffer.Snapshot());
        Assert.Equal("C", entry.Message);
        Assert.Equal(3, entry.Seq);   // 序号不回退，界面不会把新条目当旧条目
    }

    [Fact]
    public void Scope_IsTaggedByScopedSink()
    {
        LogBuffer buffer = Buffer();
        var scoped = new ScopedLogSink(buffer, "实例A");

        scoped.Info("Engine", "已连接");

        Assert.Equal("实例A", Assert.Single(buffer.Snapshot()).Scope);
    }
}

/// <summary>原生事件 → 日志条目的翻译规则。</summary>
public sealed class FrameworkLogTranslatorTests
{
    [Fact]
    public void NodeEvents_AreFilteredByDefault()
    {
        LogEntry? entry = FrameworkLogTranslator.Translate(
            "Node.Recognition.Succeeded", """{"task_id":1,"node_name":"主界面"}""", new LogOptions());

        Assert.Null(entry);
    }

    [Fact]
    public void NodeEvents_AreKeptWhenEnabled()
    {
        LogEntry entry = Assert.IsType<LogEntry>(FrameworkLogTranslator.Translate(
            "Node.Recognition.Succeeded",
            """{"task_id":1,"node_name":"主界面"}""",
            new LogOptions { NodeEvents = true }));

        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("识别成功：主界面", entry.Message);
        Assert.Equal("Node.Recognition.Succeeded", entry.Category);
    }

    [Fact]
    public void TaskFailed_IsErrorWithEntryName()
    {
        LogEntry entry = Assert.IsType<LogEntry>(FrameworkLogTranslator.Translate(
            "Tasker.Task.Failed", """{"task_id":7,"entry":"自动御魂"}""", new LogOptions()));

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("任务 #7 失败：自动御魂", entry.Message);
        Assert.Equal("""{"task_id":7,"entry":"自动御魂"}""", entry.Details);
    }

    [Fact]
    public void TaskStarting_DescribesWithoutEntry()
    {
        LogEntry entry = Assert.IsType<LogEntry>(FrameworkLogTranslator.Translate(
            "Tasker.Task.Starting", """{"task_id":3}""", new LogOptions()));

        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.Equal("任务 #3 开始", entry.Message);
    }

    [Fact]
    public void MalformedDetails_DoesNotThrow()
    {
        LogEntry entry = Assert.IsType<LogEntry>(FrameworkLogTranslator.Translate(
            "Tasker.Task.Succeeded", "{not json", new LogOptions()));

        Assert.Equal("任务 完成", entry.Message);
        Assert.Equal("{not json", entry.Details);
    }

    [Fact]
    public void UnknownMessage_KeepsRawName()
    {
        LogEntry entry = Assert.IsType<LogEntry>(FrameworkLogTranslator.Translate(
            "Something.New.Event", null, new LogOptions()));

        Assert.Equal("Something.New.Event", entry.Message);
        Assert.Equal(LogLevel.Debug, entry.Level);
    }
}

/// <summary>日志管线：agent 输出合并 + 截图卡片策略。</summary>
public sealed class EngineLogPipelineTests
{
    [Fact]
    public void Agent_UsesLevelFromSlogLine()
    {
        LogBuffer buffer = new();
        var pipeline = new EngineLogPipeline(buffer, new LogOptions());

        pipeline.Agent("""time=2026-10-27T12:00:00+08:00 level=ERROR msg="action failed" name=RepeatChallengeNTimes""");
        pipeline.Agent("time=2026-10-27T12:00:01+08:00 level=INFO msg=done");

        LogEntry[] snapshot = buffer.Snapshot();
        Assert.Equal(LogLevel.Error, snapshot[0].Level);
        Assert.Equal("Agent", snapshot[0].Source);
        Assert.Equal(LogLevel.Info, snapshot[1].Level);
    }

    [Fact]
    public void Agent_SkipsBlankLines()
    {
        LogBuffer buffer = new();
        var pipeline = new EngineLogPipeline(buffer, new LogOptions());

        pipeline.Agent("   ");
        pipeline.Agent(null);

        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Thumbnail_OnlyAttachedForWarnAndError()
    {
        LogBuffer buffer = new();
        var pipeline = new EngineLogPipeline(
            buffer,
            new LogOptions { Thumbnails = true, ThumbnailWidth = 2 },
            () => new LogThumbnail(new byte[2 * 2 * 3], 2, 2, 3));

        pipeline.Info("Runner", "完成");
        pipeline.Warn("Runner", "跳过");
        pipeline.Error("Runner", "失败");

        LogEntry[] snapshot = buffer.Snapshot();
        Assert.Null(snapshot[0].Thumbnail);
        Assert.NotNull(snapshot[1].Thumbnail);
        Assert.NotNull(snapshot[2].Thumbnail);
    }

    [Fact]
    public void Thumbnail_ProviderFailureIsSwallowed()
    {
        LogBuffer buffer = new();
        var pipeline = new EngineLogPipeline(
            buffer,
            new LogOptions { Thumbnails = true },
            () => throw new InvalidOperationException("取图失败"));

        pipeline.Error("Runner", "失败");

        Assert.Null(Assert.Single(buffer.Snapshot()).Thumbnail);
    }
}

/// <summary>日志导出格式化（文本 / CSV / 截图文件名）。</summary>
public sealed class LogExporterTests
{
    private static LogEntry Entry(long seq, string message, LogLevel level = LogLevel.Info, LogThumbnail? shot = null) => new()
    {
        Seq = seq,
        Time = new DateTimeOffset(2026, 10, 27, 13, 24, 26, 123, TimeSpan.FromHours(8)),
        Level = level,
        Source = "Runner",
        Scope = "实例A",
        Category = "queue",
        Message = message,
        Thumbnail = shot,
    };

    [Fact]
    public void FormatLine_ContainsTimeLevelScopeAndShotMarker()
    {
        string line = LogExporter.FormatLine(
            Entry(42, "失败：自动御魂", LogLevel.Error, new LogThumbnail(new byte[12], 2, 2, 3)),
            new LogOptions { TimeFormat = "HH:mm:ss.fff" });

        Assert.StartsWith("13:24:26.123 [ERROR] Runner 实例A  失败：自动御魂", line);
        Assert.Contains("[截图 shot-000042.png]", line);
    }

    [Fact]
    public void ToCsv_EscapesCommasAndQuotes()
    {
        string csv = LogExporter.ToCsv(
            [Entry(1, "值含,逗号与\"引号\"")],
            new LogOptions());

        Assert.Contains("\"值含,逗号与\"\"引号\"\"\"", csv);
        Assert.StartsWith("时间,级别,来源,实例,分类,消息,详情,截图", csv);
    }

    [Fact]
    public void InvalidTimeFormat_FallsBackToDefault()
    {
        var options = new LogOptions { TimeFormat = "%bad%" };

        string line = LogExporter.FormatLine(Entry(1, "消息"), options);

        Assert.StartsWith("13:24:26.123", line);   // 回退到默认 HH:mm:ss.fff
    }

    [Fact]
    public void ToText_HasHeaderAndOneLinePerEntry()
    {
        string text = LogExporter.ToText(
            [Entry(1, "A"), Entry(2, "B")],
            new LogOptions(),
            "自定义头");

        string[] lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("自定义头", lines[0]);
        Assert.Equal(4, lines.Length);   // 头 + 分隔线 + 2 条
    }

    [Fact]
    public void ShotFileName_IsZeroPadded()
    {
        Assert.Equal("shot-000007.png", LogExporter.ShotFileName(Entry(7, "x")));
    }
}

/// <summary>截图卡片缩放（块平均）。</summary>
public sealed class ThumbnailFactoryTests
{
    private static FrameData Frame(int width, int height, int channels, byte fill)
    {
        var pixels = new byte[width * height * channels];
        Array.Fill(pixels, fill);
        return new FrameData(pixels, width, height, channels, 1, DateTimeOffset.Now,
            new ScreenshotResult(width, height, channels, 0, pixels.Length, 0, 1, 0, 0));
    }

    [Fact]
    public void Downscales_KeepingAspectRatioAndAverageColor()
    {
        FrameData frame = Frame(1280, 720, 3, 200);

        LogThumbnail? thumb = ThumbnailFactory.Create(frame, 320);

        Assert.NotNull(thumb);
        Assert.Equal(320, thumb!.Width);
        Assert.Equal(180, thumb.Height);          // 720 × 320/1280
        Assert.Equal(3, thumb.Channels);
        Assert.All(thumb.Pixels, b => Assert.Equal(200, b));   // 纯色块平均后仍是原值
    }

    [Fact]
    public void SmallFrame_IsNotUpscaled()
    {
        FrameData frame = Frame(100, 50, 4, 10);

        LogThumbnail? thumb = ThumbnailFactory.Create(frame, 320);

        Assert.NotNull(thumb);
        Assert.Equal(100, thumb!.Width);
        Assert.Equal(50, thumb.Height);
    }

    [Fact]
    public void UnsupportedChannels_ReturnNull()
    {
        Assert.Null(ThumbnailFactory.Create(Frame(10, 10, 2, 0), 5));
        Assert.Null(ThumbnailFactory.Create(null, 320));
    }
}

/// <summary>agent 输出解析：Go slog / 括号前缀 / 纯文本。</summary>
public sealed class AgentLogParserTests
{
    [Theory]
    [InlineData("level=ERROR msg=\"x\"", LogLevel.Error)]
    [InlineData("""time=1 level=warn msg=x""", LogLevel.Warn)]
    [InlineData("[WARN] 端口被占用", LogLevel.Warn)]
    [InlineData("INFO: agent 已启动", LogLevel.Info)]
    [InlineData("普通一行输出", LogLevel.Info)]
    public void DetectLevel_HandlesCommonFormats(string line, LogLevel expected)
        => Assert.Equal(expected, AgentLogParser.DetectLevel(line));

    [Fact]
    public void DetectLevel_FallsBackToStderrDefault()
        => Assert.Equal(LogLevel.Error, AgentLogParser.DetectLevel("无可识别前缀", LogLevel.Error));

    [Fact]
    public void Clean_TrimsTrailingNewline()
        => Assert.Equal("hello", AgentLogParser.Clean("hello\r\n"));
}

/// <summary>日志策略持久化（config/maayys.json 的 log 段）。</summary>
public sealed class ConfigurationStoreLogTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ConfigurationStoreLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "maayys.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响结论
        }
    }

    private ConfigurationStore LoadedStore()
    {
        ConfigurationStore store = new(_path);
        store.Load();
        return store;
    }

    [Fact]
    public void LogOptions_RoundTripThroughFile()
    {
        ConfigurationStore store = LoadedStore();
        store.UpdateLogOptions(new LogOptions
        {
            TimeFormat = "yyyy-MM-dd HH:mm:ss",
            Capacity = 5000,
            NodeEvents = true,
            Thumbnails = false,
            ThumbnailWidth = 240,
            MaxThumbnails = 50,
        });

        LogOptions loaded = LoadedStore().LogOptions;

        Assert.Equal("yyyy-MM-dd HH:mm:ss", loaded.TimeFormat);
        Assert.Equal(5000, loaded.Capacity);
        Assert.True(loaded.NodeEvents);
        Assert.False(loaded.Thumbnails);
        Assert.Equal(240, loaded.ThumbnailWidth);
        Assert.Equal(50, loaded.MaxThumbnails);
    }

    [Fact]
    public void InvalidTimeFormat_FallsBackAndRecordsLoadError()
    {
        File.WriteAllText(
            _path,
            """
            {
              "config_version": 1,
              "current": "默认配置",
              "configurations": { "默认配置": { "name": "默认配置", "tasks": [] } },
              "log": { "time_format": "%oops%", "capacity": 10 }
            }
            """,
            new System.Text.UTF8Encoding(false));

        ConfigurationStore store = LoadedStore();

        Assert.Equal(LogOptions.DefaultTimeFormat, store.LogOptions.TimeFormat);
        Assert.Equal(100, store.LogOptions.Capacity);   // 夹取到下限
        Assert.Contains(store.LoadErrors, e => e.Contains("日志时间格式"));
    }

    [Fact]
    public void Defaults_WhenNoLogSection()
    {
        ConfigurationStore store = LoadedStore();

        Assert.Equal(LogOptions.DefaultTimeFormat, store.LogOptions.TimeFormat);
        Assert.Equal(20_000, store.LogOptions.Capacity);
        Assert.False(store.LogOptions.NodeEvents);
        Assert.True(store.LogOptions.Thumbnails);
    }
}
