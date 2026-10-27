using System.Windows.Controls;

namespace MaaYys.App.Views.Pages;

/// <summary>
/// 任务页：左栏任务链已抽成 <see cref="Controls.TaskQueueView"/>（P6.1，与主界面共用），
/// 所以这里不再有拖拽 / 添加任务 / 重命名的交互代码。
/// </summary>
public partial class TasksView : UserControl
{
    public TasksView()
    {
        InitializeComponent();
    }
}
