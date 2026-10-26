namespace MaaYys.App.ViewModels;

/// <summary>左侧导航项；P2.1 为占位页，P5/P6 替换为真实页面。</summary>
public sealed class NavigationItem
{
    public string Name { get; init; } = string.Empty;

    /// <summary>图标用文本占位（P6.4 换成图标字体 / PathGeometry）。</summary>
    public string Glyph { get; init; } = string.Empty;

    public required Func<ViewModelBase> PageFactory { get; init; }

    private ViewModelBase? _page;

    /// <summary>懒创建 + 缓存，保证切换页签时页面状态不丢失。</summary>
    public ViewModelBase Page => _page ??= PageFactory();

    /// <summary>已创建的页面实例（未访问过则为 null），供整体释放时遍历。</summary>
    public ViewModelBase? CreatedPage => _page;
}
