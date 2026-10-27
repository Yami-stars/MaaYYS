using System.Text;

namespace MaaYys.Engine.Logging;

/// <summary>
/// 文件日志落点（P6.2）：把内存日志同时写到磁盘，支持按大小滚动与保留份数。
/// <para>
/// 设计取舍：
/// <list type="bullet">
/// <item><b>策略是「同一实例」引用</b>（<see cref="FileLogSettings"/>）——和 <see cref="LogOptions"/> 一样，
/// 界面改完立即对写入端生效，不需要重启或重连。</item>
/// <item><b>写失败只降级不抛</b>：磁盘满 / 目录被删 / 权限不足都会把 <see cref="LastError"/> 记下来并自动关闭，
/// 日志落盘绝不能反过来影响任务执行。</item>
/// <item><b>文件名带会话启动时刻</b>（<c>maayys-20261027-193000.log</c>），滚动追加 <c>.p2</c>、
/// <c>.p3</c> 后缀；每次开新文件时按最后写入时间清理超出保留份数的旧文件。</item>
/// </list>
/// </para>
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    /// <summary>单行最大字符数：超长细节（原生 details_json）截断，避免一条日志撑爆文件。</summary>
    public const int MaxLineLength = 8_000;

    private readonly object _gate = new();
    private readonly FileLogSettings _settings;
    private readonly string _baseDirectory;
    private readonly LogOptions _format;
    private readonly Func<DateTimeOffset> _clock;

    private StreamWriter? _writer;
    private string? _currentPath;
    private long _written;
    private int _part;
    private string _sessionStamp = string.Empty;
    private bool _broken;
    private bool _disposed;

    public FileLogSink(FileLogSettings settings, string baseDirectory, LogOptions? format = null, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _baseDirectory = baseDirectory ?? throw new ArgumentNullException(nameof(baseDirectory));
        _format = format ?? new LogOptions();
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>当前正在写的文件（未开启 / 尚未写入时为 null）。</summary>
    public string? CurrentPath
    {
        get
        {
            lock (_gate)
            {
                return _currentPath;
            }
        }
    }

    /// <summary>最近一次 IO 失败原因（成功写入后清空；界面据此提示「落盘已停」）。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _broken ? _lastError : null;
            }
        }
    }

    private string? _lastError;

    /// <summary>当前文件已写字节数（滚动判断用）。</summary>
    public long WrittenBytes
    {
        get
        {
            lock (_gate)
            {
                return _written;
            }
        }
    }

    /// <summary>
    /// 滚动阈值（字节）。默认按设置里的 MB 换算；测试可改小以便几十字节就触发滚动。
    /// </summary>
    internal long MaxBytes
    {
        get => _maxBytesOverride;
        set => _maxBytesOverride = value;
    }

    private long _maxBytesOverride;

    private long EffectiveMaxBytes => _maxBytesOverride > 0
        ? _maxBytesOverride
        : _settings.MaxFileSizeMb * 1024L * 1024L;

    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Level < _settings.MinLevel)
        {
            return;
        }

        string line = LogExporter.FormatLine(entry, _format);
        if (line.Length > MaxLineLength)
        {
            line = line[..MaxLineLength] + "…";
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_settings.Enabled)
            {
                CloseWriter();
                return;
            }

            try
            {
                EnsureWriter();

                // 已经写了内容且这一行会超上限 → 先换分卷（空文件不滚，否则小阈值会无限空转）
                if (_written > 0 && _written + line.Length + Environment.NewLine.Length > EffectiveMaxBytes)
                {
                    Roll();
                    EnsureWriter();
                }

                _writer!.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;
                _broken = false;
                _lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>把缓冲刷到磁盘（导出 / 退出前调用；正常写入已自动 flush）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Fail(ex);
            }
        }
    }

    /// <summary>目录被用户改了之后重建写入端（界面「打开目录」/ 改路径后调用）。</summary>
    public void Restart()
    {
        lock (_gate)
        {
            CloseWriter();
            _sessionStamp = string.Empty;
            _part = 0;
            _broken = false;
            _lastError = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    // ---- 内部 ----

    /// <summary>确保有可写通道；返回 true 表示新建了文件（需要做保留份数清理）。</summary>
    private bool EnsureWriter()
    {
        if (_writer is not null && _currentPath is not null)
        {
            return false;
        }

        string directory = _settings.ResolveDirectory(_baseDirectory);
        Directory.CreateDirectory(directory);

        if (_sessionStamp.Length == 0)
        {
            _sessionStamp = _clock().ToString("yyyyMMdd-HHmmss");
        }

        string name = _part == 0
            ? $"maayys-{_sessionStamp}.log"
            : $"maayys-{_sessionStamp}.p{_part + 1}.log";

        string path = Path.Combine(directory, name);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _currentPath = path;

        PruneOldFiles(directory);
        return true;
    }

    /// <summary>关掉当前文件并换下一个分卷。</summary>
    private void Roll()
    {
        CloseWriter();
        _part++;
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 关闭失败无所谓：文件已经写进去了
        }
        finally
        {
            _writer = null;
            _currentPath = null;
            _written = 0;
        }
    }

    private void Fail(Exception ex)
    {
        _broken = true;
        _lastError = ex.Message;
        CloseWriter();
    }

    /// <summary>保留最近 <see cref="FileLogSettings.MaxFiles"/> 份（不含正在写的那个）。</summary>
    private void PruneOldFiles(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("maayys-*.log")
                .Where(f => !string.Equals(f.FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(Math.Max(0, _settings.MaxFiles - 1))
                .ToList();

            foreach (FileInfo file in files)
            {
                file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写入
        }
    }
}
