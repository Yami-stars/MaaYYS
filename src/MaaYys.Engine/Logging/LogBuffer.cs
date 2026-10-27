namespace MaaYys.Engine.Logging;

/// <summary>
/// 日志环形缓冲（P5.5）：固定容量、超出丢最旧、序号单调递增。
/// <para>
/// 长跑 2h 不卡顿的三条约束：
/// 1. 只保留最近 <see cref="LogOptions.Capacity"/> 条（默认 2 万），内存有上界；
/// 2. 淘汰用「头指针 + 批量压实」而不是每次 <c>RemoveAt(0)</c>，追加是 O(1)；
/// 3. 界面不逐条订阅，而是按 <see cref="Version"/> 轮询增量（见 LogsViewModel）。
/// </para>
/// 线程安全：引擎回调线程、定时线程、UI 线程都会写，全部走锁；查询返回快照数组。
/// </summary>
public sealed class LogBuffer : ILogSink
{
    private readonly object _gate = new();
    private readonly List<LogEntry> _entries = [];
    private LogOptions _options;
    private int _head;
    private long _seq;
    private long _appended;
    private long _dropped;
    private int _thumbCount;
    private long _version;
    private ILogSink? _mirror;

    public LogBuffer(LogOptions? options = null)
    {
        _options = (options ?? new LogOptions()).Sanitized();
    }

