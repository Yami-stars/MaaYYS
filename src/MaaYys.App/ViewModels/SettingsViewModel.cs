using System.Collections.ObjectModel;
using MaaYys.App.Services;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 设置页（P6.2）：左侧二级导航 + 右侧内容，对标 MAA 的设置中心。
/// <para>
/// 五个分类：连接 / 启动 / 运行 / 日志与缓存 / 关于。每个子页直接读写
/// <c>config/maayys.json</c> 的 <c>settings</c> 段（即时生效，没有「保存」按钮）。
/// </para>
/// <para>
/// 为什么分区：设置项从 2 项涨到 20+ 项后，一页滚到底会让人找不到东西；
/// 分类后每页只回答一个问题（连不上 / 游戏没开 / 跑一半卡住 / 日志与空间 / 版本与路径）。
/// </para>
/// </summary>
public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private NavigationItem? _selected;

    public SettingsViewModel()
        : this(null)
    {
    }

    public SettingsViewModel(InstanceWorkspace? workspace)
    {
        Items.Add(new NavigationItem { Name = "连接", Glyph = "⇄", PageFactory = () => new ConnectionSettingsViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "启动", Glyph = "▶", PageFactory = () => new StartupSettingsViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "运行", Glyph = "⚡", PageFactory = () => new RunSettingsViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "日志与缓存", Glyph = "☰", PageFactory = () => new StorageSettingsViewModel(workspace) });
        Items.Add(new NavigationItem { Name = "关于", Glyph = "ℹ", PageFactory = () => new AboutViewModel(workspace) });

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

    /// <summary>按名字切分类（调试参数 <c>--settings 连接</c>）。</summary>
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

    /// <summary>按下标切分类（0 连接 / 1 启动 / 2 运行 / 3 日志与缓存 / 4 关于）。</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            Selected = Items[index];
        }
    }

    /// <summary>
    /// 清空图片缓存（调试参数 <c>--clear-cache</c> 用）：转发到「日志与缓存」子页，
    /// 并确保该子页已创建。
    /// </summary>
    public Task ClearAsync()
    {
        Select("日志与缓存");
        return Current is StorageSettingsViewModel storage ? storage.ClearAsync() : Task.CompletedTask;
    }

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
