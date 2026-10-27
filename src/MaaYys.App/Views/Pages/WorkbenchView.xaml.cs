using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MaaYys.App.ViewModels;

namespace MaaYys.App.Views.Pages;

public partial class WorkbenchView : UserControl
{
    private WorkbenchViewModel? _viewModel;

    public WorkbenchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LogTail.CollectionChanged -= OnLogTailChanged;
        }

        _viewModel = e.NewValue as WorkbenchViewModel;

        if (_viewModel is not null)
        {
            _viewModel.LogTail.CollectionChanged += OnLogTailChanged;
        }
    }

    /// <summary>日志尾巴自动跟到最新一行（同一次批量追加只滚一次）。</summary>
    private void OnLogTailChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0)
        {
            return;
        }

        Dispatcher.InvokeAsync(
            () => LogList.ScrollIntoView(LogList.Items[^1]),
            DispatcherPriority.Background);
    }
}
