using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Scheduling;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 定时任务页（P5.4）：一行一条计划（时间 / 周计划 / 绑定配置组 / 强制启动），
/// 到点由 <see cref="TimerRunner"/> 驱动队列执行。
/// <para>
/// 调度本身挂在 <see cref="InstanceWorkspace.Timers"/> 上，页面只是编辑与展示视图；
/// 关掉这个页签不代表定时失效（与 MAA 一致）。
/// </para>
/// </summary>
public sealed class TimersViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace? _workspace;
    private readonly SynchronizationContext? _uiContext;

    private string _message = string.Empty;
    private bool _disposed;

    public TimersViewModel()
        : this(null)
    {
    }

    public TimersViewModel(InstanceWorkspace? workspace)
    {
        _workspace = workspace;
        _uiContext = SynchronizationContext.Current;

        AddTimerCommand = new RelayCommand(_ => AddTimer(), _ => ConfigurationNames.Count > 0);
        CheckNowCommand = new RelayCommand(_ => CheckNow(), _ => _workspace is not null);
        ClearMessageCommand = new RelayCommand(_ => Message = string.Empty);

        Reload();

        if (workspace is not null)
        {
            workspace.Timers.Reported += OnReported;
            workspace.Timers.Ticked += OnTicked;
            workspace.Configurations.Changed += OnConfigurationsChanged;
        }
    }

    /// <summary>定时计划行（顺序 = 配置文件里的顺序）。</summary>
    public ObservableCollection<TimerItemViewModel> Timers { get; } = [];

    /// <summary>可绑定的配置组名（来自 <c>config/maayys.json</c>）。</summary>
    public ObservableCollection<string> ConfigurationNames { get; } = [];

    public ICommand AddTimerCommand { get; }

    /// <summary>立即检查一次（不等 20 秒轮询）。</summary>
    public ICommand CheckNowCommand { get; }

    public ICommand ClearMessageCommand { get; }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasTimers => Timers.Count > 0;

    /// <summary>没有定时计划（空状态提示用）。</summary>
    public bool NoTimers => Timers.Count == 0;

    /// <summary>调度状态文案。</summary>
    public string SchedulerText => _workspace is null
        ? "未加载工作区"
        : _workspace.Timers.Running
            ? $"调度运行中（每 {_workspace.Timers.IntervalSeconds} 秒检查一次）"
            : "调度已暂停";

    /// <summary>没有配置组时的提示（此时无法新增计划）。</summary>
    public string ConfigurationHint => ConfigurationNames.Count == 0
        ? "当前没有任何配置组：请先在「任务」页新建一个配置组"
        : $"共 {ConfigurationNames.Count} 个配置组可选";

    // ---- 增删改 ----

    private void AddTimer()
    {
        if (_workspace is null || ConfigurationNames.Count == 0)
        {
            Message = "还没有配置组，无法新增定时计划";
            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        TimerEntry added = _workspace.Configurations.AddTimer(new TimerEntry
        {
            Hour = now.Hour,
            Minute = 0,
            Configuration = ConfigurationNames[0],
            Weekdays = TimerRules.AllWeekdays,
            Enabled = true,
        });

        Timers.Add(CreateRow(added));
        RaiseListStates();
        Message = $"已新增定时计划（{added.Hour:00}:{added.Minute:00}），可直接改成需要的时间";
    }

    private void RemoveTimer(TimerItemViewModel item)
    {
        _workspace?.Configurations.RemoveTimer(item.Id);
        Timers.Remove(item);
        RaiseListStates();
        Message = "已删除定时计划";
    }

    private void RunNow(TimerItemViewModel item)
    {
        if (_workspace is null)
        {
            return;
        }

        _ = _workspace.Timers.TryRunNowAsync(item.ToEntry());
        Message = $"已请求立即试跑「{item.TimeText}」";
    }

    private void CheckNow()
    {
        if (_workspace is null)
        {
            return;
        }

        IReadOnlyList<TimerEntry> fired = _workspace.Timers.PollNow();
        Message = fired.Count == 0
            ? $"已检查：当前没有到点的计划（{DateTime.Now:HH:mm:ss}）"
            : $"已检查：触发 {fired.Count} 条（{string.Join("、", fired.Select(e => $"{e.Hour:00}:{e.Minute:00}"))}）";
    }

    private void OnRowChanged(TimerItemViewModel item)
    {
        if (_workspace is null)
        {
            return;
        }

        if (!_workspace.Configurations.UpdateTimer(item.ToEntry()))
        {
            Message = "保存失败：该计划已不存在（可能已被删除）";
            return;
        }

        OnPropertyChanged(nameof(SchedulerText));
    }

    // ---- 加载 ----

    private void Reload()
    {
        Timers.Clear();

        ConfigurationNames.Clear();
        if (_workspace is not null)
        {
            foreach (var group in _workspace.Configurations.Configurations)
            {
                ConfigurationNames.Add(group.Name);
            }
        }

        if (_workspace is not null)
        {
            foreach (TimerEntry entry in _workspace.Configurations.Timers)
            {
                Timers.Add(CreateRow(entry));
            }
        }

        OnPropertyChanged(nameof(ConfigurationHint));
        RaiseListStates();
    }

    private TimerItemViewModel CreateRow(TimerEntry entry)
        => new(entry, ConfigurationNames, OnRowChanged, RemoveTimer, RunNow);

    private void RaiseListStates()
    {
        OnPropertyChanged(nameof(HasTimers));
        OnPropertyChanged(nameof(NoTimers));
        (AddTimerCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    // ---- 外部事件 ----

    private void OnReported(object? sender, TimerRunEventArgs e) => Post(() =>
    {
        Message = e.Message;
        foreach (TimerItemViewModel row in Timers)
        {
            row.RefreshDerived();
        }
    });

    private void OnTicked(object? sender, EventArgs e) => Post(() =>
    {
        // 只刷「下次触发」这类派生文本，代价很低
        foreach (TimerItemViewModel row in Timers)
        {
            row.RefreshDerived();
        }
    });

    private void OnConfigurationsChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_workspace is null)
        {
            return;
        }

        List<string> names = _workspace.Configurations.Configurations.Select(g => g.Name).ToList();
        if (names.SequenceEqual(ConfigurationNames, StringComparer.Ordinal))
        {
            return;
        }

        ConfigurationNames.Clear();
        foreach (string name in names)
        {
            ConfigurationNames.Add(name);
        }

        foreach (TimerItemViewModel row in Timers)
        {
            row.SyncConfigurations(names);
        }

        OnPropertyChanged(nameof(ConfigurationHint));
        (AddTimerCommand as RelayCommand)?.RaiseCanExecuteChanged();
    });

    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_workspace is not null)
        {
            _workspace.Timers.Reported -= OnReported;
            _workspace.Timers.Ticked -= OnTicked;
            _workspace.Configurations.Changed -= OnConfigurationsChanged;
        }
    }
}
