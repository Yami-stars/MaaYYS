using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.App.Themes;

namespace MaaYys.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>「日志」页在导航里的下标（主界面「完整日志」按钮跳转用）。</summary>
    private const int LogsPageIndex = 3;

    private readonly InstanceWorkspace _workspace;

    public ObservableCollection<NavigationItem> NavigationItems { get; } = new();

    /// <summary>
    /// 共享任务页（P6.1）：顶部运行控制、主界面任务链、任务页三处共用一个实例，
    /// 否则在工作台里加的队列项任务页看不到。
    /// </summary>
    public TasksViewModel Tasks { get; }

    /// <summary>主界面（左侧任务链 + 右侧连接/截图/日志三面板）。</summary>
    public WorkbenchViewModel Workbench { get; }

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

    /// <summary>当前激活实例（标签条选中项）。</summary>
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

    public ICommand ToggleThemeCommand { get; }
    public ICommand AddInstanceCommand { get; }
    public ICommand CloseInstanceCommand { get; }

    public MainViewModel()
        : this(new InstanceWorkspace())
    {
    }

    public MainViewModel(InstanceWorkspace workspace)
    {
        _workspace = workspace;
        _workspace.CurrentChanged += OnWorkspaceCurrentChanged;
        _workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _workspace.Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Instances));

        // P6.1：任务页与主界面共用同一个 VM（顶部运行控制也绑它）
        Tasks = new TasksViewModel(_workspace);
        Workbench = new WorkbenchViewModel(_workspace, Tasks, () => SelectPage(LogsPageIndex));

        NavigationItems.Add(new NavigationItem { Name = "主界面", Glyph = "▣", PageFactory = () => Workbench });
        NavigationItems.Add(new NavigationItem { Name = "任务", Glyph = "≡", PageFactory = () => Tasks });
        NavigationItems.Add(new NavigationItem { Name = "定时", Glyph = "⏱", PageFactory = () => new TimersViewModel(_workspace) });
        NavigationItems.Add(new NavigationItem { Name = "日志", Glyph = "☰", PageFactory = () => new LogsViewModel(_workspace) });
        NavigationItems.Add(new NavigationItem { Name = "连接", Glyph = "⇄", PageFactory = () => new ConnectionViewModel() });
        NavigationItems.Add(new NavigationItem { Name = "实例", Glyph = "❐", PageFactory = () => new InstancesViewModel(_workspace) });
        NavigationItems.Add(new NavigationItem { Name = "设置", Glyph = "⚙", PageFactory = () => new SettingsViewModel(_workspace) });

        SelectedItem = NavigationItems[0];

        ToggleThemeCommand = new RelayCommand(_ =>
        {
            var next = ThemeManager.Toggle();
            ThemeLabel = next == ThemeKind.Dark ? "暗色" : "明色";
        });

        AddInstanceCommand = new RelayCommand(_ => _workspace.Add());
        CloseInstanceCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is InstanceViewModel target && _workspace.Instances.Count > 1)
                {
                    _workspace.Remove(target, out _);
                }
            });

        // P5.4：定时调度随程序启动，不依赖「定时」页是否被打开
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

        // 主界面与任务页是「常驻」VM（导航没打开过也要停定时器 / 退订），先各自释放
        Workbench.Dispose();
        Tasks.Dispose();

        foreach (NavigationItem item in NavigationItems)
        {
            if (item.CreatedPage is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _workspace.Dispose();
    }
}
