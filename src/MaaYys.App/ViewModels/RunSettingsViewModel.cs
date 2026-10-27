using System.IO;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 运行设置（P6.2）：把 P5.3 / P5.4 里写死的策略参数暴露出来，
/// 再加防休眠 / 前后置脚本 / 完成后动作。
/// <para>
/// 这些参数**下一次启动队列时生效**（执行器按签名判断策略是否变化，变了就重建），
/// 运行中的队列不会被中途改策略 —— 那会让「为什么这次没重试」变得无法解释。
/// </para>
/// </summary>
public sealed class RunSettingsViewModel : SettingsSectionViewModel
{
    public RunSettingsViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
        ResetCommand = new RelayCommand(_ => ResetToDefaults(), _ => Persisted);
        PickPreScriptCommand = new RelayCommand(_ => PickScript(isPre: true), _ => Persisted);
        PickPostScriptCommand = new RelayCommand(_ => PickScript(isPre: false), _ => Persisted);
    }

    public ICommand ResetCommand { get; }

    public ICommand PickPreScriptCommand { get; }

    public ICommand PickPostScriptCommand { get; }

    /// <summary>完成后动作的下拉文案（顺序与 <see cref="RunSettings.AfterCompletionKinds"/> 一致）。</summary>
    public IReadOnlyList<string> AfterCompletionOptions { get; } = ["无动作", "退出程序", "关机"];

    // ---- 执行策略 ----

    /// <summary>停滞超时（秒，0 = 不检测）。</summary>
    public int StallTimeoutSeconds
    {
        get => Settings.Run.StallTimeoutSeconds;
        set
        {
            if (value == Settings.Run.StallTimeoutSeconds)
            {
                return;
            }

            Settings.Run.StallTimeoutSeconds = value;
            SaveSettings();
        }
    }

    public int MaxRetries
    {
        get => Settings.Run.MaxRetries;
        set
        {
            if (value == Settings.Run.MaxRetries)
            {
                return;
            }

            Settings.Run.MaxRetries = value;
            SaveSettings();
        }
    }

    public int RetryDelayMs
    {
        get => Settings.Run.RetryDelayMs;
        set
        {
            if (value == Settings.Run.RetryDelayMs)
            {
                return;
            }

            Settings.Run.RetryDelayMs = value;
            SaveSettings();
        }
    }

    public bool StopOnFailure
    {
        get => Settings.Run.StopOnFailure;
        set
        {
            if (value == Settings.Run.StopOnFailure)
            {
                return;
            }

            Settings.Run.StopOnFailure = value;
            SaveSettings();
        }
    }

    /// <summary>单任务时长上限（分钟；0 = 不限）。界面用分钟，存秒。</summary>
    public int MaxDurationMinutes
    {
        get => Settings.Run.MaxDurationSeconds / 60;
        set
        {
            int seconds = Math.Max(0, value) * 60;
            if (seconds == Settings.Run.MaxDurationSeconds)
            {
                return;
            }

            Settings.Run.MaxDurationSeconds = seconds;
            SaveSettings();
        }
    }

    /// <summary>定时器轮询间隔（秒）。</summary>
    public int TimerPollSeconds
    {
        get => Settings.Run.TimerPollSeconds;
        set
        {
            if (value == Settings.Run.TimerPollSeconds)
            {
                return;
            }

            Settings.Run.TimerPollSeconds = value;
            SaveSettings();

            // 定时器正在跑就按新间隔重启（否则要等下次启动程序才生效）
            bool restarted = Workspace?.Timers.ApplyInterval() ?? false;
            Message = restarted
                ? $"轮询间隔已改为 {Settings.Run.TimerPollSeconds} 秒（调度器已重启）"
                : $"轮询间隔已改为 {Settings.Run.TimerPollSeconds} 秒";
        }
    }

    public bool PreventSleep
    {
        get => Settings.Run.PreventSleep;
        set
        {
            if (value == Settings.Run.PreventSleep)
            {
                return;
            }

            Settings.Run.PreventSleep = value;
            SaveSettings();
        }
    }

    /// <summary>完成后动作（0 无动作 / 1 退出程序 / 2 关机）。</summary>
    public int AfterCompletionIndex
    {
        get
        {
            int index = Array.IndexOf(RunSettings.AfterCompletionKinds, Settings.Run.AfterCompletion);
            return index < 0 ? 0 : index;
        }

        set
        {
            int index = Math.Clamp(value, 0, AfterCompletionOptions.Count - 1);
            if (index == 2 && AfterCompletionIndex != 2)
            {
                var confirm = System.Windows.MessageBox.Show(
                    "队列正常跑完后将执行关机命令（60 秒倒计时，可用 shutdown /a 取消）。\n确定要选「关机」吗？",
                    "确认危险操作",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);
                if (confirm != System.Windows.MessageBoxResult.Yes)
                {
                    OnPropertyChanged();
                    return;
                }
            }

            string kind = RunSettings.AfterCompletionKinds[index];
            if (string.Equals(kind, Settings.Run.AfterCompletion, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Run.AfterCompletion = kind;
            SaveSettings();
            Log($"完成后动作改为「{AfterCompletionOptions[index]}」");
        }
    }

    // ---- 前后置脚本 ----

    public string PreScriptPath
    {
        get => Settings.Run.PreScriptPath;
        set
        {
            if (string.Equals(value, Settings.Run.PreScriptPath, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Run.PreScriptPath = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(PreScriptSummary));
        }
    }

    public string PostScriptPath
    {
        get => Settings.Run.PostScriptPath;
        set
        {
            if (string.Equals(value, Settings.Run.PostScriptPath, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Run.PostScriptPath = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(PostScriptSummary));
        }
    }

    public string PreScriptSummary => DescribeScript(PreScriptPath, "队列开始前执行");

    public string PostScriptSummary => DescribeScript(PostScriptPath, "队列结束后执行（成功或失败都会执行）");

    /// <summary>策略摘要（页面顶部一句话，长跑前扫一眼就知道会不会卡死不会自愈）。</summary>
    public string PolicySummary
    {
        get
        {
            RunSettings run = Settings.Run;
            string stall = run.StallTimeoutSeconds == 0
                ? "不检测停滞（任务卡住会一直等）"
                : $"{run.StallTimeoutSeconds} 秒无事件即判定卡死";
            string retry = run.MaxRetries == 0 ? "失败不重试" : $"失败重试 {run.MaxRetries} 次（间隔 {run.RetryDelayMs / 1000.0:0.#} 秒）";
            string stop = run.StopOnFailure ? "失败即停队列" : "失败后继续跑后续任务";
            string duration = run.MaxDurationSeconds == 0 ? "单任务不限时长" : $"单任务上限 {run.MaxDurationSeconds / 60} 分钟";
            return $"{stall} · {retry} · {stop} · {duration}";
        }
    }

    private static string DescribeScript(string path, string hint)
        => string.IsNullOrWhiteSpace(path)
            ? $"未配置（{hint}）"
            : File.Exists(path) ? path : $"文件不存在：{path}";

    private void PickScript(bool isPre)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = isPre ? "选择前置脚本" : "选择后置脚本",
            Filter = "脚本与程序|*.bat;*.cmd;*.exe;*.ps1|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (isPre)
        {
            PreScriptPath = dialog.FileName;
        }
        else
        {
            PostScriptPath = dialog.FileName;
        }
    }

    /// <summary>恢复默认策略（只重置运行参数，不动前后置脚本与完成后动作）。</summary>
    private void ResetToDefaults()
    {
        var defaults = new RunSettings();
        RunSettings run = Settings.Run;
        run.StallTimeoutSeconds = defaults.StallTimeoutSeconds;
        run.MaxRetries = defaults.MaxRetries;
        run.RetryDelayMs = defaults.RetryDelayMs;
        run.StopOnFailure = defaults.StopOnFailure;
        run.MaxDurationSeconds = defaults.MaxDurationSeconds;
        run.TimerPollSeconds = defaults.TimerPollSeconds;
        run.PreventSleep = defaults.PreventSleep;
        SaveSettings();
        Message = "已恢复默认策略";
        Log("运行策略已恢复默认");
    }
}