    /// <summary>策略（共享同一实例时，改时间格式/容量对写入端立即生效）。</summary>
    public LogOptions Options
    {
        get
        {
            lock (_gate)
            {
                return _options;
            }
        }

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                _options = value.Sanitized();
                Trim();
                _version++;
            }
        }
    }

    /// <summary>版本号：每次追加 / 清空自增，界面据此判断要不要拉增量。</summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>当前条数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count - _head;
            }
        }
    }

    /// <summary>累计写入条数（含已被淘汰的）。</summary>
    public long TotalAppended
    {
        get
        {
            lock (_gate)
            {
                return _appended;
            }
        }
    }

    /// <summary>因容量上限被淘汰的条数。</summary>
    public long Dropped
    {
        get
        {
            lock (_gate)
            {
                return _dropped;
            }
        }
    }

    /// <summary>最后一条的序号（空缓冲为 0）。</summary>
    public long LastSeq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary>
    /// 镜像落点（P6.2 文件日志）：写入本缓冲的条目同时转发一份给 <see cref="FileLogSink"/>。
    /// <para>
    /// 转发在锁外进行 —— 磁盘 IO 不能拖住其他写日志的线程；代价是并发写入时
    /// 文件里的顺序可能与内存里的序号顺序略有出入（单线程顺序不受影响）。
    /// 转发异常在这里吞掉：落盘失败绝不能反过来影响调用方。
    /// </para>
    /// </summary>
    public ILogSink? Mirror
    {
        get => _mirror;
        set => _mirror = ReferenceEquals(value, this) ? null : value;
    }

    public void Write(LogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_gate)
        {
            // 容量/缩略图策略在写入端统一执行：调用方不必关心上限
            if (entry.Thumbnail is not null)
            {
                if (!_options.Thumbnails || _thumbCount >= _options.MaxThumbnails)
                {
                    entry = Strip(entry);
                }
                else
                {
                    _thumbCount++;
                }
            }

            entry.Seq = ++_seq;
            _entries.Add(entry);
            _appended++;
            _version++;

            Trim();
        }

        if (_mirror is { } mirror)
        {
            try
            {
                mirror.Write(entry);
            }
            catch
            {
                // 落盘失败只降级，不影响内存日志与调用方
            }
        }
    }

    /// <summary>追加便捷入口（带缩略图参数）。</summary>
    public void Append(
        LogLevel level,
        string source,
        string message,
        string? category = null,
        string? details = null,
        LogThumbnail? thumbnail = null)
        => Write(new LogEntry
        {
            Level = level,
            Source = source,
            Category = category ?? string.Empty,
            Message = message,
            Details = details,
            Thumbnail = thumbnail,
        });

    /// <summary>全量快照（按序号升序）；<paramref name="max"/> &gt; 0 时只取最后 N 条。</summary>
    public LogEntry[] Snapshot(int max = 0)
    {
        lock (_gate)
        {
            int count = _entries.Count - _head;
            if (count <= 0)
            {
                return [];
            }

            int take = max > 0 && max < count ? max : count;
            var result = new LogEntry[take];
            int start = _entries.Count - take;
            _entries.CopyTo(start, result, 0, take);
            return result;
        }
    }

    /// <summary>增量快照：只返回序号大于 <paramref name="afterSeq"/> 的条目。</summary>
    public LogEntry[] SnapshotSince(long afterSeq, int max = 0)
    {
        lock (_gate)
        {
            int index = IndexOfFirstAfter(afterSeq);
            int count = _entries.Count - index;
            if (count <= 0)
            {
                return [];
            }

            int take = max > 0 && max < count ? max : count;
            int start = _entries.Count - take;
            var result = new LogEntry[take];
            _entries.CopyTo(start, result, 0, take);
            return result;
        }
    }

    /// <summary>
    /// 带过滤的快照（界面刷新用）：级别阈值 + 来源 + 关键字（匹配消息/详情/分类）。
    /// <paramref name="max"/> &gt; 0 时**从新到旧**取满 N 条再翻回正序，保证看到的是最新内容。
    /// </summary>
    public LogEntry[] Query(LogLevel minLevel, string? source = null, string? keyword = null, int max = 0)
    {
        lock (_gate)
        {
            var result = new List<LogEntry>();
            for (int i = _entries.Count - 1; i >= _head; i--)
            {
                LogEntry entry = _entries[i];
                if (!Matches(entry, minLevel, source, keyword))
                {
                    continue;
                }

                result.Add(entry);
                if (max > 0 && result.Count >= max)
                {
                    break;
                }
            }

            result.Reverse();
            return [.. result];
        }
    }

    /// <summary>增量 + 过滤（界面轮询用）。</summary>
    public LogEntry[] QuerySince(long afterSeq, LogLevel minLevel, string? source = null, string? keyword = null, int max = 0)
    {
        lock (_gate)
        {
            int index = IndexOfFirstAfter(afterSeq);
            var result = new List<LogEntry>();
            for (int i = index; i < _entries.Count; i++)
            {
                LogEntry entry = _entries[i];
                if (!Matches(entry, minLevel, source, keyword))
                {
                    continue;
                }

                result.Add(entry);
                if (max > 0 && result.Count >= max)
                {
                    break;
                }
            }

            return [.. result];
        }
    }

    /// <summary>清空（序号不重置，避免界面把「清空后新条目」误判为旧条目）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _head = 0;
            _thumbCount = 0;
            _dropped = 0;
            _version++;
        }
    }

    /// <summary>过滤语义集中在这里，界面与测试共用同一份实现。</summary>
    public static bool Matches(LogEntry entry, LogLevel minLevel, string? source, string? keyword)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Level < minLevel)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(source) && !string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return true;
        }

        string needle = keyword.Trim();
        return entry.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || entry.Scope.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (entry.Details?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static LogEntry Strip(LogEntry entry) => new()
    {
        Seq = entry.Seq,
        Time = entry.Time,
        Level = entry.Level,
        Source = entry.Source,
        Category = entry.Category,
        Message = entry.Message,
        Details = entry.Details,
        Scope = entry.Scope,
    };

    /// <summary>从 _head 起找到第一条序号 &gt; afterSeq 的下标（序号单调，二分）。</summary>
    private int IndexOfFirstAfter(long afterSeq)
    {
        int low = _head;
        int high = _entries.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (_entries[mid].Seq <= afterSeq)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>淘汰最旧条目；_head 堆积过半时批量压实，保持追加 O(1)。</summary>
    private void Trim()
    {
        int capacity = _options.Capacity;
        while (_entries.Count - _head > capacity)
        {
            if (_entries[_head].Thumbnail is not null && _thumbCount > 0)
            {
                _thumbCount--;
            }

            _head++;
            _dropped++;
        }

        if (_head > 0 && _head >= capacity)
        {
            _entries.RemoveRange(0, _head);
            _head = 0;
        }
    }
}
