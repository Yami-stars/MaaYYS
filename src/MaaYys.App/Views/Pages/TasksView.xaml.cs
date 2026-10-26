using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaaYys.App.ViewModels;

namespace MaaYys.App.Views.Pages;

public partial class TasksView : UserControl
{
    private string? _draggedId;
    private Point _dragStartPoint;

    public TasksView()
    {
        InitializeComponent();
    }

    // ---- 拖拽排序 ----

    private void QueueList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggedId = null;
        ListBoxItem? item = FindListBoxItem(e.OriginalSource as DependencyObject);
        if (item?.DataContext is QueueItemViewModel vm)
        {
            _draggedId = vm.Id;
            _dragStartPoint = e.GetPosition(QueueList);
        }
    }

    private void QueueList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedId is null)
        {
            return;
        }

        Point pos = e.GetPosition(QueueList);
        if (Math.Abs(pos.X - _dragStartPoint.X) > 4 || Math.Abs(pos.Y - _dragStartPoint.Y) > 4)
        {
            DragDrop.DoDragDrop(QueueList, _draggedId, DragDropEffects.Move);
        }
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is string)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is not string draggedId || string.IsNullOrEmpty(draggedId))
        {
            e.Handled = true;
            return;
        }

        Point point = e.GetPosition(QueueList);
        int targetIndex = IndexOfItemAt(point);
        ((TasksViewModel)DataContext).MoveInstanceTo(draggedId, targetIndex);
        _draggedId = null;
        e.Handled = true;
    }

    private int IndexOfItemAt(Point point)
    {
        for (int i = 0; i < QueueList.Items.Count; i++)
        {
            if (QueueList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container)
            {
                continue;
            }

            Point topLeft = container.TransformToAncestor(QueueList).Transform(new Point(0, 0));
            if (point.Y < topLeft.Y + container.ActualHeight / 2)
            {
                return i;
            }
        }

        return QueueList.Items.Count - 1;
    }

    private static ListBoxItem? FindListBoxItem(DependencyObject? obj)
    {
        while (obj is not null and not ListBoxItem)
        {
            obj = VisualTreeHelper.GetParent(obj);
        }

        return obj as ListBoxItem;
    }

    // ---- 添加任务 ----

    private void AddTaskBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
        {
            return;
        }

        if (e.AddedItems[0] is TaskCatalogItem item)
        {
            ((TasksViewModel)DataContext).AddTaskCommand.Execute(item.Name);
        }

        ((ComboBox)sender).SelectedIndex = -1; // 允许重复添加同类实例
    }

    // ---- 重命名提交 ----

    private void AliasBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QueueItemViewModel vm && vm.IsEditing)
        {
            vm.CommitEditCommand.Execute(null);
        }
    }

    private void AliasBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not QueueItemViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (sender is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            vm.CommitEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.IsEditing = false;
            e.Handled = true;
        }
    }
}
