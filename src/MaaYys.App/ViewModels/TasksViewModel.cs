using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;

namespace MaaYys.App.ViewModels;

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

/// <summary>左侧任务列表项。</summary>
public sealed class TaskOptionItemViewModel : ViewModelBase
{
    private readonly Action _onEnabledChanged;
    private bool _isEnabled;

    public TaskOptionItemViewModel(TaskConfig task, bool isEnabled, int optionCount, Action onEnabledChanged)
    {
        Task = task;
        _isEnabled = isEnabled;
        OptionCount = optionCount;
        _onEnabledChanged = onEnabledChanged;
    }

    public TaskConfig Task { get; }

    public string Label => Task.DisplayName;

    public string Entry => Task.Entry;

    public string GroupText => Task.Group is { Count: > 0 } ? string.Join(" / ", Task.Group) : "未分组";

    public int OptionCount { get; }

    public string OptionText => OptionCount == 0 ? "无参数" : $"{OptionCount} 项参数";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _onEnabledChanged();
            }
        }
    }
}

/// <summary>
/// 任务页（P3.4）：左边任务清单，右边**该任务的参数面板**，底部是编译出的 <c>pipeline_override</c> 预览。
/// <para>
/// 编辑对象是<b>当前配置组</b>（与实例页同一份数据）：勾选任务 = 加入队列，改参数 = 写进
/// <see cref="TaskSelection.Options"/>，两者都立即落盘。
/// </para>
/// <para>
/// 参数面板完全由 <c>interface.json</c> 的 option 定义驱动（29 个任务、94 个选项、四种类型），
/// 新增选项不用改界面代码。
/// </para>
/// </summary>
public sealed class TasksViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace? _workspace;
    private bool _loading;
    private TaskOptionItemViewModel? _selectedTask;

    public TasksViewModel()
        : this(null)
    {
    }

    public TasksViewModel(InstanceWorkspace? workspace)
    {
        _workspace = workspace;

        ResetTaskCommand = new RelayCommand(_ => ResetTaskOptions(), _ => SelectedTask is not null && OptionRows.Count > 0);
        ResetAllCommand = new RelayCommand(_ => ResetAllOptions(), _ => Tasks.Any(t => t.OptionCount > 0));

        if (workspace is not null && workspace.Resources.Config is not null)
        {
            foreach (TaskConfiguration group in workspace.Configurations.Configurations)
            {
                ConfigurationNames.Add(group.Name);
            }

            _selectedConfiguration = workspace.Configurations.Current ?? ConfigurationNames.FirstOrDefault() ?? string.Empty;
        }

        LoadTasks();
    }

    public ObservableCollection<string> ConfigurationNames { get; } = [];

    private string _selectedConfiguration = string.Empty;

    /// <summary>当前编辑的配置组（切换即重新加载勾选态与参数值）。</summary>
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
            LoadTasks();
        }
    }

    public ObservableCollection<TaskOptionItemViewModel> Tasks { get; } = [];

    public TaskOptionItemViewModel? SelectedTask
    {
        get => _selectedTask;
        set
        {
            if (!SetProperty(ref _selectedTask, value))
            {
                return;
            }

            RebuildOptionRows();
            OnPropertyChanged(nameof(TaskHeader));
            OnPropertyChanged(nameof(TaskHint));
            RaiseCommandStates();
        }
    }

    public string TaskHeader => SelectedTask is null ? "选择一个任务" : SelectedTask.Label;

    public string TaskHint => SelectedTask is null
        ? "左侧点任务名即可编辑它的参数；勾选框决定它是否加入任务队列。"
        : SelectedTask.OptionCount == 0
            ? "该任务没有可配置参数。"
            : $"{SelectedTask.OptionText} · 改动即时保存到配置组「{SelectedConfiguration}」";

    public int EnabledCount => Tasks.Count(t => t.IsEnabled);

    public string TaskSummary => $"已勾选 {EnabledCount} / {Tasks.Count} 个任务";

    public ObservableCollection<OptionRowViewModel> OptionRows { get; } = [];

    private string _preview = "{}";

    /// <summary>当前任务编译后的 <c>pipeline_override</c>（P5 投递任务时传的就是它）。</summary>
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

    // ---- 加载 ----

    private TaskConfiguration? CurrentGroup()
        => _workspace?.Configurations.Find(SelectedConfiguration) ?? _workspace?.Configurations.CurrentConfiguration;

    private void LoadTasks()
    {
        _loading = true;
        try
        {
            string? previous = SelectedTask?.Task.Name;
            Tasks.Clear();
            OptionRows.Clear();

            InterfaceConfig? config = _workspace?.Resources.Config;
            if (config is null)
            {
                Preview = "{}";
                return;
            }

            TaskConfiguration? group = CurrentGroup();
            HashSet<string> enabled = group is null
                ? []
                : [.. group.Tasks.Where(t => t.Enabled).Select(t => t.Name)];

            foreach (TaskConfig task in config.MergedTasks)
            {
                int count = TaskOptions.Describe(config, task).Count;
                Tasks.Add(new TaskOptionItemViewModel(task, enabled.Contains(task.Name), count, OnTaskEnabledChanged));
            }

            SelectedTask = Tasks.FirstOrDefault(t => t.Task.Name == previous) ?? Tasks.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(TaskSummary));
    }

    private IReadOnlyDictionary<string, string> CurrentValues()
    {
        TaskConfiguration? group = CurrentGroup();
        string? taskName = SelectedTask?.Task.Name;
        if (group is null || taskName is null)
        {
            return new Dictionary<string, string>();
        }

        return group.FindTask(taskName)?.Options ?? new Dictionary<string, string>();
    }

    private void RebuildOptionRows()
    {
        OptionRows.Clear();

        InterfaceConfig? config = _workspace?.Resources.Config;
        TaskConfig? task = SelectedTask?.Task;
        if (config is null || task is null)
        {
            Preview = "{}";
            return;
        }

        IReadOnlyDictionary<string, string> values = CurrentValues();
        IReadOnlyList<TaskOptionDefinition> rows = TaskOptions.Describe(config, task, values);

        foreach (TaskOptionDefinition row in rows)
        {
            if (!config.MergedOptions.TryGetValue(row.Key, out OptionConfig? option))
            {
                continue;
            }

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
                    value => OnInputChanged(row.Key, input.Name, value)));
            }

            OptionRows.Add(new OptionRowViewModel(
                row,
                current,
                inputs,
                value => OnSingleChoiceChanged(row.Key, value),
                _ => OnCheckChanged(row.Key)));
        }

        RefreshPreview();
        RaiseCommandStates();
    }

    // ---- 改动 ----

    /// <summary>
    /// 以代码方式给某个任务设置一个选项值（与界面操作走同一条保存路径）。
    /// 供自动化验证与 P5 的「按配置批量预置」使用。
    /// </summary>
    public bool ApplyOption(string taskName, string optionKey, string? value)
    {
        TaskOptionItemViewModel? item = Tasks.FirstOrDefault(t => string.Equals(t.Task.Name, taskName, StringComparison.Ordinal));
        if (item is null)
        {
            return false;
        }

        SelectedTask = item;

        TaskConfiguration? group = CurrentGroup();
        if (group is null)
        {
            return false;
        }

        group.EnsureTask(taskName, enabled: true).SetOption(optionKey, value);
        SaveGroup(group);
        RebuildOptionRows();
        OnPropertyChanged(nameof(TaskSummary));
        return true;
    }

    /// <summary>勾选任务 = 加入/移出配置组的任务队列。</summary>
    private void OnTaskEnabledChanged()
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

        foreach (TaskOptionItemViewModel item in Tasks)
        {
            if (item.IsEnabled)
            {
                group.EnsureTask(item.Task.Name, enabled: true);
            }
            else
            {
                TaskSelection? entry = group.FindTask(item.Task.Name);
                if (entry is not null)
                {
                    entry.Enabled = false;
                }
            }
        }

        // 未启用且没有任何参数取值的条目没有保留价值
        group.Tasks.RemoveAll(t => !t.Enabled && t.Options.Count == 0);
        SaveGroup(group);
        Message = $"{SelectedConfiguration}：{group.EnabledCount}/{Tasks.Count} 个任务已勾选";
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(TaskHint));
    }

    private TaskSelection? EnsureEntry()
    {
        TaskConfiguration? group = CurrentGroup();
        string? taskName = SelectedTask?.Task.Name;
        if (group is null || taskName is null)
        {
            return null;
        }

        return group.EnsureTask(taskName, enabled: true);
    }

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
        string? taskName = SelectedTask?.Task.Name;
        TaskSelection? entry = taskName is null ? null : group?.FindTask(taskName);
        if (group is null || entry is null)
        {
            return;
        }

        entry.Options.Clear();
        SaveGroup(group);
        RebuildOptionRows();
        Message = $"已把「{taskName}」的参数恢复为默认值";
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
        TaskConfig? task = SelectedTask?.Task;
        if (config is null || task is null)
        {
            Preview = "{}";
            return;
        }

        try
        {
            string json = TaskOptions.Compile(config, task, CurrentValues(), out IReadOnlyList<string> warnings);
            Preview = warnings.Count == 0
                ? Pretty(json)
                : Pretty(json) + Environment.NewLine + Environment.NewLine + "⚠ " + string.Join(Environment.NewLine + "⚠ ", warnings);
        }
        catch (JsonException ex)
        {
            Preview = $"编译失败：{ex.Message}";
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
    }

    public void Dispose()
    {
        // 任务页没有订阅外部事件，无需额外释放
    }
}
