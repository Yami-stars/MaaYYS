using System.Windows;
using System.Windows.Input;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 主界面右栏面板的折叠状态（P6.1）。
/// <para>
/// 行高交给 <see cref="RowHeight"/> 绑定到外层 <c>RowDefinition</c>：展开时是预设的 GridLength（Auto / N*），
/// 折叠时回落 <see cref="GridLength.Auto"/> —— 内容 Collapsed 后这一行只剩标题栏高度，不会留一块空白。
/// </para>
/// </summary>
public sealed class PanelStateViewModel : ViewModelBase
{
    private readonly GridLength _expandedHeight;
    private bool _isExpanded = true;

    public PanelStateViewModel(string title, string glyph, GridLength expandedHeight)
    {
        Title = title;
        Glyph = glyph;
        _expandedHeight = expandedHeight;
        ToggleCommand = new RelayCommand(_ => IsExpanded = !IsExpanded);
    }

    /// <summary>高度随内容（适合条目少的「连接」面板）。</summary>
    public static PanelStateViewModel AutoHeight(string title, string glyph)
        => new(title, glyph, GridLength.Auto);

    /// <summary>按权重占剩余空间（截图 / 日志面板）。</summary>
    public static PanelStateViewModel Star(string title, string glyph, double weight)
        => new(title, glyph, new GridLength(weight, GridUnitType.Star));

    public string Title { get; }

    public string Glyph { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RowHeight));
            OnPropertyChanged(nameof(Chevron));
        }
    }

    /// <summary>供 RowDefinition.Height 直接绑定。</summary>
    public GridLength RowHeight => _isExpanded ? _expandedHeight : GridLength.Auto;

    public string Chevron => _isExpanded ? "▾" : "▸";

    public ICommand ToggleCommand { get; }
}
