using MaaYys.Engine.Config;
using MaaYys.Engine.Logging;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 日志落盘（P6.2）：级别过滤、按大小滚动、保留份数、策略即时生效、镜像转发。
/// </summary>
public sealed class FileLogSinkTests : IDisposable
{
    private readonly string _root;

    public FileLogSinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-filelog-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不影响结论
        }
    }

    private static LogEntry Entry(string message, LogLevel level = LogLevel.Info)
        => new() { Level = level, Source = "Runner", Message = message, Category = "test" };

    private FileLogSink CreateSink(FileLogSettings? settings = null)
        => new(settings ?? new FileLogSettings { Enabled = true }, _root);

    /// <summary>
    /// 读日志文件：sink 的写句柄一直持有，普通 <see cref="File.ReadAllText(string)"/>
    /// 会因共享模式不兼容而报「文件被占用」，所以这里显式允许读写共享。
    /// </summary>
    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string[] LogFiles()
        => Directory.Exists(Path.Combine(_root, "logs"))
            ? Directory.GetFiles(Path.Combine(_root, "logs"), "maayys-*.log").OrderBy(p => p).ToArray()
            : [];

    [Fact]
    public void Write_CreatesFileUnderDefaultLogsDirectory()
    {
        using var sink = CreateSink();

        sink.Write(Entry("第一条"));
        sink.Write(Entry("第二条"));
        sink.Flush();

        string file = Assert.Single(LogFiles());
        Assert.Contains("logs", file, StringComparison.OrdinalIgnoreCase);
        string text = ReadLog(file);
        Assert.Contains("第一条", text);
        Assert.Contains("第二条", text);
        Assert.Contains("[INFO]", text);
    }

    [Fact]
    public void Write_FiltersBelowMinLevel()
    {
        using var sink = CreateSink(new FileLogSettings { Enabled = true, MinLevel = LogLevel.Warn });

        sink.Write(Entry("调试信息", LogLevel.Debug));
        sink.Write(Entry("普通信息", LogLevel.Info));
        sink.Write(Entry("警告", LogLevel.Warn));
        sink.Flush();

        string text = ReadLog(Assert.Single(LogFiles()));
        Assert.DoesNotContain("调试信息", text);
        Assert.DoesNotContain("普通信息", text);
        Assert.Contains("警告", text);
    }

    [Fact]
    public void DisabledSetting_DoesNotWriteAndClosesFile()
    {
        var settings = new FileLogSettings { Enabled = true };
        using var sink = CreateSink(settings);

        sink.Write(Entry("开启时写入"));
        sink.Flush();
        Assert.NotNull(sink.CurrentPath);

        // 策略是同一实例引用：关掉后立即停止落盘
        settings.Enabled = false;
        sink.Write(Entry("关闭后不该出现"));
        sink.Flush();

        Assert.Null(sink.CurrentPath);
        string text = ReadLog(Assert.Single(LogFiles()));
        Assert.Contains("开启时写入", text);
        Assert.DoesNotContain("关闭后不该出现", text);
    }

    [Fact]
    public void Roll_CreatesNextPartWhenSizeExceeded()
    {
        var settings = new FileLogSettings { Enabled = true };
        using var sink = CreateSink(settings);
        sink.MaxBytes = 240;

        for (int i = 0; i < 12; i++)
        {
            sink.Write(Entry(new string('x', 40)));
        }

        sink.Flush();

        string[] files = LogFiles();
        Assert.True(files.Length >= 2, $"应当滚动出多个文件，实际 {files.Length}");
        Assert.Contains(files, f => f.Contains(".p2.log", StringComparison.Ordinal));
    }

    [Fact]
    public void Prune_KeepsOnlyMaxFiles()
    {
        var settings = new FileLogSettings { Enabled = true, MaxFiles = 2 };
        using var sink = CreateSink(settings);
        sink.MaxBytes = 120;

        for (int i = 0; i < 20; i++)
        {
            sink.Write(Entry(new string('y', 40)));
        }

        sink.Flush();

        // 当前文件 + 最多 1 份历史
        Assert.True(LogFiles().Length <= 2, $"保留份数上限失效：{LogFiles().Length}");
    }

    [Fact]
    public void Restart_PicksUpNewDirectory()
    {
        var settings = new FileLogSettings { Enabled = true };
        using var sink = CreateSink(settings);

        sink.Write(Entry("旧目录"));
        sink.Flush();
        Assert.Contains("logs", sink.CurrentPath!, StringComparison.OrdinalIgnoreCase);

        settings.Directory = "custom-logs";
        sink.Restart();
        sink.Write(Entry("新目录"));
        sink.Flush();

        Assert.Contains("custom-logs", sink.CurrentPath!, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_root, "custom-logs", Path.GetFileName(sink.CurrentPath!))));
    }

    [Fact]
    public void BufferMirror_ForwardsEntriesToFile()
    {
        var settings = new FileLogSettings { Enabled = true };
        using var sink = CreateSink(settings);
        var buffer = new LogBuffer(new LogOptions()) { Mirror = sink };

        buffer.Write(LogLevel.Info, "System", "经过镜像的一行", "mirror");
        sink.Flush();

        Assert.Contains("经过镜像的一行", ReadLog(Assert.Single(LogFiles())));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void BufferMirror_RejectsSelfToAvoidRecursion()
    {
        var buffer = new LogBuffer();
        buffer.Mirror = buffer;
        Assert.Null(buffer.Mirror);

        // 仍然能正常写入（不会自己调自己）
        buffer.Write(LogLevel.Info, "System", "没有任何镜像", "mirror");
        Assert.Equal(1, buffer.Count);
    }
}
