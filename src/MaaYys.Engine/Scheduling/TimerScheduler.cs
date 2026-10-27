namespace MaaYys.Engine.Scheduling;

/// <summary>定时触发事件参数。</summary>
public sealed class TimerFiredEventArgs(TimerEntry entry, DateTimeOffset when) : EventArgs
{
    /// <summary>命中的计划（<see cref="TimerEntry.LastFiredKey"/> 已更新为本次分钟键）。</summary>
    public TimerEntry Entry { get; } = entry;

    /// <summary>触发时刻。</summary>
    public DateTimeOffset When { get; } = when;
}

/// <summary>
/// 定时调度器（P5.4）：按固定间隔轮询，命中「时:分 + 周计划」就发一次事件。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>不依赖 UI 与框架，时钟可注入 → 测试里直接调 <see cref="Poll"/> 精确控制时间。</item>
/// <item>同一分钟只触发一次（分钟键去重），且分钟键会随配置落盘 → 重启不会补触发。</item>
/// <item>轮询间隔默认 20 秒：1 分钟内至少检查 3 次，容忍系统休眠造成的 1~2 分钟漂移。</item>
/// </list>
/// </para>
/// </summary>
public sealed class TimerScheduler : IDisposable
{
    /// <summary>默认轮询间隔。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(20);

    private readonly Func<IReadOnlyList<TimerEntry>> _entriesProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    /// <summary>内存去重：即使订阅方没有回写 <c>last_fired</c>，同一分钟也不会重复触发。</summary>
    private readonly Dictionary<string, string> _firedKeys = new(StringComparer.Ordinal);

    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    public TimerScheduler(Func<IReadOnlyList<TimerEntry>> entriesProvider, Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(entriesProvider);
        _entriesProvider = entriesProvider;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    /// <summary>是否正在轮询。</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    /// <summary>命中一条计划（在轮询线程上触发，订阅方需自行切 UI 线程）。</summary>
    public event EventHandler<TimerFiredEventArgs>? Fired;

    /// <summary>每轮检查结束（界面刷新「下次触发」用）。</summary>
    public event EventHandler? Ticked;

    /// <summary>开始轮询（重复调用无副作用）。</summary>
    public void Start(TimeSpan? interval = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_timer is not null)
            {
                return;
            }

            TimeSpan period = interval is { TotalMilliseconds: >= 1000 } custom ? custom : DefaultInterval;
            _timer = new Timer(_ => PollSafe(), null, TimeSpan.Zero, period);
        }
    }

    /// <summary>停止轮询。</summary>
    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// 检查一次并触发命中项（测试与「立即检查」按钮用）。返回本次命中的条目。
    /// </summary>
    public IReadOnlyList<TimerEntry> Poll(DateTimeOffset now)
    {
        IReadOnlyList<TimerEntry> entries;
        try
        {
            entries = _entriesProvider() ?? [];
        }
        catch
        {
            return [];
        }

        List<TimerEntry> fired = [];
        string key = TimerRules.MinuteKey(now);
        foreach (TimerEntry entry in entries)
        {
            if (!TimerRules.ShouldFire(entry, now))
            {
                continue;
            }

            if (_firedKeys.TryGetValue(entry.Id ?? string.Empty, out string? last) &&
                string.Equals(last, key, StringComparison.Ordinal))
            {
                continue;
            }

            entry.LastFiredKey = key;
            _firedKeys[entry.Id ?? string.Empty] = key;
            fired.Add(entry);
        }

        // 分钟键变了就丢掉旧记录，字典不随运行时长增长
        if (_firedKeys.Count > 0)
        {
            foreach (string staleId in _firedKeys.Where(kv => !string.Equals(kv.Value, key, StringComparison.Ordinal))
                         .Select(kv => kv.Key).ToList())
            {
                _firedKeys.Remove(staleId);
            }
        }

        foreach (TimerEntry entry in fired)
        {
            try
            {
                Fired?.Invoke(this, new TimerFiredEventArgs(entry, now));
            }
            catch
            {
                // 订阅方异常不应拖垮调度线程
            }
        }

        try
        {
            Ticked?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 同上
        }

        return fired;
    }

    /// <summary>按注入的时钟检查一次。</summary>
    public IReadOnlyList<TimerEntry> Poll() => Poll(_clock());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void PollSafe()
    {
        // 订阅方可能阻塞（写盘 / 起任务），不允许并发轮询
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            Poll();
        }
        catch
        {
            // 定时线程不能抛：一次异常不该让后续周期停摆
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }
}
