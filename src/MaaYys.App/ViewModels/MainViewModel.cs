using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using MaaYys.App.Services;
using MaaYys.App.Themes;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 主窗口 VM（P6.2 改成 MAA 式顶部页签）：一键长草 / 多实例 / 小工具 / 设置。
/// <para>
/// 连接、日志、定时这些原先的顶层页收进了「多实例」与「小工具」，
/// 顶层只留四个入口，和 MAA 的信息架构对齐。
/// </para>
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly InstanceWorkspace _workspace;

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    /// <summary>工作区（应用设置、实例、日志的入口；App 启动时读自启设置要用）。</summary>
    public InstanceWorkspace Workspace => _workspace;

    /// <summary>一键长草（原任务页）：左下 Link Start 与命令行调试都绑它，必须常驻。</summary>
    public TasksViewModel Tasks { get; }

    /// <summary>多实例：设备绑定 + 连接 + 实时截图 + 配置组。</summary>
    public InstancesViewModel InstancePage { get; }

    /// <summary>小工具：定时任务 / 运行日志。</summary>
    public ToolsViewModel Tools { get; }

    /// <summary>设置：外观、缓存与资源维护、关于。</summary>
    public SettingsViewModel SettingsPage { get; }

    private NavigationItem? _selectedItem;
    public NavigationItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetProperty(ref _selectedItem, value))
                return;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(Title));
        }
    }

    public ViewModelBase? CurrentPage => SelectedItem?.Page;

    public string Title => SelectedItem is null ? "MaaYys" : $"MaaYys — {SelectedItem.Name}";

    /// <summary>实例标签集合（P2.4；与实例页同一份数据源）。</summary>
    public ObservableCollection<InstanceViewModel> Instances => _workspace.Instances;

    /// <summary>当前激活实例（顶栏下拉选中项）。</summary>
    public InstanceViewModel? CurrentInstance
    {
        get => _workspace.Current;
        set
        {
            if (ReferenceEquals(_workspace.Current, value))
            {
                return;
            }

            _workspace.Current = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private string _themeLabel = "暗色";
    public string ThemeLabel
    {
        get => _themeLabel;
        private set => SetProperty(ref _themeLabel, value);
    }

    /// <summary>资源清单路径（启动时的「资源更新整理」需要）。</summary>
    public string? InterfacePath => _workspace.Resources.InterfacePath;

    /// <summary>状态栏：当前实例 + 连接状态 + 帧率。</summary>
    public string StatusText => _workspace.StatusSummary;

    // ---- P6.3 应用内 Toast（队列关键节点的轻提示，4 秒自隐） ----

    private string _toastTitle = string.Empty;
    private string _toastMessage = string.Empty;
    private bool _toastVisible;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public string ToastTitle
    {
        get => _toastTitle;
        private set => SetProperty(ref _toastTitle, value);
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    /// <summary>弹一条应用内提示，4 秒后自动消失。</summary>
    public void ShowToast(string title, string message)
    {
        ToastTitle = title ?? string.Empty;
        ToastMessage = message ?? string.Empty;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public void HideToast()
    {
        _toastTimer.Stop();
        ToastVisible = false;
    }

    public ICommand ToggleThemeCommand { get; }
    public ICommand AddInstanceCommand { get; }
    public ICommand CloseInstanceCommand { get; }
    public ICommand HideToastCommand { get; }

    public MainViewModel()
        : this(new InstanceWorkspace())
    {
    }

    public MainViewModel(InstanceWorkspace workspace)
    {
        _workspace = workspace;
        _workspace.CurrentChanged += OnWorkspaceCurrentChanged;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _workspace.Instances.CollectionChanged += OnInstancesChanged;
        _workspace.Notifier.ToastRequested += OnToastRequested;
        _toastTimer.Tick += (_, _) => HideToast();

        Tasks = new TasksViewModel(_workspace);
        InstancePage = new InstancesViewModel(_workspace);
        Tools = new ToolsViewModel(_workspace);
        SettingsPage = new SettingsViewModel(_workspace);

        NavigationItems.Add(new NavigationItem { Name = "一键长草", Glyph = "▶", PageFactory = () => Tasks });
        NavigationItems.Add(new NavigationItem { Name = "多实例", Glyph = "❐", PageFactory = () => InstancePage });
        NavigationItems.Add(new NavigationItem { Name = "小工具", Glyph = "⚒", PageFactory = () => Tools });
        NavigationItems.Add(new NavigationItem { Name = "设置", Glyph = "⚙", PageFactory = () => SettingsPage });

        SelectedItem = NavigationItems[0];

        ToggleThemeCommand = new RelayCommand(_ =>
        {
            var next = ThemeManager.Toggle();
            ThemeLabel = next == ThemeKind.Dark ? "暗色" : "明色";
        });

        AddInstanceCommand = new RelayCommand(_ => _workspace.Add());
        CloseInstanceCommand = new RelayCommand(parameter =>
        {
            if (parameter is InstanceViewModel target && _workspace.Instances.Count > 1)
            {
                _workspace.Remove(target, out _);
            }
        });
        HideToastCommand = new RelayCommand(_ => HideToast());

        // P5.4：定时调度随程序启动，不依赖「小工具」页是否被打开
        _workspace.Timers.Start();
    }

    /// <summary>实例清单路径（设置页 / 调试输出用）。</summary>
    public string InstanceStorePath => _workspace.Store.Path;

    /// <summary>切换页签（调试截图 --page N 用）。</summary>
    public void SelectPage(int index)
    {
        if (index >= 0 && index < NavigationItems.Count)
        {
            SelectedItem = NavigationItems[index];
        }
    }

    /// <summary>按名称选页签（调试截图 --tab 名称 用，避免改布局后下标漂移）。</summary>
    public bool SelectPageByName(string name)
    {
        NavigationItem? target = NavigationItems.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        SelectedItem = target;
        return true;
    }

    /// <summary>按名称或 Id 选择实例（调试截图 --instance 用）。</summary>
    public bool SelectInstance(string key)
    {
        InstanceViewModel? target = Instances.FirstOrDefault(i =>
                                     string.Equals(i.Id, key, StringComparison.OrdinalIgnoreCase)) ??
                                 Instances.FirstOrDefault(i =>
                                     string.Equals(i.DisplayName, key, StringComparison.Ordinal));
        if (target is null)
        {
            return false;
        }

        CurrentInstance = target;
        return true;
    }

    private void OnWorkspaceCurrentChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CurrentInstance));
        OnPropertyChanged(nameof(StatusText));
    }

    private void OnToastRequested(object? sender, ToastEventArgs e)
    {
        // 来自队列回调线程，但 NotificationService 已用 UI 同步上下文抛回主线程
        ShowToast(e.Title, e.Message);
    }

    private void OnInstancesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Instances));
    }

    /// <summary>
    /// 转发工作区的状态摘要变化。缺这一步时，实例「连接成功/失败」只刷新标签圆点，
    /// 状态栏会一直停在切换实例那一刻的文案（实测：标签显示已连接、状态栏仍写未连接）。
    /// </summary>
    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstanceWorkspace.StatusSummary))
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>窗口关闭时释放各页面与各实例持有的原生资源。</summary>
    public void Dispose()
    {
        _workspace.CurrentChanged -= OnWorkspaceCurrentChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        _workspace.Instances.CollectionChanged -= OnInstancesChanged;
        _workspace.Notifier.ToastRequested -= OnToastRequested;
        _toastTimer.Stop();

        // 常驻页面可能既被导航项持有、又被字段持有，用引用相等去重，避免同一 VM 释放两次
        HashSet<IDisposable> released = new(ReferenceEqualityComparer.Instance);

        foreach (NavigationItem item in NavigationItems)
        {
            if (item.CreatedPage is IDisposable disposable && released.Add(disposable))
            {
                disposable.Dispose();
            }
        }

        foreach (IDisposable extra in new IDisposable[] { Tasks, Tools, InstancePage })
        {
            if (released.Add(extra))
            {
                extra.Dispose();
            }
        }

        _workspace.Dispose();
    }
}
