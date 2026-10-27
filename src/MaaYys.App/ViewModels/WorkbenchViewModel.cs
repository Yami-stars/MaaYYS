using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.Engine;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 主界面 / 工作台（P6.1）：左侧任务链 + 右侧「连接 / 截图 / 日志」三面板（均可折叠）。
/// <para>
/// 三块数据都是**既有唯一数据源**的投影，不复制状态：
/// 任务链复用任务页的 <see cref="TasksViewModel"/>（同一个队列对象，任一处改动两边同时生效）；
/// 截图直接用当前实例的 <see cref="InstanceViewModel.PreviewSource"/>（预览泵按「激活实例」跑，与页面无关）；
/// 日志按 <see cref="LogBuffer.Version"/> 轮询增量，只保留最近 <see cref="LogTailLimit"/> 条。
/// </para>
/// </summary>
public sealed class WorkbenchViewModel : ViewModelBase, IDisposable
{
    /// <summary>面板里保留的日志条数（完整历史在日志页，这里只做「瞥一眼」）。</summary>
    public const int LogTailLimit = 200;

    private readonly InstanceWorkspace? _workspace;
    private readonly Action? _openLogs;
    private readonly DispatcherTimer? _logTimer;

    private long _lastSeq;
    private long _logVersion = -1;
    private bool _disposed;

    public WorkbenchViewModel()
        : this(null, null, null)
    {
    }

    public WorkbenchViewModel(InstanceWorkspace? workspace, TasksViewModel? tasks, Action? openLogs)
    {
        _workspace = workspace;
        _openLogs = openLogs;

        // 任务链与任务页共用同一个 VM：否则工作台里加一项、任务页看不到
        Tasks = tasks ?? new TasksViewModel(workspace);

        ConnectionPanel = PanelStateViewModel.AutoHeight("连接", "⇄");
        ScreenshotPanel = PanelStateViewModel.Star("截图", "▣", 3);
        LogPanel = PanelStateViewModel.Star("日志", "☰", 2);

        OpenLogsCommand = new RelayCommand(_ => _openLogs?.Invoke());
        ClearLogCommand = new RelayCommand(_ => ClearTail());

        if (workspace is not null)
        {
            workspace.CurrentChanged += OnCurrentChanged;

            // 轮询而非订阅：日志写入来自引擎/agent/定时多个线程，逐条回调会在重负载时打爆 UI 线程
            _logTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _logTimer.Tick += (_, _) => PumpLogTail();
            _logTimer.Start();
            PumpLogTail();
        }
    }

    /// <summary>共享任务页（队列 + 运行命令）。</summary>
    public TasksViewModel Tasks { get; }

    public PanelStateViewModel ConnectionPanel { get; }

    public PanelStateViewModel ScreenshotPanel { get; }

    public PanelStateViewModel LogPanel { get; }

    /// <summary>日志面板的行（环形上限 <see cref="LogTailLimit"/>）。</summary>
    public ObservableCollection<LogRowViewModel> LogTail { get; } = [];

    /// <summary>面板标题栏的条数文案（ObservableCollection.Count 不会通知，故单独给一个属性）。</summary>
    public string LogTailText => LogTail.Count == 0 ? "暂无日志" : $"最近 {LogTail.Count} 条";

    /// <summary>当前实例（连接面板与截图面板都指向它）。</summary>
    public InstanceViewModel? CurrentInstance => _workspace?.Current;

    public bool HasInstance => CurrentInstance is not null;

    /// <summary>运行库版本（连接面板底部小字；缺库时降级为提示，不影响界面）。</summary>
    public string RuntimeVersion
    {
        get
        {
            try
            {
                return MaaFramework.Version;
            }
            catch (Exception ex)
            {
                return $"运行库不可用：{ex.Message}";
            }
        }
    }

    public ICommand OpenLogsCommand { get; }

    public ICommand ClearLogCommand { get; }

    /// <summary>按名称折叠面板（连接 / 截图 / 日志）。用于界面自检与后续快捷键。</summary>
    public bool CollapsePanel(string name)
    {
        PanelStateViewModel? panel = name switch
        {
            "连接" or "connection" => ConnectionPanel,
            "截图" or "screenshot" => ScreenshotPanel,
            "日志" or "log" => LogPanel,
            _ => null,
        };

        if (panel is null)
        {
            return false;
        }

        panel.IsExpanded = false;
        return true;
    }

    /// <summary>增量拉取日志尾（UI 线程调用）。</summary>
    public void PumpLogTail()
    {
        LogBuffer? log = _workspace?.Log;
        if (log is null)
        {
            return;
        }

        long version = log.Version;
        if (version == _logVersion)
        {
            return;
        }

        _logVersion = version;

        // 清空后序号不重置，但缓冲变空：直接同步一次，避免面板留着已清空的旧行
        if (log.Count == 0)
        {
            LogTail.Clear();
            _lastSeq = log.LastSeq;
            OnPropertyChanged(nameof(LogTailText));
            return;
        }

        LogOptions options = log.Options;
        LogEntry[] fresh = log.SnapshotSince(_lastSeq, LogTailLimit + 1);
        if (fresh.Length == 0)
        {
            return;
        }

        _lastSeq = fresh[^1].Seq;

        if (fresh.Length > LogTailLimit)
        {
            // 一次积压超过面板上限：整体重建，省掉「先追加 500 条再删 500 条」
            Rebuild(fresh[^LogTailLimit..], options);
            OnPropertyChanged(nameof(LogTailText));
            return;
        }

        foreach (LogEntry entry in fresh)
        {
            LogTail.Add(new LogRowViewModel(entry, options));
        }

        int overflow = LogTail.Count - LogTailLimit;
        for (int i = 0; i < overflow; i++)
        {
            LogTail.RemoveAt(0);
        }

        OnPropertyChanged(nameof(LogTailText));
    }

    private void Rebuild(LogEntry[] entries, LogOptions options)
    {
        LogTail.Clear();
        foreach (LogEntry entry in entries)
        {
            LogTail.Add(new LogRowViewModel(entry, options));
        }
    }

    private void ClearTail()
    {
        LogTail.Clear();
        _lastSeq = _workspace?.Log.LastSeq ?? 0;
        OnPropertyChanged(nameof(LogTailText));
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentInstance));
        OnPropertyChanged(nameof(HasInstance));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_logTimer is not null)
        {
            _logTimer.Stop();
        }

        if (_workspace is not null)
        {
            _workspace.CurrentChanged -= OnCurrentChanged;
        }
    }
}
