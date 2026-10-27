using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>可用任务目录项（「添加任务」下拉用）。</summary>
public sealed class TaskCatalogItem
{
    public TaskCatalogItem(string name, string displayName, IReadOnlyList<string>? group)
    {
        Name = name;
        DisplayName = displayName;
        GroupText = group is { Count: > 0 } ? string.Join(" / ", group) : "未分组";
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string GroupText { get; }
}

/// <summary>选项面板里的一个可选项（select 的 case / checkbox 的一格）。</summary>
public sealed class ChoiceItemViewModel : ViewModelBase
{
    private readonly Action<bool> _onCheckChanged;
    private bool _isChecked;

    public ChoiceItemViewModel(OptionChoice choice, bool isChecked, Action<bool>? onCheckChanged = null)
    {
        Name = choice.Name;
        Label = choice.Label;
        Description = choice.Description;
        _isChecked = isChecked;
        _onCheckChanged = onCheckChanged ?? (_ => { });
    }

    public string Name { get; }

    public string Label { get; }

    public string? Description { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _onCheckChanged(value);
            }
        }
    }
}

/// <summary>input 类型的一行输入。</summary>
public sealed class InputRowViewModel : ViewModelBase
{
    private readonly Action<string> _onChanged;
    private string _value;

    public InputRowViewModel(OptionInputField field, string value, Action<string> onChanged)
    {
        Field = field;
        _value = value;
        _onChanged = onChanged;
    }

    public OptionInputField Field { get; }

    public string Label => Field.Label;

    public string Hint => string.IsNullOrWhiteSpace(Field.Description) ? Field.PipelineType : Field.Description!;

    public string Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                _onChanged(value);
            }
        }
    }
}

/// <summary>选项面板的一行（已按当前取值展开，见 <see cref="TaskOptionDefinition"/>）。</summary>
public sealed class OptionRowViewModel : ViewModelBase
{
    private int _selectedIndex;
    private readonly Action<string?> _onSelectChanged;

    public OptionRowViewModel(
        TaskOptionDefinition definition,
        IReadOnlyList<string> currentValues,
        IReadOnlyList<InputRowViewModel> inputs,
        Action<string?> onSelectChanged,
        Action<bool> onCheckChanged)
    {
        Definition = definition;
        Inputs = new ObservableCollection<InputRowViewModel>(inputs);
        _onSelectChanged = onSelectChanged;

        Choices = new ObservableCollection<ChoiceItemViewModel>(
            definition.Choices.Select(c => new ChoiceItemViewModel(
                c, currentValues.Contains(c.Name, StringComparer.Ordinal),
                definition.IsCheckbox ? onCheckChanged : null)));

        _selectedIndex = definition.IsCheckbox ? -1 : IndexOf(currentValues.FirstOrDefault());
    }

    public TaskOptionDefinition Definition { get; }

    public string Key => Definition.Key;

    public string Label => Definition.Label;

    public string? Description => Definition.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public int Indent => Definition.Indent;

    /// <summary>嵌套缩进（直接绑到行容器的 Margin）。</summary>
    public System.Windows.Thickness IndentMargin => new(Indent, 0, 0, 0);

    public bool IsSelect => Definition.IsSelect;

    public bool IsCheckbox => Definition.IsCheckbox;

    public bool IsInput => Definition.IsInput;

    public bool IsSingleChoice => IsSelect || Definition.IsSwitch;

    public string TypeLabel => Definition.Type switch
    {
        "select" => "单选",
        "switch" => "开关",
        "checkbox" => "多选",
        "input" => "输入",
        _ => Definition.Type,
    };

    public IReadOnlyList<OptionChoice> RawChoices => Definition.Choices;

    public ObservableCollection<ChoiceItemViewModel> Choices { get; }

    public ObservableCollection<InputRowViewModel> Inputs { get; }

    /// <summary>select/switch 的选中项；-1 表示「跟随默认 / 不改动」。</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!SetProperty(ref _selectedIndex, value))
            {
                return;
            }

            if (IsSingleChoice)
            {
                _onSelectChanged(value >= 0 && value < RawChoices.Count ? RawChoices[value].Name : null);
            }
        }
    }

    private int IndexOf(string? name)
        => string.IsNullOrEmpty(name)
            ? -1
            : RawChoices.ToList().FindIndex(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}

/// <summary>「全局选项」面板里的一个设置分组（对应 interface.json 的 setting 段）。</summary>
public sealed class GlobalOptionSectionViewModel : ViewModelBase
{
    public GlobalOptionSectionViewModel(string label, string? description)
    {
        Label = label;
        Description = description;
    }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<OptionRowViewModel> Rows { get; } = [];
}

