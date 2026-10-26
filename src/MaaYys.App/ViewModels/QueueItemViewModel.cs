using System.Windows.Input;
using MaaYys.Engine;
using MaaYys.Engine.Config;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 任务队列中的一项（对标 MAA TaskQueue 的一行）。
/// <para>
/// 直接持有对应的 <see cref="TaskSelection"/> 引用：勾选 / 重命名 / 选项编辑都会就地写回同一个对象，
/// <see cref="TasksViewModel"/> 只需在变更后调用保存即可，无需再用名字反查（多实例下也更精确）。
/// 命令回调由 <see cref="TasksViewModel"/> 注入，排序 / 删除 / 运行都落回配置与执行引擎。
/// </para>
/// </summary>
public sealed class QueueItemViewModel : ViewModelBase
{
    private readonly TaskSelection _selection;
    private readonly Action<string> _remove;
    private readonly Action<string> _moveUp;
    private readonly Action<string> _moveDown;
    private readonly Action<string> _runFromHere;
    private readonly Action<string, string> _rename;
    private readonly Action<bool> _enabledChanged;

    private string _alias;
    private bool _isEditing;
    private TaskRunStatus _status;
    private int _instanceCount;
    private int _index;
    private int _count;

    public QueueItemViewModel(
        TaskSelection selection,
        int instanceCount,
        Action<string> remove,
        Action<string> moveUp,
        Action<string> moveDown,
        Action<string> runFromHere,
        Action<string, string> rename,
        Action<bool> enabledChanged)
    {
        _selection = selection;
        _alias = selection.Alias ?? string.Empty;
        _instanceCount = instanceCount;
        _remove = remove;
        _moveUp = moveUp;
        _moveDown = moveDown;
        _runFromHere = runFromHere;
        _rename = rename;
        _enabledChanged = enabledChanged;

        RemoveCommand = new RelayCommand(_ => _remove(Id));
        MoveUpCommand = new RelayCommand(_ => _moveUp(Id), _ => CanMoveUp);
        MoveDownCommand = new RelayCommand(_ => _moveDown(Id), _ => CanMoveDown);
        RunFromHereCommand = new RelayCommand(_ => _runFromHere(Id));
        BeginEditCommand = new RelayCommand(_ => IsEditing = true);
        CommitEditCommand = new RelayCommand(_ =>
        {
            _rename(Id, _alias);
            IsEditing = false;
        });
    }

    public string Id => _selection.Id;

    /// <summary>任务入口名（interface.json 的 task 名）。</summary>
    public string Name => _selection.Name;

    /// <summary>对应的配置条目（编辑直接写回）。</summary>
    public TaskSelection Selection => _selection;

    /// <summary>同类实例数量（&gt;1 时界面提示「×N」）。</summary>
    public int InstanceCount
    {
        get => _instanceCount;
        set => SetProperty(ref _instanceCount, value);
    }

    public string Alias
    {
        get => _alias;
        set
        {
            if (SetProperty(ref _alias, value))
            {
                _selection.Alias = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    /// <summary>界面显示名：有别名用别名，否则用任务入口名。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(_alias) ? Name : _alias;

    public bool IsEnabled
    {
        get => _selection.Enabled;
        set
        {
            if (_selection.Enabled == value)
            {
                return;
            }

            _selection.Enabled = value;
            OnPropertyChanged(nameof(IsEnabled));
            _enabledChanged(value);
        }
    }

    /// <summary>是否处于重命名编辑态（inline 编辑用）。</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>运行态（P5.3 执行引擎驱动；P5.1 仅建模型与绑定）。</summary>
    public TaskRunStatus Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public IReadOnlyDictionary<string, string> Options => _selection.Options;

    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RunFromHereCommand { get; }
    public ICommand BeginEditCommand { get; }
    public ICommand CommitEditCommand { get; }

    public int Index
    {
        get => _index;
        set
        {
            if (SetProperty(ref _index, value))
            {
                RefreshPositionFlags();
            }
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                RefreshPositionFlags();
            }
        }
    }

    public bool CanMoveUp => _index > 0;
    public bool CanMoveDown => _index < _count - 1;

    private void RefreshPositionFlags()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        (MoveUpCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (MoveDownCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>重排后由 ViewModel 调用，刷新位置相关属性与命令可用态。</summary>
    public void RefreshPosition(int index, int count)
    {
        Index = index;
        Count = count;
    }
}
