using System.Collections.ObjectModel;
using MaaYys.App.Services;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 小工具页（P6.2）：左侧二级导航 + 右侧内容。
/// <para>
/// 定时任务与运行日志从顶层导航收进这里 —— 主界面只留「一键长草」，工具类内容不占顶部页签。
/// 复用 <see cref="NavigationItem"/> 的懒创建：切走再切回不丢状态。
/// </para>
/// </summary>
public sealed class ToolsViewModel : ViewModelBase, IDisposable
{
    private NavigationItem? _selected;

    public ToolsViewModel()
        : this(new InstanceWorkspace())
    {
    }

    public ToolsViewModel(InstanceWorkspace? workspace)
    {
        Items.Add(new NavigationItem { Name = "定时任务", Glyph = "⏱", PageFactory = () => new TimersViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "运行日志", Glyph = "☰", PageFactory = () => new LogsViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "设备探测", Glyph = "⇄", PageFactory = () => new ConnectionViewModel() });

        _selected = Items[0];
    }

    public ObservableCollection<NavigationItem> Items { get; } = [];

    public NavigationItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(Current));
            }
        }
    }

    public ViewModelBase? Current => _selected?.Page;

    public void Select(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Selected = Items[index];
        }
    }

    /// <summary>按名字切分类（调试参数 <c>--tool 运行日志</c>）；找不到返回 false。</summary>
    public bool Select(string name)
    {
        NavigationItem? item = Items.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return false;
        }

        Selected = item;
        return true;
    }

    /// <summary>当前分类的下标（调试 / 状态恢复用）。</summary>
    public int SelectedIndex => _selected is null ? -1 : Items.IndexOf(_selected);

    public void Dispose()
    {
        foreach (NavigationItem item in Items)
        {
            if (item.CreatedPage is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