/// <summary>
/// 任务页（P3.4 参数面板 + P5.1 任务队列）：
/// <para>
/// 左侧任务队列（有序、可同类多实例 / 重命名 / 拖拽排序 / 启用、运行态着色）；
/// 右侧编辑选中队列项的参数；底部是编译出的 <c>pipeline_override</c> 预览（P5 投递任务时传它）。
/// </para>
/// <para>
/// 参数面板完全由 <c>interface.json</c> 的 option 定义驱动，新增选项不用改界面代码。
/// 编辑直接写回 <see cref="TaskSelection"/>（队列项持有其引用），变更后统一落盘。
/// </para>
/// </summary>
public sealed class TasksViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace? _workspace;
    private readonly QueueRunService? _runner;
    private readonly SynchronizationContext? _uiContext;

    private string _lastRunSummary = string.Empty;

    private bool _loading;
    private QueueItemViewModel? _selectedQueueItem;
    private int _selectedRightTab;

    public TasksViewModel()
        : this(null)
    {
    }

    public TasksViewModel(InstanceWorkspace? workspace, ExecutionOptions? executionOptions = null)
    {
        _workspace = workspace;
        _runner = workspace?.Runner;
        _uiContext = SynchronizationContext.Current;

        if (_runner is not null)
        {
            _runner.ItemStatusChanged += OnItemStatusChanged;
            _runner.StateChanged += OnExecutorStateChanged;
        }

        if (workspace is not null)
        {
            workspace.CurrentChanged += OnWorkspaceCurrentChanged;
        }

        ResetTaskCommand = new RelayCommand(_ => ResetTaskOptions(), _ => SelectedQueueItem is not null && OptionRows.Count > 0);
        ResetAllCommand = new RelayCommand(_ => ResetAllOptions(), _ => Queue.Any(q => q.Options.Count > 0));
        AddTaskCommand = new RelayCommand(p => AddTaskToQueue((string)p!), _ => AvailableTasks.Any());
        RunQueueCommand = new RelayCommand(_ => StartRun(), _ => CanStartRun);
        StopRunCommand = new RelayCommand(_ => StopRun(), _ => IsBusy);
        PauseResumeCommand = new RelayCommand(_ => TogglePause(), _ => IsBusy);

        // P6.2 前先把 MAA 式任务卡的三个动作补齐（齿轮只做「定位到该任务」）
        SelectAllCommand = new RelayCommand(_ => SetAllEnabled(true), _ => Queue.Count > 0);
        DeselectAllCommand = new RelayCommand(_ => SetAllEnabled(false), _ => Queue.Any(q => q.IsEnabled));
        EditItemCommand = new RelayCommand(p =>
        {
            if (p is QueueItemViewModel item)
            {
                SelectedQueueItem = item;
            }
        });

        if (workspace is not null && workspace.Resources.Config is not null)
        {
            foreach (TaskConfiguration group in workspace.Configurations.Configurations)
            {
                ConfigurationNames.Add(group.Name);
            }

            _selectedConfiguration = workspace.Configurations.Current ?? ConfigurationNames.FirstOrDefault() ?? string.Empty;
        }

        LoadCatalog();
        LoadQueue();
        RebuildGlobalOptionRows();
    }

    public ObservableCollection<string> ConfigurationNames { get; } = [];

    private string _selectedConfiguration = string.Empty;

    /// <summary>当前编辑的配置组（切换即重新加载队列与参数值）。</summary>
    public string SelectedConfiguration
    {
        get => _selectedConfiguration;
        set
        {
            if (!SetProperty(ref _selectedConfiguration, value) || _loading)
            {
                return;
            }

            _workspace?.Configurations.SetCurrent(value);
            LoadCatalog();
            LoadQueue();
            RebuildGlobalOptionRows();
        }
    }

    /// <summary>任务队列（顺序即执行顺序，可拖拽重排）。</summary>
    public ObservableCollection<QueueItemViewModel> Queue { get; } = [];

    /// <summary>所有可加入队列的任务（「添加任务」来源）。</summary>
    public ObservableCollection<TaskCatalogItem> AvailableTasks { get; } = [];

    public ObservableCollection<OptionRowViewModel> OptionRows { get; } = [];

    /// <summary>
    /// 全局选项面板的分组（对应 interface.json 的 setting 段，MXU setting 扩展）。
    /// 没有定义 setting 时为空，界面自动隐藏该 Tab。
    /// </summary>
    public ObservableCollection<GlobalOptionSectionViewModel> GlobalSections { get; } = [];

    /// <summary>右侧二级页签的标题（MAA 的「常规设置 / 高级设置」）。</summary>
    public static IReadOnlyList<string> SubTabs { get; } = ["常规设置", "高级设置"];

    /// <summary>右侧面板当前选中的页（0=常规设置，1=高级设置）。</summary>
    public int SelectedRightTab
    {
        get => _selectedRightTab;
        set
        {
            if (SetProperty(ref _selectedRightTab, value))
            {
                OnPropertyChanged(nameof(IsRegularTab));
                OnPropertyChanged(nameof(IsAdvancedTab));
            }
        }
    }

    public bool IsRegularTab => _selectedRightTab == 0;

    public bool IsAdvancedTab => _selectedRightTab == 1;

    /// <summary>实例列表（「多任务共用」区的下拉；与实例页同一份数据源）。</summary>
    public ObservableCollection<InstanceViewModel>? Instances => _workspace?.Instances;

    /// <summary>当前投递目标实例。</summary>
    public InstanceViewModel? CurrentInstance
    {
        get => _workspace?.Current;
        set
        {
            if (_workspace is null || value is null || ReferenceEquals(_workspace.Current, value))
            {
                return;
            }

            _workspace.Current = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanRunOnCurrentInstance));
        }
    }

    /// <summary>当前实例是否已连接（未连接时 Link Start 会给出原因）。</summary>
    public bool CanRunOnCurrentInstance => _workspace?.Current?.State == InstanceState.Connected;

    private string _globalPreview = "{}";

    /// <summary>全局选项编译后的 <c>pipeline_override</c>（预览用，展示全局层对所有任务的贡献）。</summary>
    public string GlobalPreview
    {
        get => _globalPreview;
        private set => SetProperty(ref _globalPreview, value);
    }

    private bool _hasGlobalOptions;

    /// <summary>是否存在全局选项（有 setting 段定义时为 true，用于决定是否显示「全局选项」页）。</summary>
    public bool HasGlobalOptions
    {
        get => _hasGlobalOptions;
        private set => SetProperty(ref _hasGlobalOptions, value);
    }

    public QueueItemViewModel? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set
        {
            if (!SetProperty(ref _selectedQueueItem, value))
            {
                return;
            }

            RebuildOptionRows();
            OnPropertyChanged(nameof(TaskHeader));
            OnPropertyChanged(nameof(TaskHint));
            RaiseCommandStates();
        }
    }

    public string TaskHeader => SelectedQueueItem is null ? "选择一个队列任务" : SelectedQueueItem.DisplayName;

    public string TaskHint => SelectedQueueItem is null
        ? "左侧为任务队列：可拖拽排序、添加同类实例、重命名、右键「从此处运行」。"
        : $"{SelectedQueueItem.Options.Count} 项参数 · 改动即时保存到配置组「{SelectedConfiguration}」";

    public int EnabledCount => Queue.Count(q => q.IsEnabled);

    public string TaskSummary => $"队列 {Queue.Count} 项 · 启用 {EnabledCount}";

    private string _preview = "{}";

    /// <summary>当前选中实例编译后的 <c>pipeline_override</c>（P5 投递任务时使用）。</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    private string _message = string.Empty;

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public bool HasError { get; private set; }

    public string CatalogHint => _workspace?.Resources.Config is not null
        ? $"清单：{_workspace.Resources.InterfacePath}（任务 {_workspace.Resources.Config.MergedTasks.Count} 个 · 选项 {_workspace.Resources.Config.MergedOptions.Count} 个）"
        : _workspace?.Resources.Errors.FirstOrDefault() ?? "未找到 interface.json，任务与参数不可用";

    public ICommand ResetTaskCommand { get; }
    public ICommand ResetAllCommand { get; }
    public ICommand AddTaskCommand { get; }

    // ---- P5.3 运行态（供 XAML 绑定） ----

    /// <summary>开始运行整个队列。</summary>
    public ICommand RunQueueCommand { get; }

    /// <summary>停止当前运行。</summary>
    public ICommand StopRunCommand { get; }

    /// <summary>暂停 / 继续（同一按钮）。</summary>
    public ICommand PauseResumeCommand { get; }

    /// <summary>任务卡底部「全选」：把队列里所有项勾上。</summary>
    public ICommand SelectAllCommand { get; }

    /// <summary>任务卡底部「清空」：取消全部勾选（不删任务，与 MAA 一致）。</summary>
    public ICommand DeselectAllCommand { get; }

    /// <summary>任务行右侧齿轮：把右侧参数面板定位到该任务。</summary>
    public ICommand EditItemCommand { get; }

    /// <summary>「完成后」下拉的可选项（顺序与 <see cref="RunSettings.AfterCompletionKinds"/> 一致）。</summary>
    public static IReadOnlyList<string> AfterCompletionOptions { get; } = ["无动作", "退出程序", "关机"];

    /// <summary>
    /// 整轮队列正常跑完后做什么（停止 / 暂停不触发）。
    /// <para>
    /// P6.2 起直接读写应用设置（<c>settings.run.after_completion</c>）——
    /// 重启后不再回到「无动作」，设置页的同一项也实时同步。
    /// </para>
    /// <para>选「关机」会二次确认 —— 无人值守时这个选项一旦误选代价很大。</para>
    /// </summary>
    public int AfterCompletionIndex
    {
        get
        {
            string kind = _workspace?.Configurations.Settings.Run.AfterCompletion ?? "none";
            int index = Array.IndexOf(RunSettings.AfterCompletionKinds, kind);
            return index < 0 ? 0 : index;
        }

        set
        {
            int index = Math.Clamp(value, 0, AfterCompletionOptions.Count - 1);
            if (index == 2 && AfterCompletionIndex != 2)
            {
                MessageBoxResult confirm = MessageBox.Show(
                    "队列跑完后将执行关机命令（60 秒倒计时，可用 shutdown /a 取消）。\n确定要选「关机」吗？",
                    "确认危险操作",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    OnPropertyChanged();   // 让下拉回退到原值
                    return;
                }
            }

            if (_workspace is null)
            {
                OnPropertyChanged();
                return;
            }

            RunSettings run = _workspace.Configurations.Settings.Run;
            if (string.Equals(run.AfterCompletion, RunSettings.AfterCompletionKinds[index], StringComparison.Ordinal))
            {
                return;
            }

            run.AfterCompletion = RunSettings.AfterCompletionKinds[index];
            _workspace.Configurations.UpdateSettings(_workspace.Configurations.Settings);
            OnPropertyChanged();
            // 设置页同步（同一个 RunSettings 实例，但页面上的下拉要重读）
            _workspace.NotifySettingsChanged();
        }
    }

    /// <summary>执行器是否处于运行/暂停/停止中。</summary>
    public bool IsBusy => _runner?.IsBusy == true;

    /// <summary>队列里有启用任务且当前空闲。</summary>
    public bool CanStartRun => !IsBusy && Queue.Any(q => q.IsEnabled);

    /// <summary>运行状态文案（界面右上角）。</summary>
    public string RunStateText => _runner?.State switch
    {
        ExecutionState.Running => "运行中",
        ExecutionState.Stopping => "停止中",
        ExecutionState.Paused => "已暂停",
        _ => "空闲",
    };

    /// <summary>
    /// 运行状态总览（P6.1 主界面顶部）：运行态 + 当前任务 + 完成/失败/跳过计数。
    /// 队列为空或全部跑完时退化为纯计数，一眼能看出「还剩多少」。
    /// </summary>
    public string RunOverview
    {
        get
        {
            int total = Queue.Count;
            if (total == 0)
            {
                return "队列为空 · 点 ⊕ 添加任务";
            }

            int done = Queue.Count(q => q.Status == TaskRunStatus.Done);
            int failed = Queue.Count(q => q.Status == TaskRunStatus.Failed);
            int skipped = Queue.Count(q => q.Status == TaskRunStatus.Skipped);
            string counters = $"完成 {done} · 失败 {failed} · 跳过 {skipped} · 共 {total}";

            if (_runner?.IsBusy != true)
            {
                return $"{RunStateText} · {counters}";
            }

            string current = Queue.FirstOrDefault(q => q.Status == TaskRunStatus.Running)?.DisplayName ?? "准备中";
            return $"{RunStateText} · {current} · {counters}";
        }
    }

    /// <summary>上一轮执行的汇总（成功/失败/跳过计数）。</summary>
    public string LastRunSummary
    {
        get => _lastRunSummary;
        private set => SetProperty(ref _lastRunSummary, value);
    }

    // ---- 加载 ----

    private TaskConfiguration? CurrentGroup()
        => _workspace?.Configurations.Find(SelectedConfiguration) ?? _workspace?.Configurations.CurrentConfiguration;

    private void LoadCatalog()
    {
        AvailableTasks.Clear();
        InterfaceConfig? config = _workspace?.Resources.Config;
        if (config is null)
        {
            return;
        }

        foreach (TaskConfig task in config.MergedTasks)
        {
            AvailableTasks.Add(new TaskCatalogItem(task.Name, task.DisplayName, task.Group));
        }
    }

    private void LoadQueue()
    {
        _loading = true;
        try
        {
            string? previous = SelectedQueueItem?.Id;
            Queue.Clear();
            OptionRows.Clear();

            TaskConfiguration? group = CurrentGroup();
            if (group is null)
            {
                Preview = "{}";
                return;
            }

            for (int i = 0; i < group.Tasks.Count; i++)
            {
                TaskSelection sel = group.Tasks[i];
                int instances = group.Tasks.Count(s => s.Name == sel.Name);
                var vm = new QueueItemViewModel(
                    sel, instances,
                    RemoveInstance, MoveUp, MoveDown, RunFromHere, RenameInstance, OnEnabledChanged);
                vm.RefreshPosition(i, group.Tasks.Count);
                Queue.Add(vm);
            }

            SelectedQueueItem = Queue.FirstOrDefault(q => q.Id == previous) ?? Queue.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(RunOverview));
    }

    private void RefreshInstanceCounts(TaskConfiguration group)
    {
        foreach (QueueItemViewModel vm in Queue)
        {
            vm.InstanceCount = group.Tasks.Count(s => s.Name == vm.Name);
        }
    }

    private void RefreshPositions()
    {
        for (int i = 0; i < Queue.Count; i++)
        {
            Queue[i].RefreshPosition(i, Queue.Count);
        }
    }

    // ---- 队列操作 ----

    /// <summary>把一个任务加入队列（支持同类多实例，自动生成实例 ID）。</summary>
    private void AddTaskToQueue(string name)
    {
        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return;
        }

        TaskSelection sel = group.AddInstance(name);
        int instances = group.Tasks.Count(s => s.Name == name);
        var vm = new QueueItemViewModel(
            sel, instances,
            RemoveInstance, MoveUp, MoveDown, RunFromHere, RenameInstance, OnEnabledChanged);
        vm.RefreshPosition(group.Tasks.Count - 1, group.Tasks.Count);
        Queue.Add(vm);
        RefreshInstanceCounts(group);
        SaveGroup(group);
        SelectedQueueItem = vm;
        RaiseCommandStates();
    }

    private void RemoveInstance(string id)
    {
        TaskConfiguration? group = CurrentGroup();
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == id);
        if (group is null || vm is null)
        {
            return;
        }

        group.RemoveById(id);
        Queue.Remove(vm);
        RefreshInstanceCounts(group);
        RefreshPositions();
        SaveGroup(group);
        if (ReferenceEquals(SelectedQueueItem, vm))
        {
            SelectedQueueItem = Queue.FirstOrDefault();
        }

        RaiseCommandStates();
    }

    private void MoveUp(string id) => MoveInstance(id, -1);

    private void MoveDown(string id) => MoveInstance(id, +1);

    /// <summary>拖放排序：把指定实例移到目标索引（公共入口，供 UI 拖放调用）。</summary>
    public void MoveInstanceTo(string id, int targetIndex)
    {
        TaskConfiguration? group = CurrentGroup();
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == id);
        if (group is null || vm is null)
        {
            return;
        }

        int current = Queue.IndexOf(vm);
        int target = Math.Max(0, Math.Min(targetIndex, Queue.Count - 1));
        if (target == current)
        {
            return;
        }

        group.MoveById(id, target);
        Queue.Move(current, target);
        RefreshPositions();
        SaveGroup(group);
        RaiseCommandStates();
    }

    private void MoveInstance(string id, int delta)
    {
        TaskConfiguration? group = CurrentGroup();
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == id);
        if (group is null || vm is null)
        {
            return;
        }

        int idx = Queue.IndexOf(vm);
        int target = idx + delta;
        if (target < 0 || target >= Queue.Count)
        {
            return;
        }

        group.MoveById(id, target);   // 配置里的顺序
        Queue.Move(idx, target);     // UI 集合同步
        RefreshPositions();
        SaveGroup(group);
        RaiseCommandStates();
    }

    private void RenameInstance(string id, string alias)
    {
        TaskConfiguration? group = CurrentGroup();
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == id);
        if (group is null || vm is null)
        {
            return;
        }

        vm.Alias = alias;   // setter 写回 sel.Alias 并通知 DisplayName
        SaveGroup(group);
    }

    /// <summary>右键「从此处运行」：把该队列项作为起点投递执行引擎。</summary>
    private void RunFromHere(string id)
    {
        int index = Queue.ToList().FindIndex(q => q.Id == id);
        if (index < 0)
        {
            return;
        }

        _ = RunQueueAsync(index);
    }

    /// <summary>勾选状态变化：<see cref="TaskSelection.Enabled"/> 已由 QueueItem 直接写回，这里只落盘。</summary>
    private void OnEnabledChanged(bool _)
    {
        if (_loading)
        {
            return;
        }

        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return;
        }

        SaveGroup(group);
        Message = $"{SelectedConfiguration}：{EnabledCount}/{Queue.Count} 个任务已启用";
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(RunOverview));
        OnPropertyChanged(nameof(TaskHint));
    }

    /// <summary>执行引擎在运行时调用：设置某队列项的运行态（驱动 UI 着色）。</summary>
    public void SetStatus(string id, TaskRunStatus status)
    {
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == id);
        if (vm is not null)
        {
            vm.Status = status;
        }
    }

    // ---- P5.3 执行引擎接线 ----

    /// <summary>开始执行整个队列（从第一项）。</summary>
    private void StartRun() => _ = RunQueueAsync(0);

    private async Task RunQueueAsync(int startIndex)
    {
        if (IsBusy)
        {
            Message = "已有队列在运行，请先停止";
            return;
        }

        if (_runner is null)
        {
            Message = "执行入口不可用（未加载工作区）";
            return;
        }

        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            Message = "没有可用的配置组：请先新建一个";
            return;
        }

        ResetRunStatuses();

        try
        {
            int enabled = _runner.BuildItems(group).Count(i => i.Enabled);
            Message = startIndex == 0
                ? $"开始执行队列（{enabled} 项启用）"
                : $"从第 {startIndex + 1} 项开始执行";

            await HandleSummaryAsync(_runner.RunAsync(group, startIndex));
        }
        catch (InvalidOperationException ex)
        {
            Message = ex.Message;
            RaiseRunStates();
        }
    }

    private async Task HandleSummaryAsync(Task<ExecutionSummary> task)
    {
        RaiseRunStates();
        try
        {
            ExecutionSummary summary = await task.ConfigureAwait(true);
            LastRunSummary = $"成功 {summary.Succeeded} · 失败 {summary.Failed} · 跳过 {summary.Skipped}";
            Message = summary.Message ?? LastRunSummary;
        }
        catch (Exception ex)
        {
            Message = $"执行异常：{ex.Message}";
        }
        finally
        {
            RaiseRunStates();
        }

        // 「完成后动作」由 QueueRunService 统一执行（P6.2 起与定时触发共用同一条路径），
        // 页面只负责展示结果 —— 否则手动跑一次会执行两遍（关机可能被下发两次）。
    }

    /// <summary>全选 / 清空：逐项写回 Enabled（每项都会走 OnEnabledChanged 落盘）。</summary>
    private void SetAllEnabled(bool enabled)
    {
        foreach (QueueItemViewModel item in Queue)
        {
            item.IsEnabled = enabled;
        }

        RaiseCommandStates();
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(RunOverview));
        OnPropertyChanged(nameof(CanStartRun));
        Message = enabled
            ? $"{SelectedConfiguration}：已勾选全部 {Queue.Count} 项"
            : $"{SelectedConfiguration}：已取消全部勾选";
    }

    private void StopRun()
    {
        if (_runner?.Stop() == true)
        {
            Message = "正在停止…";
        }
    }

    private void TogglePause()
    {
        if (_runner is null)
        {
            return;
        }

        if (_runner.State == ExecutionState.Running)
        {
            if (_runner.Pause())
            {
                Message = "正在暂停…";
            }

            return;
        }

        if (_runner.State == ExecutionState.Paused)
        {
            Task<ExecutionSummary>? resumed = _runner.Resume();
            if (resumed is not null)
            {
                _ = HandleSummaryAsync(resumed);
            }
        }
    }

    private void ResetRunStatuses()
    {
        foreach (QueueItemViewModel item in Queue)
        {
            item.Status = TaskRunStatus.None;
        }
    }

    private void OnItemStatusChanged(object? sender, QueueItemStatusChangedEventArgs e) => Post(() =>
    {
        QueueItemViewModel? vm = Queue.FirstOrDefault(q => q.Id == e.Item.Id);
        if (vm is null)
        {
            return;
        }

        vm.Status = e.Status;

        if (e.Status is TaskRunStatus.Running or TaskRunStatus.Done or TaskRunStatus.Failed or TaskRunStatus.Stopped)
        {
            Message = e.Message is null ? e.Item.DisplayName : $"{e.Item.DisplayName}：{e.Message}";
        }

        if (e.Status is TaskRunStatus.Done or TaskRunStatus.Failed)
        {
            OnPropertyChanged(nameof(TaskSummary));
        }

        OnPropertyChanged(nameof(RunOverview));
    });

    private void OnExecutorStateChanged(object? sender, ExecutionState state) => Post(RaiseRunStates);

    /// <summary>执行事件来自引擎线程，统一切回 UI 线程。</summary>
    private void Post(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return;
        }

        _uiContext.Post(_ => action(), null);
    }

    private void RaiseRunStates()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanStartRun));
        OnPropertyChanged(nameof(RunStateText));
        OnPropertyChanged(nameof(RunOverview));
        (RunQueueCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopRunCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (PauseResumeCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 以代码方式给某个任务设置一个选项值（与界面操作走同一条保存路径）。
    /// 按任务入口名定位第一个实例，供自动化验证与 P5 的「按配置批量预置」使用。
    /// </summary>
    public bool ApplyOption(string taskName, string optionKey, string? value)
    {
        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return false;
        }

        TaskSelection? entry = group.Tasks.FirstOrDefault(t => string.Equals(t.Name, taskName, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }

        entry.SetOption(optionKey, value);
        SaveGroup(group);
        if (SelectedQueueItem is not null && string.Equals(SelectedQueueItem.Name, taskName, StringComparison.Ordinal))
        {
            RebuildOptionRows();
        }

        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(RunOverview));
        return true;
    }

    // ---- 参数面板 ----

    private IReadOnlyDictionary<string, string> CurrentValues()
        => SelectedQueueItem?.Options ?? new Dictionary<string, string>();

    private void RebuildOptionRows()
    {
        OptionRows.Clear();

        InterfaceConfig? config = _workspace?.Resources.Config;
        QueueItemViewModel? sel = SelectedQueueItem;
        if (config is null || sel is null)
        {
            Preview = "{}";
            return;
        }

        TaskConfig? task = config.MergedTasks.FirstOrDefault(t => string.Equals(t.Name, sel.Name, StringComparison.Ordinal));
        if (task is null)
        {
            Preview = "{}";
            return;
        }

        IReadOnlyDictionary<string, string> values = sel.Options;
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(config, task, values);

        foreach (TaskOptionDefinition row in rows)
        {
            if (!config.MergedOptions.TryGetValue(row.Key, out OptionConfig? option) || option is null)
            {
                continue;
            }

            OptionRows.Add(BuildRow(
                option, row, values,
                value => OnSingleChoiceChanged(row.Key, value),
                _ => OnCheckChanged(row.Key),
                (inputName, value) => OnInputChanged(row.Key, inputName, value)));
        }

        RefreshPreview();
        RaiseCommandStates();
    }

    /// <summary>用一份 option 定义 + 当前取值 + 三类回调构造一行参数视图（任务级与全局级共用）。</summary>
    private OptionRowViewModel BuildRow(
        OptionConfig option,
        TaskOptionDefinition row,
        IReadOnlyDictionary<string, string> values,
        Action<string?> onSelect,
        Action<bool> onCheck,
        Action<string, string> onInput)
    {
        IReadOnlyList<string> current = option.Type == "checkbox"
            ? TaskOptions.ResolveValues(option, values)
            : [TaskOptions.ResolveValue(option, values) ?? string.Empty];

        List<InputRowViewModel> inputs = [];
        foreach (InputConfig input in option.Inputs ?? [])
        {
            inputs.Add(new InputRowViewModel(
                new OptionInputField(
                    input.Name, input.Label ?? input.Name, input.Description,
                    input.Default ?? string.Empty, input.PipelineType ?? "string"),
                TaskOptions.ResolveInput(option, input, values),
                value => onInput(input.Name, value)));
        }

        return new OptionRowViewModel(row, current, inputs, onSelect, onCheck);
    }

    /// <summary>
    /// 重建「全局选项」面板（MXU setting 扩展）：按 interface.json 的 setting 段逐组渲染，
    /// 编辑回调写回配置组的 <see cref="TaskConfiguration.GlobalOptions"/>。
    /// </summary>
    private void RebuildGlobalOptionRows()
    {
        GlobalSections.Clear();

        InterfaceConfig? config = _workspace?.Resources.Config;
        if (config is null)
        {
            GlobalPreview = "{}";
            return;
        }

        IReadOnlyDictionary<string, string> values = CurrentGroup()?.GlobalOptions
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (SettingSectionConfig section in config.MergedSettings)
        {
            var vm = new GlobalOptionSectionViewModel(section.Label ?? section.Name, section.Description);
            IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(config, section.Option ?? [], values);
            foreach (TaskOptionDefinition row in rows)
            {
                if (!config.MergedOptions.TryGetValue(row.Key, out OptionConfig? option) || option is null)
                {
                    continue;
                }

                vm.Rows.Add(BuildRow(
                    option, row, values,
                    value => OnGlobalSingleChoiceChanged(row.Key, value),
                    _ => OnGlobalCheckChanged(row.Key),
                    (inputName, value) => OnGlobalInputChanged(row.Key, inputName, value)));
            }

            GlobalSections.Add(vm);
        }

        HasGlobalOptions = GlobalSections.Count > 0;
        RefreshGlobalPreview();
    }

    private void OnGlobalSingleChoiceChanged(string optionKey, string? value)
    {
        if (_loading)
        {
            return;
        }

        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(value))
        {
            group.GlobalOptions.Remove(optionKey);
        }
        else
        {
            group.GlobalOptions[optionKey] = value;
        }

        SaveGroup(group);
        RebuildGlobalOptionRows();
    }

    private void OnGlobalCheckChanged(string optionKey)
    {
        if (_loading)
        {
            return;
        }

        TaskConfiguration? group = CurrentGroup();
        GlobalOptionSectionViewModel? section = GlobalSections.FirstOrDefault(s => s.Rows.Any(r => r.Key == optionKey));
        OptionRowViewModel? row = section?.Rows.FirstOrDefault(r => r.Key == optionKey);
        if (group is null || row is null)
        {
            return;
        }

        List<string> selected = [.. row.Choices.Where(c => c.IsChecked).Select(c => c.Name)];
        if (selected.Count == 0)
        {
            group.GlobalOptions.Remove(optionKey);
        }
        else
        {
            group.GlobalOptions[optionKey] = string.Join(TaskOptions.ListSeparator, selected);
        }

        SaveGroup(group);
        RebuildGlobalOptionRows();
    }

    private void OnGlobalInputChanged(string optionKey, string inputName, string value)
    {
        if (_loading)
        {
            return;
        }

        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return;
        }

        string key = TaskOptions.InputKey(optionKey, inputName);
        if (string.IsNullOrEmpty(value))
        {
            group.GlobalOptions.Remove(key);
        }
        else
        {
            group.GlobalOptions[key] = value;
        }

        SaveGroup(group);
        RefreshGlobalPreview();   // 输入不影响面板结构，不重建以免丢焦点
    }

    private TaskSelection? EnsureEntry()
        => SelectedQueueItem?.Selection;

    private void OnSingleChoiceChanged(string optionKey, string? value)
    {
        if (_loading)
        {
            return;
        }

        TaskSelection? entry = EnsureEntry();
        if (entry is null)
        {
            return;
        }

        entry.SetOption(optionKey, value);
        SaveCurrent();
        RebuildOptionRows();       // 联动子选项要跟着展开/收起
    }

    private void OnCheckChanged(string optionKey)
    {
        if (_loading)
        {
            return;
        }

        OptionRowViewModel? row = OptionRows.FirstOrDefault(r => r.Key == optionKey);
        TaskSelection? entry = EnsureEntry();
        if (row is null || entry is null)
        {
            return;
        }

        List<string> selected = [.. row.Choices.Where(c => c.IsChecked).Select(c => c.Name)];
        entry.SetOption(optionKey, selected.Count == 0 ? null : string.Join(TaskOptions.ListSeparator, selected));
        SaveCurrent();
        RebuildOptionRows();
    }

    private void OnInputChanged(string optionKey, string inputName, string value)
    {
        if (_loading)
        {
            return;
        }

        TaskSelection? entry = EnsureEntry();
        if (entry is null)
        {
            return;
        }

        string key = TaskOptions.InputKey(optionKey, inputName);
        if (string.IsNullOrEmpty(value))
        {
            entry.Options.Remove(key);
        }
        else
        {
            entry.Options[key] = value;
        }

        SaveCurrent();
        RefreshPreview();          // 输入不影响面板结构，不重建（否则每敲一个字就丢焦点）
    }

    private void ResetTaskOptions()
    {
        TaskConfiguration? group = CurrentGroup();
        string? id = SelectedQueueItem?.Id;
        TaskSelection? entry = id is null ? null : group?.FindById(id);
        if (group is null || entry is null)
        {
            return;
        }

        entry.Options.Clear();
        SaveGroup(group);
        RebuildOptionRows();
        Message = $"已把「{entry.Name}」的参数恢复为默认值";
    }

    private void ResetAllOptions()
    {
        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return;
        }

        int cleared = 0;
        foreach (TaskSelection entry in group.Tasks)
        {
            cleared += entry.Options.Count;
            entry.Options.Clear();
        }

        SaveGroup(group);
        RebuildOptionRows();
        Message = $"已清空本配置组的 {cleared} 项参数取值";
    }

    private void SaveCurrent()
    {
        TaskConfiguration? group = CurrentGroup();
        if (group is not null)
        {
            SaveGroup(group);
        }
    }

    private void SaveGroup(TaskConfiguration group)
    {
        try
        {
            _workspace?.Configurations.Touch(group.Name);
            HasError = false;
        }
        catch (IOException ex)
        {
            HasError = true;
            Message = $"保存失败：{ex.Message}";
        }

        OnPropertyChanged(nameof(HasError));
    }

    private void RefreshPreview()
    {
        InterfaceConfig? config = _workspace?.Resources.Config;
        QueueItemViewModel? sel = SelectedQueueItem;
        if (config is null || sel is null)
        {
            Preview = "{}";
            return;
        }

        TaskConfig? task = config.MergedTasks.FirstOrDefault(t => string.Equals(t.Name, sel.Name, StringComparison.Ordinal));
        if (task is null)
        {
            Preview = "{}";
            return;
        }

        IReadOnlyDictionary<string, string> global = CurrentGroup()?.GlobalOptions
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            // 合并全局选项层（MXU setting 扩展）：全局默认值 + 当前任务级覆盖
            string json = TaskOptions.Compile(config, task, sel.Options, global, out IReadOnlyList<string> warnings);
            Preview = warnings.Count == 0
                ? Pretty(json)
                : Pretty(json) + Environment.NewLine + Environment.NewLine + "⚠ " + string.Join(Environment.NewLine + "⚠ ", warnings);
        }
        catch (JsonException ex)
        {
            Preview = $"编译失败：{ex.Message}";
        }
    }

    private void RefreshGlobalPreview()
    {
        InterfaceConfig? config = _workspace?.Resources.Config;
        if (config is null)
        {
            GlobalPreview = "{}";
            return;
        }

        IReadOnlyDictionary<string, string> global = CurrentGroup()?.GlobalOptions
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            string json = TaskOptions.CompileGlobal(config, global, out IReadOnlyList<string> warnings);
            GlobalPreview = warnings.Count == 0
                ? Pretty(json)
                : Pretty(json) + Environment.NewLine + Environment.NewLine + "⚠ " + string.Join(Environment.NewLine + "⚠ ", warnings);
        }
        catch (JsonException ex)
        {
            GlobalPreview = $"编译失败：{ex.Message}";
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonSerializer.Serialize(
                JsonSerializer.Deserialize<JsonElement>(json),
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private void RaiseCommandStates()
    {
        (ResetTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ResetAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddTaskCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SelectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeselectAllCommand as RelayCommand)?.RaiseCanExecuteChanged();

        // 队列增删 / 重排后，摘要、运行总览、开始按钮可用态都要跟着变
        // （P6.1：顶部工具栏与任务链在同一个窗口里，漏掉就会出现「列表已有 4 项、摘要仍写 0 项」）
        OnPropertyChanged(nameof(TaskSummary));
        RaiseRunStates();
    }

    public void Dispose()
    {
        if (_runner is not null)
        {
            _runner.ItemStatusChanged -= OnItemStatusChanged;
            _runner.StateChanged -= OnExecutorStateChanged;
        }

        if (_workspace is not null)
        {
            _workspace.CurrentChanged -= OnWorkspaceCurrentChanged;
        }
    }

    /// <summary>切换实例后刷新共用区的下拉与可运行提示。</summary>
    private void OnWorkspaceCurrentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentInstance));
        OnPropertyChanged(nameof(CanRunOnCurrentInstance));
    }
}
