using System.IO;
using System.Windows.Input;
using MaaYys.App.Services;
using MaaYys.Engine.Config;
using MaaYys.Engine.Logging;

namespace MaaYys.App.ViewModels;

/// <summary>
/// 启动设置（P6.2）：前置程序（游戏本体）与开机自启。
/// <para>
/// 前置程序存在两个触发点：① 连接时没找到窗口 → 自动拉起并等窗口出现；
/// ② 本页「立即启动」按钮 → 手动试跑一次（不用先断开再连）。
/// </para>
/// </summary>
public sealed class StartupSettingsViewModel : SettingsSectionViewModel
{
    private bool _launching;

    public StartupSettingsViewModel(InstanceWorkspace? workspace)
        : base(workspace)
    {
        BrowseCommand = new RelayCommand(_ => Browse(), _ => Persisted);
        LaunchNowCommand = new RelayCommand(_ => _ = LaunchNowAsync(), _ => !_launching && CanLaunchNow);
        RefreshAutoStartCommand = new RelayCommand(_ => OnPropertyChanged(nameof(AutoStartStatus)));
    }

    public ICommand BrowseCommand { get; }

    public ICommand LaunchNowCommand { get; }

    public ICommand RefreshAutoStartCommand { get; }

    public string PreProgramPath
    {
        get => Settings.Startup.PreProgramPath;
        set
        {
            if (string.Equals(value, Settings.Startup.PreProgramPath, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Startup.PreProgramPath = value ?? string.Empty;
            SaveSettings();
            OnPropertyChanged(nameof(CanLaunchNow));
            OnPropertyChanged(nameof(PreProgramSummary));
        }
    }

    public string PreProgramArguments
    {
        get => Settings.Startup.PreProgramArguments;
        set
        {
            if (string.Equals(value, Settings.Startup.PreProgramArguments, StringComparison.Ordinal))
            {
                return;
            }

            Settings.Startup.PreProgramArguments = value ?? string.Empty;
            SaveSettings();
        }
    }

    /// <summary>启动后等待窗口出现的上限（秒）。</summary>
    public int PreProgramWaitSeconds
    {
        get => Settings.Startup.PreProgramWaitSeconds;
        set
        {
            if (value == Settings.Startup.PreProgramWaitSeconds)
            {
                return;
            }

            Settings.Startup.PreProgramWaitSeconds = value;
            SaveSettings();
        }
    }

    /// <summary>连接时若没找到窗口，是否自动拉起前置程序。</summary>
    public bool AutoLaunchOnConnect
    {
        get => Settings.Startup.AutoLaunchOnConnect;
        set
        {
            if (value == Settings.Startup.AutoLaunchOnConnect)
            {
                return;
            }

            Settings.Startup.AutoLaunchOnConnect = value;
            SaveSettings();
        }
    }

    public bool AutoStartWithWindows
    {
        get => Settings.Startup.AutoStartWithWindows;
        set
        {
            if (value == Settings.Startup.AutoStartWithWindows)
            {
                return;
            }

            // 先写注册表：写失败就不把开关留在「开」的位置（否则用户以为设好了）
            string? error = AutoStartService.Apply(value, Settings.Startup.SilentStart);
            if (error is not null)
            {
                Message = $"开机自启设置失败：{error}";
                OnPropertyChanged(nameof(AutoStartWithWindows));
                return;
            }

            Settings.Startup.AutoStartWithWindows = value;
            SaveSettings();
            OnPropertyChanged(nameof(AutoStartWithWindows));
            OnPropertyChanged(nameof(AutoStartStatus));
            Message = value ? "已加入开机启动项" : "已移除开机启动项";
            Log(Message);
        }
    }

    public bool SilentStart
    {
        get => Settings.Startup.SilentStart;
        set
        {
            if (value == Settings.Startup.SilentStart)
            {
                return;
            }

            Settings.Startup.SilentStart = value;
            SaveSettings();
            OnPropertyChanged(nameof(SilentStart));

            // 已在开机启动里就直接改命令，不用用户重开一遍开关
            if (Settings.Startup.AutoStartWithWindows)
            {
                string? error = AutoStartService.Apply(true, value);
                if (error is not null)
                {
                    Message = $"静默启动设置失败：{error}";
                }
            }

            OnPropertyChanged(nameof(AutoStartStatus));
        }
    }

    /// <summary>前置程序的启动进度 / 结果（与全局 <see cref="SettingsSectionViewModel.Message"/> 分开，
    /// 否则「已加入开机启动项」和「正在启动前置程序…」会互相覆盖）。</summary>
    public string LaunchMessage
    {
        get => _launchMessage;
        private set => SetProperty(ref _launchMessage, value);
    }

    private string _launchMessage = string.Empty;

    /// <summary>「立即启动」是否可点：路径配了且文件存在。</summary>
    public bool CanLaunchNow => !string.IsNullOrWhiteSpace(PreProgramPath) && File.Exists(PreProgramPath);

    /// <summary>前置程序一行摘要（路径不存在时直接说清楚）。</summary>
    public string PreProgramSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PreProgramPath))
            {
                return "未配置：连接时若没找到游戏窗口，会直接报「未找到匹配窗口」";
            }

            return File.Exists(PreProgramPath)
                ? PreProgramWaitSeconds > 0
                    ? $"已配置：找不到窗口时启动它，最多等 {PreProgramWaitSeconds} 秒"
                    : "已配置：启动后不等待窗口（立即重试连接，通常需要配合较慢的游戏启动）"
                : $"路径不存在：{PreProgramPath}";
        }
    }

    /// <summary>注册表里的实际状态（可能与内存里的开关不一致 —— 被别处删掉时会显示出来）。</summary>
    public string AutoStartStatus
    {
        get
        {
            string? command = AutoStartService.CurrentCommand();
            return string.IsNullOrWhiteSpace(command)
                ? "注册表里没有启动项"
                : $"注册表命令：{command}";
        }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择前置程序",
            Filter = "可执行文件|*.exe;*.lnk;*.bat;*.cmd|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() == true)
        {
            PreProgramPath = dialog.FileName;
            Message = $"已选择：{System.IO.Path.GetFileName(dialog.FileName)}";
        }
    }

    /// <summary>手动启动一次（走与连接时相同的路径，便于先验证路径对不对）。</summary>
    private async Task LaunchNowAsync()
    {
        if (Workspace is null || _launching)
        {
            return;
        }

        InstanceDefinition definition = Workspace.Current?.Definition
            ?? new InstanceDefinition { Win32TitleRegex = Settings.Connection.Win32TitleRegex };

        _launching = true;
        (LaunchNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            // 手动启动不管「连接时自动启动」开关，否则按钮会因为开关关着而什么都不做
            LaunchResult result = await Workspace.Launch.LaunchAndWaitAsync(
                definition,
                message => LaunchMessage = message,
                ignoreToggle: true).ConfigureAwait(true);

            LaunchMessage = result.Outcome switch
            {
                LaunchOutcome.WindowReady => "已启动，窗口已出现，可以连接了",
                LaunchOutcome.WindowMissing => result.Message,
                LaunchOutcome.NotFound => result.Message,
                LaunchOutcome.Failed => result.Message,
                _ => "未配置前置程序路径",
            };
            Log(
                $"手动启动前置程序：{result.Outcome} · {result.Message}",
                result.Outcome == LaunchOutcome.WindowReady ? LogLevel.Info : LogLevel.Warn);
        }
        finally
        {
            _launching = false;
            (LaunchNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }
}
