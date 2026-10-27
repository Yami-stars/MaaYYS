using System.Windows;
using System.Windows.Controls;
using MaaYys.App.ViewModels;

namespace MaaYys.App.Views.Pages;

/// <summary>
/// 日志页后台代码：只负责「批量追加后滚动到底部」。
/// 逐条 Add 时不做 ScrollIntoView —— 一屏几十条会触发几十次布局，长跑必卡。
/// </summary>
public partial class LogsView : UserControl
{
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is not LogsViewModel viewModel)
        {
            return;
        }

        _viewModel = viewModel;
        viewModel.BatchAppended += OnBatchAppended;
        ScrollToEnd();
    }

    private void OnBatchAppended(object? sender, EventArgs e)
    {
        if (_viewModel?.AutoScroll == true)
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        int count = LogList.Items.Count;
        if (count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(LogList.Items[count - 1]);
    }

    private void Detach()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.BatchAppended -= OnBatchAppended;
        _viewModel = null;
    }
}
