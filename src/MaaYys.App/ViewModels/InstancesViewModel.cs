using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;
using MaaYys.Engine.Devices;

namespace MaaYys.App.ViewModels;

/// <summary>任务勾选项（实例编辑器里多选）。</summary>
public sealed class TaskSelectionItem : ViewModelBase
{
    private readonly Action _onChanged;

    public TaskSelectionItem(InstanceTaskOption option, bool isSelected, Action onChanged)
    {
        Entry = option.Entry;
        Label = option.Label;
        Group = option.Group;
        _isSelected = isSelected;
        _onChanged = onChanged;
    }

    public string Entry { get; }

    public string Label { get; }

    public string? Group { get; }

    public string GroupLabel => string.IsNullOrWhiteSpace(Group) ? "未分组" : Group!;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onChanged();
            }
        }
    }
}

/// <summary>
/// 实例管理页（P2.4）：左侧实例列表 + 右侧配置编辑器。
/// 编辑走「草稿」——改动 <see cref="Draft"/>，点保存才写回清单并落到 instances.json；
/// 未保存时标签条不变，避免边打字边污染持久化文件。
/// </summary>
public sealed class InstancesViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace _workspace;
    private InstanceDefinition _draft = new();
    private bool _syncingSelection;
    private bool _disposed;
    private string _selectedConfigurationName = string.Empty;
    private bool _loadingConfiguration;

    public InstancesViewModel(InstanceWorkspace workspace)
    {
        _workspace = workspace;

        AddCommand = new RelayCommand(_ => AddInstance());
        DuplicateCommand = new RelayCommand(_ => DuplicateInstance(), _ => Selected is not null);
        DeleteCommand = new RelayCommand(_ => DeleteInstance(), _ => Selected is not null && _workspace.Instances.Count > 1);
        SaveCommand = new RelayCommand(_ => SaveDraft(), _ => Selected is not null && IsDirty);
        RevertCommand = new RelayCommand(_ => LoadDraft(Selected), _ => Selected is not null && IsDirty);
        DetectCommand = new RelayCommand(_ => _ = DetectAsync(), _ => Selected is not null);
        SwitchResourceCommand = new RelayCommand(
            _ => _ = SwitchResourceAsync(),
            _ => Selected?.State == InstanceState.Connected && !string.IsNullOrWhiteSpace(ResourceName));

        NewConfigurationCommand = new RelayCommand(_ => NewConfiguration());
        DuplicateConfigurationCommand = new RelayCommand(
            _ => DuplicateConfiguration(),
            _ => !string.IsNullOrWhiteSpace(SelectedConfigurationName));
        DeleteConfigurationCommand = new RelayCommand(
            _ => DeleteConfiguration(),
            _ => !string.IsNullOrWhiteSpace(SelectedConfigurationName));
        RenameConfigurationCommand = new RelayCommand(
            _ => RenameConfiguration(),
            _ => !string.IsNullOrWhiteSpace(SelectedConfigurationName));
        ImportConfigurationCommand = new RelayCommand(_ => ImportConfiguration());
        ExportConfigurationCommand = new RelayCommand(
            _ => ExportConfiguration(),
            _ => !string.IsNullOrWhiteSpace(SelectedConfigurationName));

        _workspace.CurrentChanged += OnWorkspaceCurrentChanged;
        Selected = _workspace.Current ?? _workspace.Instances.FirstOrDefault();
    }

    // ---- 列表 ----

    /// <summary>实例列表（与标签条同一份集合）。</summary>
    public ObservableCollection<InstanceViewModel> Instances => _workspace.Instances;

    private InstanceViewModel? _selected;
    public InstanceViewModel? Selected
    {
        get => _selected;
        set
        {
            InstanceViewModel? previous = _selected;
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            // 跟随选中实例的连接/资源状态刷新命令可用性与提示
            if (previous is not null && !ReferenceEquals(previous, value))
            {
                previous.PropertyChanged -= OnSelectedInstancePropertyChanged;
            }

            if (value is not null)
            {
                value.PropertyChanged += OnSelectedInstancePropertyChanged;
            }

            RaiseCommandStates();
            OnPropertyChanged(nameof(ResourceHint));
            LoadDraft(value);

            if (value is not null && !_syncingSelection)
            {
                _syncingSelection = true;
                try
                {
                    _workspace.Current = value;
                }
                finally
                {
                    _syncingSelection = false;
                }
            }
        }
    }

    /// <summary>可选项来源提示（interface.json 是否就位）。</summary>
    public string CatalogHint => _workspace.Catalog.Loaded
        ? $"资源/任务清单来自 {_workspace.Catalog.InterfacePath}（资源 {_workspace.Catalog.ResourceNames.Count} 个 · 任务 {_workspace.Catalog.TaskCount} 个）"
        : _workspace.Catalog.Errors.FirstOrDefault() ?? "未读取到 interface.json，使用默认选项";

    /// <summary>清单文件路径（界面上让用户知道配置存哪）。</summary>
    public string StorePath => _workspace.Store.Path;

    public IReadOnlyList<string> ResourceNames => _workspace.Catalog.ResourceNames;

    public IReadOnlyList<string> ScreencapMethods => InstanceDefinition.Win32ScreencapMethods;

    public IReadOnlyList<string> InputMethods => InstanceDefinition.Win32InputMethods;

    public IReadOnlyList<int> FpsOptions { get; } = [2, 5, 10, 15, 30];

    public IReadOnlyList<string> KindOptions { get; } = ["桌面端（Win32）", "模拟器（ADB）"];

    /// <summary>任务勾选项（按草稿的 TaskEntries 还原选中态）。</summary>
    public ObservableCollection<TaskSelectionItem> TaskSelections { get; } = [];

    public string TaskSummary => _workspace.Catalog.TaskCount == 0
        ? "尚无任务清单（interface.json 在 P3.6 产出后可勾选）"
        : $"已勾选 {TaskSelections.Count(t => t.IsSelected)} / {TaskSelections.Count} 个任务";

    /// <summary>当前实例的资源加载/切换状态（P2.5）。</summary>
    public string ResourceHint => Selected?.ResourceStatus ?? string.Empty;

    // ---- 配置组（P2.6）----

    /// <summary>可选配置组名（下拉框数据源，顺序同 maayys.json）。</summary>
    public ObservableCollection<string> ConfigurationNames { get; } = [];

    /// <summary>
    /// 当前编辑的配置组名。写回 <see cref="InstanceDefinition.ConfigurationName"/>，
    /// 任务勾选列表随它切换（配置组才是任务的真实归属）。
    /// </summary>
    public string SelectedConfigurationName
    {
        get => _selectedConfigurationName;
        set
        {
            if (!SetProperty(ref _selectedConfigurationName, value))
            {
                return;
            }

            // 只有「用户主动选择」才写回实例绑定；加载时仅用于显示（空串保持「跟随 Current」语义）
            if (!_loadingConfiguration)
            {
                Draft.ConfigurationName = value;
                MarkDirty();
            }

            ApplyTasksFromConfiguration();
        }
    }

    /// <summary>配置组摘要 + 落盘位置。</summary>
    public string ConfigurationHint
    {
        get
        {
            TaskConfiguration? group = CurrentConfiguration();
            return group is null
                ? "尚无配置组，点「新建配置组」开始"
                : $"{group.Name} · {group.Summary} · 存于 {Path.GetFileName(_workspace.Configurations.Path)}";
        }
    }

    private TaskConfiguration? CurrentConfiguration()
        => _workspace.Configurations.Find(_selectedConfigurationName)
           ?? _workspace.ConfigurationFor(Draft);

    /// <summary>把下拉框与配置组状态对齐；<paramref name="select"/> 为空时沿用草稿绑定或 Current。</summary>
    private void RefreshConfigurationNames(string? select = null)
    {
        _loadingConfiguration = true;
        try
        {
            string preferred = select
                               ?? (string.IsNullOrWhiteSpace(Draft.ConfigurationName)
                                   ? _workspace.Configurations.Current ?? string.Empty
                                   : Draft.ConfigurationName);

            ConfigurationNames.Clear();
            foreach (TaskConfiguration group in _workspace.Configurations.Configurations)
            {
                ConfigurationNames.Add(group.Name);
            }

            SelectedConfigurationName = ConfigurationNames.Contains(preferred)
                ? preferred
                : ConfigurationNames.FirstOrDefault() ?? string.Empty;
        }
        finally
        {
            _loadingConfiguration = false;
        }

        OnPropertyChanged(nameof(ConfigurationHint));
    }

    /// <summary>按当前配置组重建任务勾选列表（勾选态 = 配置组里 Enabled 的任务）。</summary>
    private void ApplyTasksFromConfiguration()
    {
        TaskConfiguration? group = _workspace.Configurations.Find(_selectedConfigurationName);
        HashSet<string> enabled = group is null
            ? []
            : [.. group.Tasks.Where(t => t.Enabled).Select(t => t.Name)];

        TaskSelections.Clear();
        foreach (InstanceTaskOption option in _workspace.Catalog.Tasks)
        {
            TaskSelections.Add(new TaskSelectionItem(option, enabled.Contains(option.Entry), OnTaskSelectionChanged));
        }

        // 配置组里有、但当前 interface.json 未提供的任务（换资源/换清单后可能出现）——保留为可取消条目
        foreach (string stale in enabled.Where(e => _workspace.Catalog.Tasks.All(t => t.Entry != e)))
        {
            TaskSelections.Add(new TaskSelectionItem(new InstanceTaskOption(stale, stale, null), true, OnTaskSelectionChanged));
        }

        Draft.TaskEntries = [.. enabled];
        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(ConfigurationHint));
    }

    private void OnSelectedInstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstanceViewModel.State))
        {
            RaiseCommandStates();
        }
        else if (e.PropertyName is nameof(InstanceViewModel.ResourceStatus))
        {
            OnPropertyChanged(nameof(ResourceHint));
        }
    }

    // ---- 草稿与编辑器状态 ----

    /// <summary>编辑草稿（保存前不影响清单与标签条）。</summary>
    public InstanceDefinition Draft
    {
        get => _draft;
        private set
        {
            if (SetProperty(ref _draft, value))
            {
                OnPropertyChanged(nameof(SelectedDeviceKind));
                OnPropertyChanged(nameof(IsWin32Mode));
                OnPropertyChanged(nameof(IsAdbMode));
            }
        }
    }

    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private string _message = string.Empty;
    /// <summary>操作结果 / 校验错误提示（成功为绿色语义，失败为红色语义，由 XAML 依 <see cref="HasError"/> 着色）。</summary>
    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    private bool _hasError;
    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    private string _deviceHint = string.Empty;
    /// <summary>设备探测结果提示（确认正则可命中目标窗口）。</summary>
    public string DeviceHint
    {
        get => _deviceHint;
        private set => SetProperty(ref _deviceHint, value);
    }

    // ---- 编辑器包装属性（draft 无通知，故逐个转发）----

    public int SelectedDeviceKindIndex
    {
        get => (int)Draft.DeviceKind;
        set
        {
            Draft.DeviceKind = (InstanceDeviceKind)value;
            OnPropertyChanged(nameof(SelectedDeviceKind));
            OnPropertyChanged(nameof(IsWin32Mode));
            OnPropertyChanged(nameof(IsAdbMode));
            MarkDirty();
        }
    }

    public InstanceDeviceKind SelectedDeviceKind
    {
        get => Draft.DeviceKind;
        set
        {
            Draft.DeviceKind = value;
            OnPropertyChanged(nameof(SelectedDeviceKindIndex));
            OnPropertyChanged(nameof(IsWin32Mode));
            OnPropertyChanged(nameof(IsAdbMode));
            MarkDirty();
        }
    }

    public bool IsWin32Mode => Draft.DeviceKind == InstanceDeviceKind.Win32;

    public bool IsAdbMode => Draft.DeviceKind == InstanceDeviceKind.Adb;

    public string Name
    {
        get => Draft.Name;
        set
        {
            Draft.Name = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string Win32TitleRegex
    {
        get => Draft.Win32TitleRegex;
        set
        {
            Draft.Win32TitleRegex = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string Win32ClassRegex
    {
        get => Draft.Win32ClassRegex;
        set
        {
            Draft.Win32ClassRegex = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string AdbAddress
    {
        get => Draft.AdbAddress;
        set
        {
            Draft.AdbAddress = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string AdbConfig
    {
        get => Draft.AdbConfig;
        set
        {
            Draft.AdbConfig = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string ScreencapMethod
    {
        get => Draft.ScreencapMethod;
        set
        {
            Draft.ScreencapMethod = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string InputMethod
    {
        get => Draft.InputMethod;
        set
        {
            Draft.InputMethod = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public int PreviewFps
    {
        get => Draft.PreviewFps;
        set
        {
            Draft.PreviewFps = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string ResourceName
    {
        get => Draft.ResourceName;
        set
        {
            Draft.ResourceName = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string Note
    {
        get => Draft.Note;
        set
        {
            Draft.Note = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public ICommand AddCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand DetectCommand { get; }

    /// <summary>资源集热切换（P2.5）：把草稿里选的资源集应用到已连接实例，不重连不重启。</summary>
    public ICommand SwitchResourceCommand { get; }

    /// <summary>配置组管理（P2.6）。</summary>
    public ICommand NewConfigurationCommand { get; }
    public ICommand DuplicateConfigurationCommand { get; }
    public ICommand DeleteConfigurationCommand { get; }
    public ICommand RenameConfigurationCommand { get; }
    public ICommand ImportConfigurationCommand { get; }
    public ICommand ExportConfigurationCommand { get; }

    // ---- 行为 ----

    /// <summary>
    /// 新建实例时用 interface.json 的 <c>controller[].win32</c> 段作默认值（P3.6）。
    /// 只填界面下拉里存在的取值，避免清单写了界面不支持的方法导致保存时校验不过。
    /// </summary>
    private InstanceDefinition CreateDraftFromInterfaceDefaults()
    {
        var definition = new InstanceDefinition();
        if (_workspace.Resources.Win32Defaults is not { } win32)
        {
            return definition;
        }

        if (win32.TitleRegex is { } title)
        {
            definition.Win32TitleRegex = title;
        }

        definition.Win32ClassRegex = win32.ClassRegex ?? string.Empty;

        if (win32.Screencap is { } screencap
            && InstanceDefinition.Win32ScreencapMethods.Contains(screencap, StringComparer.OrdinalIgnoreCase))
        {
            definition.ScreencapMethod = screencap;
        }

        if (win32.Mouse is { } mouse
            && InstanceDefinition.Win32InputMethods.Contains(mouse, StringComparer.OrdinalIgnoreCase))
        {
            definition.InputMethod = mouse;
        }

        return definition;
    }

    private void LoadDraft(InstanceViewModel? source)
    {
        Draft = source?.Definition.Clone(source.Definition.Name) ?? CreateDraftFromInterfaceDefaults();

        if (source is not null)
        {
            // Clone 会换 Id/名字后缀，这里恢复原值：草稿只是编辑载体，Id 由保存时按 Selected 定位
            Draft.Id = source.Id;
            Draft.Name = source.Definition.Name;
            Draft.CreatedAt = source.Definition.CreatedAt;
            Draft.Win32WindowHandle = source.Definition.Win32WindowHandle;
        }

        // P2.6：任务勾选的真实归属是配置组，实例只记「用哪一组」。
        // 旧清单里的 TaskEntries 已由 ConfigurationStore.Load 迁移为同名配置组，这里按组重建列表即可。
        RefreshConfigurationNames();
        ApplyTasksFromConfiguration();

        IsDirty = false;
        HasError = false;
        Message = string.Empty;
        DeviceHint = string.Empty;

        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(Draft));
        OnPropertyChanged(string.Empty);

        // 切换实例/恢复草稿后重探一次设备，确认当前正则/地址能命中
        _ = DetectAsync();
    }

    private void OnTaskSelectionChanged()
    {
        List<string> selected = TaskSelections.Where(t => t.IsSelected).Select(t => t.Entry).ToList();
        Draft.TaskEntries = selected;

        // 直接写进配置组：已存在的任务只翻转 Enabled（保留其选项值），新勾选的追加。
        TaskConfiguration? group = _workspace.Configurations.Find(_selectedConfigurationName);
        if (group is not null)
        {
            HashSet<string> enabled = new(selected, StringComparer.Ordinal);
            foreach (string name in selected)
            {
                group.EnsureTask(name, enabled: true);
            }

            foreach (TaskSelection task in group.Tasks)
            {
                task.Enabled = enabled.Contains(task.Name);
            }

            // 未启用且没有任何选项取值的条目没有保留价值，顺手清掉避免摘要虚高
            group.Tasks.RemoveAll(t => !t.Enabled && t.Options.Count == 0);
        }

        OnPropertyChanged(nameof(TaskSummary));
        OnPropertyChanged(nameof(ConfigurationHint));
        MarkDirty();
    }

    private void MarkDirty()
    {
        IsDirty = true;
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevertCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void RaiseCommandStates()
    {
        (DuplicateCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SaveCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RevertCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DetectCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SwitchResourceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DuplicateConfigurationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DeleteConfigurationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RenameConfigurationCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ExportConfigurationCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void AddInstance()
    {
        InstanceViewModel? created = _workspace.Add();
        Selected = created;
        Message = created is null ? "新建失败" : $"已新建实例「{created.DisplayName}」";
        HasError = created is null;
        _ = DetectAsync();
    }

    private void DuplicateInstance()
    {
        if (Selected is null)
        {
            return;
        }

        InstanceViewModel? copy = _workspace.Duplicate(Selected);
        Selected = copy;
        Message = copy is null ? "复制失败" : $"已复制为「{copy.DisplayName}」";
        HasError = copy is null;
    }

    private void DeleteInstance()
    {
        if (Selected is null)
        {
            return;
        }

        string name = Selected.DisplayName;
        if (!_workspace.Remove(Selected, out string? error))
        {
            HasError = true;
            Message = $"删除失败：{error}";
            return;
        }

        Selected = _workspace.Current ?? _workspace.Instances.FirstOrDefault();
        HasError = false;
        Message = $"已删除实例「{name}」";
        RaiseCommandStates();
    }

    /// <summary>
    /// 热切换资源集（P2.5 验收项）：官服 ↔ B站服 之间切换不重连、不重启进程。
    /// 草稿里选的资源集会写回实例定义并持久化。
    /// </summary>
    private async Task SwitchResourceAsync()
    {
        if (Selected is null)
        {
            return;
        }

        string target = ResourceName;
        Message = $"正在切换资源集「{target}」…";
        HasError = false;

        (bool success, string message) = await Selected.SwitchResourceAsync(target).ConfigureAwait(true);
        Message = message;
        HasError = !success;
        OnPropertyChanged(nameof(ResourceHint));
        RaiseCommandStates();
    }

    // ---- 配置组行为（P2.6）----

    private string _configurationNameInput = string.Empty;

    /// <summary>「新建 / 重命名」用的名字输入框（留空则新建时自动取名）。</summary>
    public string ConfigurationNameInput
    {
        get => _configurationNameInput;
        set => SetProperty(ref _configurationNameInput, value);
    }

    private void NewConfiguration()
    {
        TaskConfiguration created = _workspace.Configurations.Add(ConfigurationNameInput);
        ConfigurationNameInput = string.Empty;
        RefreshConfigurationNames(created.Name);
        Draft.ConfigurationName = created.Name;   // 新建即绑定，意图明确
        MarkDirty();
        HasError = false;
        Message = $"已新建配置组「{created.Name}」，当前实例已切到该组";
        RaiseCommandStates();
    }

    private void DuplicateConfiguration()
    {
        TaskConfiguration? copy = _workspace.Configurations.Duplicate(SelectedConfigurationName);
        if (copy is null)
        {
            HasError = true;
            Message = "复制失败：配置组不存在";
            return;
        }

        RefreshConfigurationNames(copy.Name);
        Draft.ConfigurationName = copy.Name;      // 复制即绑定到副本
        MarkDirty();
        HasError = false;
        Message = $"已复制为「{copy.Name}」（任务与选项一并复制）";
        RaiseCommandStates();
    }

    private void DeleteConfiguration()
    {
        string name = SelectedConfigurationName;
        if (!_workspace.Configurations.Remove(name, out string? error))
        {
            HasError = true;
            Message = $"删除失败：{error}";
            return;
        }

        // 指向该组的实例改回「跟随 Current」，避免静默指向一个不存在的组
        _workspace.DetachConfiguration(name);
        RefreshConfigurationNames();
        ApplyTasksFromConfiguration();
        MarkDirty();
        HasError = false;
        Message = $"已删除配置组「{name}」（上一版仍在 {Path.GetFileName(_workspace.Configurations.Path)}.bak 里）";
        RaiseCommandStates();
    }

    private void RenameConfiguration()
    {
        string target = (ConfigurationNameInput ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            HasError = true;
            Message = "重命名失败：请先在名字输入框填写新名字";
            return;
        }

        string old = SelectedConfigurationName;
        if (!_workspace.Configurations.Rename(old, target, out string? error))
        {
            HasError = old == target ? false : true;
            Message = old == target ? "名字没有变化" : $"重命名失败：{error}";
            return;
        }

        // 实例绑定跟着改名，否则会静默回落到 Current
        _workspace.RebindConfiguration(old, target);
        ConfigurationNameInput = string.Empty;
        bool wasBound = string.Equals(Draft.ConfigurationName, old, StringComparison.Ordinal);
        RefreshConfigurationNames(target);
        if (wasBound)
        {
            Draft.ConfigurationName = target;
        }

        MarkDirty();
        HasError = false;
        Message = $"已重命名「{old}」→「{target}」";
        RaiseCommandStates();
    }

    private void ImportConfiguration()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入配置组",
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ImportResult result = _workspace.Configurations.Import(dialog.FileName);
        if (!result.Success)
        {
            HasError = true;
            Message = $"导入失败：{result.Error}";
            return;
        }

        RefreshConfigurationNames(result.Imported[0]);
        Draft.ConfigurationName = result.Imported[0];
        ApplyTasksFromConfiguration();
        MarkDirty();
        HasError = false;

        string note = result.Errors.Count > 0 ? $"（{string.Join("；", result.Errors)}）" : string.Empty;
        Message = $"已导入 {result.Imported.Count} 个配置组：{string.Join("、", result.Imported)}{note}";
        RaiseCommandStates();
    }

    private void ExportConfiguration()
    {
        string safeName = string.Concat(
            SelectedConfigurationName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出配置组",
            Filter = "配置文件 (*.json)|*.json",
            DefaultExt = ".json",
            FileName = $"maayys-{safeName}.json",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_workspace.Configurations.Export(dialog.FileName, SelectedConfigurationName))
        {
            HasError = true;
            Message = "导出失败：无法写入目标文件";
            return;
        }

        HasError = false;
        Message = $"已导出配置组「{SelectedConfigurationName}」到 {dialog.FileName}";
    }

    private void SaveDraft()
    {
        if (Selected is null)
        {
            return;
        }

        IReadOnlyList<string> errors = Draft.Validate();
        if (errors.Count > 0)
        {
            HasError = true;
            Message = "保存失败：\n" + string.Join("\n", errors);
            return;
        }

        Draft.UpdatedAt = DateTimeOffset.Now;
        if (!_workspace.Store.Update(Draft, out string? error))
        {
            HasError = true;
            Message = $"保存失败：{error}";
            return;
        }

        // 配置组落盘：任务勾选在 OnTaskSelectionChanged 里已写进内存里的组，这里做原子写。
        if (!string.IsNullOrWhiteSpace(SelectedConfigurationName))
        {
            _workspace.Configurations.Touch(SelectedConfigurationName);
        }

        HasError = false;
        Message = $"已保存「{Draft.Name}」到 {System.IO.Path.GetFileName(_workspace.Store.Path)}";
        IsDirty = false;
        Selected.NotifyDefinitionChanged();
        OnPropertyChanged(nameof(Instances));
        OnPropertyChanged(nameof(ConfigurationHint));
    }

    /// <summary>探测当前草稿目标命中的设备数，用于确认正则/地址填写正确。</summary>
    private async Task DetectAsync()
    {
        InstanceDefinition probing = Draft;
        try
        {
            bool isWin32 = probing.DeviceKind == InstanceDeviceKind.Win32;
            string? titleRegex = isWin32 ? probing.Win32TitleRegex : null;
            string? classRegex = isWin32 && !string.IsNullOrWhiteSpace(probing.Win32ClassRegex) ? probing.Win32ClassRegex : null;

            DeviceHint = "正在探测设备…";
            IReadOnlyList<DeviceDescriptor> devices = await Task.Run(
                () => DeviceScanner.ScanAll(titleRegex, classRegex, includeAdb: true)).ConfigureAwait(true);

            List<DeviceDescriptor> matched = isWin32
                ? devices.Where(d => d.Kind == DeviceKind.Win32).ToList()
                : devices.Where(d => d.Kind == DeviceKind.Adb).ToList();

            // 扫描期间可能已切换实例：结果只回填给发起探测的那份草稿
            if (!ReferenceEquals(Draft, probing))
            {
                return;
            }

            if (matched.Count == 0)
            {
                DeviceHint = isWin32
                    ? $"未命中窗口（标题正则「{probing.Win32TitleRegex}」）——请确认游戏已启动"
                    : "未发现 ADB 设备——请确认模拟器已启动且 adb devices 能看到设备";
                return;
            }

            string first = matched[0].Kind == DeviceKind.Win32
                ? $"{matched[0].DisplayName}（{matched[0].Detail}）"
                : $"{matched[0].DisplayName}（{matched[0].Adb?.Address}）";
            DeviceHint = $"命中 {matched.Count} 个设备，连接时优先使用：{first}";
        }
        catch (Exception ex)
        {
            DeviceHint = $"探测失败：{ex.Message}";
        }
    }

    private void OnWorkspaceCurrentChanged(object? sender, EventArgs e)
    {
        if (_syncingSelection || _workspace.Current is null || ReferenceEquals(_workspace.Current, Selected))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            Selected = _workspace.Current;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _workspace.CurrentChanged -= OnWorkspaceCurrentChanged;
    }
}
