using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using MaaYys.App.ViewModels;
using MaaYys.Engine.Config;
using MaaYys.Engine.Logging;

namespace MaaYys.App.Services;

/// <summary>
/// 实例工作区（P2.4）：把「实例清单 + 可选项清单 + 标签页运行态」串成一份共享状态，
/// 供 <see cref="MainViewModel"/>（标签条 / 状态栏）与 <see cref="InstancesViewModel"/>（实例页编辑器）同时使用。
/// 单一数据源，避免两处各自维护集合导致标签条与列表不同步。
/// </summary>
public sealed class InstanceWorkspace : ViewModelBase, IDisposable
{
    private readonly SynchronizationContext _uiContext;
    private InstanceViewModel? _current;
    private bool _syncing;
    private bool _disposed;

    public InstanceWorkspace(string? interfacePath = null, string? storePath = null, SynchronizationContext? uiContext = null)
    {
        _uiContext = uiContext ?? SynchronizationContext.Current ?? new SynchronizationContext();

        Store = new InstanceStore(storePath);
        Store.Load();

        // P2.6：配置组与实例清单同目录。加载时若清单里还有 P2.4 的 task_entries，
        // 会顺手「提升」为同名配置组并写回实例，所以这里可能出现实例被修改的情况。
        string configPath = storePath is null
            ? ConfigurationStore.DefaultPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storePath))!, ConfigurationStore.FileName);
        Configurations = new ConfigurationStore(configPath);
        bool migrated = Configurations.Load(Store.Instances);
        if (migrated)
        {
            Store.Save();
        }

        string? resolvedInterface = interfacePath ?? LocateInterfaceJson();
        Catalog = InstanceCatalog.Load(resolvedInterface);
        Resources = ResourceProvider.Load(resolvedInterface);

        // P5.5：日志缓冲（策略复用配置文件里的 log 段），必须在建实例之前就绪
        Log = new LogBuffer(Configurations.LogOptions);
        Log.Write(LogLevel.Info, "System", $"日志已启动（容量 {Configurations.LogOptions.Capacity} 条，时间格式 {Configurations.LogOptions.TimeFormat}）", "lifecycle");

        Store.Changed += (_, _) => SyncFromStore();
        SyncFromStore();

        // P5.3 / P5.4：执行入口与定时调度跟着工作区走，页面未创建也生效
        Runner = new QueueRunService(this);
        Timers = new TimerRunner(this);
    }

    /// <summary>
    /// 运行日志（P5.5）：全程序共用一份环形缓冲，各实例的引擎通过
    /// <see cref="ScopedLogSink"/> 打上实例名后写入同一条流，日志页可按来源/实例/级别过滤。
    /// </summary>
    public LogBuffer Log { get; }

    /// <summary>配置组（P2.6：命名任务配置，可被多个实例共用）。</summary>
    public ConfigurationStore Configurations { get; }

    /// <summary>
    /// 取实例对应的配置组：实例显式绑定的优先，未绑定（或指向已删除的组）时跟随 Current 指针。
    /// </summary>
    public TaskConfiguration? ConfigurationFor(InstanceDefinition definition)
        => Configurations.Find(definition.ConfigurationName) ?? Configurations.CurrentConfiguration;

    /// <summary>
    /// 配置组改名后，把所有指向旧名的实例改绑到新名（否则实例会静默回落到 Current）。
    /// </summary>
    public void RebindConfiguration(string oldName, string newName)
    {
        bool touched = false;
        foreach (InstanceViewModel instance in Instances)
        {
            if (string.Equals(instance.Definition.ConfigurationName, oldName, StringComparison.Ordinal))
            {
                instance.Definition.ConfigurationName = newName;
                touched = true;
            }
        }

        if (touched)
        {
            Store.Save();
        }
    }

    /// <summary>配置组删除后，清掉指向它的实例绑定，让它们明确回到「跟随 Current」。</summary>
    public void DetachConfiguration(string name)
    {
        bool touched = false;
        foreach (InstanceViewModel instance in Instances)
        {
            if (string.Equals(instance.Definition.ConfigurationName, name, StringComparison.Ordinal))
            {
                instance.Definition.ConfigurationName = string.Empty;
                touched = true;
            }
        }

        if (touched)
        {
            Store.Save();
        }
    }

    /// <summary>实例清单（持久化）。</summary>
    public InstanceStore Store { get; }

    /// <summary>可选资源集 / 任务清单（来自 interface.json，缺失时降级）。</summary>
    public InstanceCatalog Catalog { get; }

    /// <summary>资源集加载链（P2.5：按名字解析 bundle 路径，供实例连接/热切换使用）。</summary>
    public ResourceProvider Resources { get; }

    /// <summary>
    /// 队列执行入口（P5.3）：全程序唯一执行器，任务页与定时器共用。
    /// 构造在 <see cref="InstanceWorkspace"/> 末尾，可用时一定非 null。
    /// </summary>
    public QueueRunService Runner { get; }

    /// <summary>定时任务执行器（P5.4）：到点触发 <see cref="Runner"/>。</summary>
    public TimerRunner Timers { get; }

    /// <summary>标签页集合（顺序 = 清单顺序）。</summary>
    public ObservableCollection<InstanceViewModel> Instances { get; } = [];

    /// <summary>当前激活实例（切换标签条即切换它）。</summary>
    public InstanceViewModel? Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value))
            {
                return;
            }

            InstanceViewModel? previous = _current;
            _current = value;

            if (previous is not null)
            {
                previous.IsActive = false;
            }

            if (value is not null)
            {
                value.IsActive = true;
                if (!_syncing)
                {
                    Store.SetCurrent(value.Id);
                }
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusSummary));
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>激活实例变化（状态栏 / 页面提示刷新用）。</summary>
    public event EventHandler? CurrentChanged;

    /// <summary>状态栏摘要：实例名 + 连接状态 + 帧率。</summary>
    public string StatusSummary => Current is null
        ? "无实例"
        : $"{Current.DisplayName} · {Current.StateText}"
          + (Current.State == InstanceState.Connected ? $" · {Current.ActualFps:F0}fps" : string.Empty);

    // ---- 变更操作 ----

    /// <summary>新建实例并激活。</summary>
    public InstanceViewModel? Add(string? name = null)
    {
        InstanceDefinition created = Store.Add(name);
        SyncFromStore();
        InstanceViewModel? vm = Instances.FirstOrDefault(i => i.Id == created.Id);
        Current = vm;
        return vm;
    }

    /// <summary>复制实例并激活。</summary>
    public InstanceViewModel? Duplicate(InstanceViewModel source)
    {
        InstanceDefinition? copy = Store.Duplicate(source.Id);
        if (copy is null)
        {
            return null;
        }

        SyncFromStore();
        InstanceViewModel? vm = Instances.FirstOrDefault(i => i.Id == copy.Id);
        Current = vm;
        return vm;
    }

    /// <summary>删除实例（连带释放引擎会话与设备占用）。</summary>
    public bool Remove(InstanceViewModel target, out string? error)
    {
        target.Dispose();
        if (!Store.Remove(target.Id, out error))
        {
            return false;
        }

        SyncFromStore();
        return true;
    }

    /// <summary>把清单同步到标签集合：按 Id 增量更新（保留已连接会话，不重建对象）。</summary>
    private void SyncFromStore()
    {
        _syncing = true;
        try
        {
            IReadOnlyList<InstanceDefinition> definitions = Store.Instances;

            // 移除已被删除的
            foreach (InstanceViewModel stale in Instances.Where(vm => definitions.All(d => d.Id != vm.Id)).ToList())
            {
                Instances.Remove(stale);
                stale.Dispose();
            }

            // 追加新增的 + 按清单顺序重排
            for (int index = 0; index < definitions.Count; index++)
            {
                InstanceDefinition definition = definitions[index];
                InstanceViewModel? existing = Instances.FirstOrDefault(vm => vm.Id == definition.Id);
                if (existing is null)
                {
                    existing = new InstanceViewModel(definition, Store, _uiContext, Resources, Log);
                    InstallListener(existing);
                    Instances.Insert(Math.Min(index, Instances.Count), existing);
                }
                else
                {
                    existing.NotifyDefinitionChanged();
                    int currentIndex = Instances.IndexOf(existing);
                    if (currentIndex != index)
                    {
                        Instances.Move(currentIndex, index);
                    }
                }
            }

            // 当前指针跟随清单（Store.Current 自带回退到首项）
            InstanceDefinition? currentDefinition = Store.Current;
            InstanceViewModel? currentVm = currentDefinition is null
                ? Instances.FirstOrDefault()
                : Instances.FirstOrDefault(vm => vm.Id == currentDefinition.Id) ?? Instances.FirstOrDefault();

            if (!ReferenceEquals(_current, currentVm))
            {
                InstanceViewModel? previous = _current;
                _current = currentVm;
                if (previous is not null)
                {
                    previous.IsActive = false;
                }

                if (currentVm is not null)
                {
                    currentVm.IsActive = true;
                }

                OnPropertyChanged(nameof(Current));
                OnPropertyChanged(nameof(StatusSummary));
                CurrentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void InstallListener(InstanceViewModel vm)
    {
        vm.PropertyChanged += OnInstancePropertyChanged;
    }

    private void OnInstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _current) &&
            e.PropertyName is nameof(InstanceViewModel.State) or nameof(InstanceViewModel.ActualFps) or nameof(InstanceViewModel.StateText))
        {
            OnPropertyChanged(nameof(StatusSummary));
        }
    }

    /// <summary>
    /// 定位 interface.json。优先级（P2.5 起）：
    /// 1) 环境变量 <c>MAAYYS_RESOURCE_ROOT</c>（对接独立资源包目录，如 MaaYYs 安装目录）；
    /// 2) 程序目录下的 interface.json；
    /// 3) 程序目录下的 resource\interface.json（P3.6 产出的标准位置）；
    /// 4) 程序目录向上最多 6 层（开发期 bin 层级较深）。
    /// </summary>
    public static string? LocateInterfaceJson()
    {
        string? root = Environment.GetEnvironmentVariable("MAAYYS_RESOURCE_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            string candidate = Path.Combine(root, "interface.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        string direct = Path.Combine(AppContext.BaseDirectory, "interface.json");
        if (File.Exists(direct))
        {
            return direct;
        }

        string packaged = Path.Combine(AppContext.BaseDirectory, "resource", "interface.json");
        if (File.Exists(packaged))
        {
            return packaged;
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 6 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir.FullName, "interface.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Timers.Dispose();
        Runner.Dispose();

        foreach (InstanceViewModel vm in Instances)
        {
            vm.PropertyChanged -= OnInstancePropertyChanged;
            vm.Dispose();
        }

        Instances.Clear();
    }
}
