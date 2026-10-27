using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using MaaYys.App.ViewModels;
using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Execution;
using MaaYys.Engine.Logging;
using MaaYys.Engine.Notification;

namespace MaaYys.App.Services;

/// <summary>
/// 队列执行入口（P5.3 落地 / P5.4 复用）：整个程序只有**一份**执行器，
/// 任务页的「开始运行」与定时器到点触发都走这里。
/// <para>
/// 为什么提到 workspace 层：定时触发时任务页可能根本没创建过，
/// 若执行器归页面所有，定时就得自己再起一份 —— 两个队列会同时向 Maafw 投递任务。
/// 统一到 <see cref="InstanceWorkspace"/> 后，天然互斥：正在跑就直接拒绝第二次启动。
/// </para>
/// </summary>
public sealed class QueueRunService : ViewModelBase, IDisposable
{
    /// <summary>脚本最长执行时间：卡住的脚本不能把整条队列拖死。</summary>
    private static readonly TimeSpan ScriptTimeout = TimeSpan.FromMinutes(2);

    private readonly InstanceWorkspace _workspace;

    /// <summary>固定策略（测试 / 特殊调用方用）；为 null 时每次运行都从「运行设置」取。</summary>
    private readonly ExecutionOptions? _optionsOverride;

    /// <summary>执行器策略签名：设置页改了策略，下次运行自动重建执行器。</summary>
    private string? _executorSignature;

    /// <summary>防休眠令牌（运行时持有，结束时释放）。</summary>
    private IDisposable? _sleepToken;

    private QueueExecutor? _executor;
    private MaaEngineExecutionHost? _host;
    private MaaYysEngine? _engine;
    private bool _starting;
    private bool _disposed;

    /// <summary>通知聚合（P6.3）；为 null 时（测试 / 无工作区）不通知。</summary>
    private readonly NotificationService? _notifier;

    /// <summary>运行期通知定时器（卡住超时 / 提醒间隔共用，15 秒一跳）。</summary>
    private Timer? _notifyTimer;

    private DateTime _runStartUtc;
    private DateTime _lastActivityUtc;
    private bool _stuckFired;
    private int _reminderCount;

    public QueueRunService(InstanceWorkspace workspace, NotificationService? notifier = null, ExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _notifier = notifier;
        _optionsOverride = options;
    }

    /// <summary>队列项运行态变化（供任务页着色）。事件来自引擎线程，订阅方需自行切 UI 线程。</summary>
    public event EventHandler<QueueItemStatusChangedEventArgs>? ItemStatusChanged;

    /// <summary>执行器状态变化（Running / Paused / Stopping / Idle）。</summary>
    public event EventHandler<ExecutionState>? StateChanged;

    public ExecutionState State => _executor?.State ?? ExecutionState.Idle;

    /// <summary>是否正在占用引擎（运行 / 暂停 / 停止中）。</summary>
    public bool IsBusy => _starting ||
                          State is ExecutionState.Running or ExecutionState.Paused or ExecutionState.Stopping;

    /// <summary>正在运行的队列项（空闲为 null）。</summary>
    public ExecutionItem? CurrentItem => _executor?.CurrentItem;

    /// <summary>把配置组的队列编译成执行项（与参数面板预览同一条编译路径）。</summary>
    public List<ExecutionItem> BuildItems(TaskConfiguration group)
    {
        ArgumentNullException.ThrowIfNull(group);
        InterfaceConfig? config = _workspace.Resources.Config;
        return config is null
            ? []
            : ExecutionPlan.Build(config, group.Tasks, group.GlobalOptions);
    }

    /// <summary>
    /// 开始执行一个配置组的队列。
    /// </summary>
    /// <exception cref="InvalidOperationException">引擎未就绪、队列为空，或已有队列在跑。</exception>
    public async Task<ExecutionSummary> RunAsync(TaskConfiguration group, int startIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(group);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsBusy)
        {
            throw new InvalidOperationException("已有队列在运行，请先停止");
        }

        MaaYysEngine? engine = _workspace.Current?.Engine;
        if (engine is null)
        {
            throw new InvalidOperationException("当前实例未连接：请先在实例页连接设备");
        }

        if (!engine.Ready)
        {
            throw new InvalidOperationException("引擎未就绪：请先连接设备并完成资源加载");
        }

        List<ExecutionItem> items = BuildItems(group);
        if (items.Count == 0)
        {
            throw new InvalidOperationException("队列为空：先添加任务");
        }

        if (!items.Any(i => i.Enabled))
        {
            throw new InvalidOperationException("队列里没有启用的任务");
        }

        QueueExecutor executor = EnsureExecutor(engine);
        _starting = true;
        OnPropertyChanged(nameof(IsBusy));

        RunSettings run = _workspace.Configurations.Settings.Run;

        // P6.3：启动运行期通知定时器（卡住超时 / 提醒间隔）
        _runStartUtc = DateTime.UtcNow;
        _lastActivityUtc = DateTime.UtcNow;
        _stuckFired = false;
        _reminderCount = 0;
        StartNotifyTimer();

        // P6.2：执行期间阻止系统休眠（长跑最容易被「睡过去」打断）
        if (run.PreventSleep && _sleepToken is null)
        {
            _sleepToken = SleepGuard.Acquire();
            if (SleepGuard.IsActive)
            {
                _workspace.Log.Debug("Runner", "已阻止系统休眠（队列结束后自动恢复）", "queue");
            }
        }

        _workspace.Log.Write(
            LogLevel.Info,
            "Runner",
            $"开始执行配置组「{group.Name}」：{items.Count(i => i.Enabled)} 项启用 / 共 {items.Count} 项"
                + (startIndex > 0 ? $"，从第 {startIndex + 1} 项开始" : string.Empty),
            "queue");

        Task<ExecutionSummary> task;
        try
        {
            // P6.2：前置脚本（失败只记日志，不阻断任务）
            await RunScriptAsync(run.PreScriptPath, "前置脚本").ConfigureAwait(true);
            task = executor.RunAsync(items, Math.Clamp(startIndex, 0, items.Count));
        }
        catch
        {
            _starting = false;
            OnPropertyChanged(nameof(IsBusy));
            ReleaseSleepGuard();
            throw;
        }

        _ = task.ContinueWith(
            t => LogSummary(group, t),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = task.ContinueWith(
            _ =>
            {
                _starting = false;
                OnPropertyChanged(nameof(IsBusy));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = task.ContinueWith(
            t => FinishRun(run, group, t),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await task.ConfigureAwait(true);
    }

    /// <summary>停止整个队列。返回 false 表示当前没在跑。</summary>
    public bool Stop() => _executor?.Stop() ?? false;

    /// <summary>暂停：中止当前任务并记住下标。</summary>
    public bool Pause() => _executor?.Pause() ?? false;

    /// <summary>从暂停点继续；返回 null 表示当前不是暂停态。</summary>
    public Task<ExecutionSummary>? Resume() => _executor?.Resume();

    /// <summary>取（或重建）执行器：引擎换了（切实例/重连）必须换宿主。</summary>
    private QueueExecutor EnsureExecutor(MaaYysEngine engine)
    {
        ExecutionOptions options = _optionsOverride ?? ExecutionOptions.FromRunSettings(_workspace.Configurations.Settings.Run);
        string signature = Signature(options);

        if (_executor is not null && ReferenceEquals(_engine, engine) && string.Equals(signature, _executorSignature, StringComparison.Ordinal))
        {
            return _executor;
        }

        DisposeExecutor();
        _engine = engine;
        _executorSignature = signature;
        _host = new MaaEngineExecutionHost(engine);
        _executor = new QueueExecutor(_host, options);
        _executor.ItemStatusChanged += OnItemStatusChanged;
        _executor.StateChanged += OnExecutorStateChanged;
        return _executor;
    }

    private void DisposeExecutor()
    {
        if (_executor is not null)
        {
            _executor.ItemStatusChanged -= OnItemStatusChanged;
            _executor.StateChanged -= OnExecutorStateChanged;
            _executor.Dispose();
            _executor = null;
        }

        _host?.Dispose();
        _host = null;
        _engine = null;
        _executorSignature = null;
    }

    private void OnItemStatusChanged(object? sender, QueueItemStatusChangedEventArgs e)
    {
        // P6.3：任何任务状态变化都算「有进展」，刷新卡住计时并解除已触发的卡住提示
        _lastActivityUtc = DateTime.UtcNow;
        _stuckFired = false;

        LogItem(e);
        ItemStatusChanged?.Invoke(this, e);
    }

    /// <summary>队列项的运行态变化 → 日志（P5.5：失败/跳过会自动带截图卡片）。</summary>
    private void LogItem(QueueItemStatusChangedEventArgs e)
    {
        string name = string.IsNullOrWhiteSpace(e.Item.DisplayName) ? e.Item.Entry : e.Item.DisplayName;
        string duration = e.Run?.Duration is { } span ? $"（{span.TotalSeconds:F1}s）" : string.Empty;

        switch (e.Status)
        {
            case TaskRunStatus.Running:
                _workspace.Log.Debug("Runner", $"开始执行：{name}", "queue");
                break;
            case TaskRunStatus.Done:
                _workspace.Log.Info("Runner", $"完成：{name}{duration}", "queue");
                break;
            case TaskRunStatus.Failed:
                _workspace.Log.Error(
                    "Runner",
                    $"失败：{name}{duration}{(string.IsNullOrWhiteSpace(e.Message) ? string.Empty : $" · {e.Message}")}",
                    "queue");
                break;
            case TaskRunStatus.Skipped:
                _workspace.Log.Warn(
                    "Runner",
                    $"跳过：{name}{(string.IsNullOrWhiteSpace(e.Message) ? string.Empty : $" · {e.Message}")}",
                    "queue");
                break;
            case TaskRunStatus.Stopped:
                _workspace.Log.Warn("Runner", $"已停止：{name}", "queue");
                break;
        }
    }

    /// <summary>整轮汇总（成功/失败/跳过 + 结束原因）。</summary>
    private void LogSummary(TaskConfiguration group, Task<ExecutionSummary> task)
    {
        try
        {
            ExecutionSummary summary = task.Result;
            LogLevel level = summary.Failed > 0 ? LogLevel.Warn : LogLevel.Info;
            _workspace.Log.Write(
                level,
                "Runner",
                $"配置组「{group.Name}」结束：{summary.Outcome} · 成功 {summary.Succeeded} · 失败 {summary.Failed} · 跳过 {summary.Skipped}"
                    + (string.IsNullOrWhiteSpace(summary.Message) ? string.Empty : $" · {summary.Message}"),
                "queue");
        }
        catch (AggregateException ex)
        {
            _workspace.Log.Error("Runner", $"配置组「{group.Name}」执行异常：{ex.InnerException?.Message ?? ex.Message}", "queue");
        }
    }

    private void OnExecutorStateChanged(object? sender, ExecutionState state)
    {
        switch (state)
        {
            case ExecutionState.Paused:
                _workspace.Log.Warn("Runner", "队列已暂停（继续时从当前任务重跑）", "queue");
                break;
            case ExecutionState.Stopping:
                _workspace.Log.Info("Runner", "正在停止队列…", "queue");
                break;
            case ExecutionState.Idle:
                _workspace.Log.Debug("Runner", "队列回到空闲", "queue");
                break;
        }

        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsBusy));
        StateChanged?.Invoke(this, state);
    }

    // ---- P6.2 运行设置相关 ----

    private static string Signature(ExecutionOptions options)
        => $"{options.StallTimeoutSeconds}|{options.MaxRetries}|{options.RetryDelayMs}|{options.StopOnFailure}|{options.MaxDurationSeconds}";

    private void ReleaseSleepGuard()
    {
        _sleepToken?.Dispose();
        _sleepToken = null;
    }

    /// <summary>跑完一轮后的收尾：停通知定时器 → 后置脚本 → 释放防休眠 → 通知 → 完成后动作。</summary>
    private void FinishRun(RunSettings run, TaskConfiguration group, Task<ExecutionSummary> task)
    {
        StopNotifyTimer();

        if (!string.IsNullOrWhiteSpace(run.PostScriptPath))
        {
            try
            {
                RunScriptAsync(run.PostScriptPath, "后置脚本").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _workspace.Log.Error("Runner", $"后置脚本执行异常：{ex.Message}", "queue");
            }
        }

        ReleaseSleepGuard();

        ExecutionOutcome outcome;
        int failed;
        try
        {
            ExecutionSummary summary = task.Result;
            outcome = summary.Outcome;
            failed = summary.Failed;
        }
        catch
        {
            return;   // 异常已由 LogSummary 记过，不触发动作
        }

        // P6.3：按结果发通知（应用内 Toast + 外部 Webhook），失败只记日志不阻断
        NotifyFinish(group, outcome, failed);

        // 只有「正常跑完」才执行动作：失败 / 停止 / 暂停都不算
        if (outcome == ExecutionOutcome.Completed && !string.Equals(run.AfterCompletion, "none", StringComparison.Ordinal))
        {
            ApplyAfterCompletion(run.AfterCompletion);
        }
    }

    // ---- P6.3 运行期通知（卡住超时 / 提醒间隔 / 完成失败） ----

    private void StartNotifyTimer()
    {
        if (_notifier is null)
        {
            return;
        }

        _notifyTimer ??= new Timer(_ => NotifyTick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    private void StopNotifyTimer()
    {
        _notifyTimer?.Dispose();
        _notifyTimer = null;
    }

    private void NotifyTick()
    {
        if (_notifier is null)
        {
            return;
        }

        // 只在真正运行期间判断（暂停/停止/空闲都不算）
        if (State != ExecutionState.Running)
        {
            return;
        }

        NotificationSettings notify = _workspace.Configurations.Settings.Notification;
        DateTime now = DateTime.UtcNow;

        // 卡住超时：超过设定时长没有任何任务状态变化
        if (notify.StuckTimeoutSeconds > 0 && !_stuckFired)
        {
            double idle = (now - _lastActivityUtc).TotalSeconds;
            if (idle >= notify.StuckTimeoutSeconds)
            {
                _stuckFired = true;
                FireNotify(NotificationTrigger.Stuck, "任务可能卡住了", $"已 {idle:F0} 秒无任何任务进展，请检查游戏是否失去响应。");
            }
        }

        // 提醒间隔：每 N 分钟提示一次仍在运行（仅应用内 Toast，不发外部）
        if (notify.ReminderIntervalMinutes > 0)
        {
            double elapsedMin = (now - _runStartUtc).TotalMinutes;
            if (elapsedMin >= (double)(_reminderCount + 1) * notify.ReminderIntervalMinutes)
            {
                _reminderCount++;
                int minutes = (int)elapsedMin;
                FireNotify(NotificationTrigger.Test, "队列仍在运行", $"已连续运行约 {minutes} 分钟。", sendExternal: false);
            }
        }
    }

    private void NotifyFinish(TaskConfiguration group, ExecutionOutcome outcome, int failed)
    {
        if (_notifier is null)
        {
            return;
        }

        string name = group.Name ?? "未命名配置组";
        if (outcome == ExecutionOutcome.Completed)
        {
            FireNotify(NotificationTrigger.Completed, "队列完成", $"配置组「{name}」已正常跑完。");
        }
        else if (failed > 0 || outcome == ExecutionOutcome.Failed)
        {
            FireNotify(NotificationTrigger.Failed, "队列失败", $"配置组「{name}」有 {failed} 个任务失败（{outcome}）。");
        }
    }

    /// <summary>异步发通知但吞掉异常：收尾在引擎线程，通知失败绝不能反噬队列。</summary>
    private void FireNotify(NotificationTrigger trigger, string title, string message, bool sendExternal = true)
    {
        if (_notifier is null)
        {
            return;
        }

        _notifier.NotifyAsync(trigger, title, message, sendExternal).ContinueWith(
            t =>
            {
                if (t.Exception is { } ex)
                {
                    _workspace.Log.Error("Notify", $"通知异常：{ex.InnerException?.Message ?? ex.Message}", "notify");
                }
            },
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// 整轮正常跑完后执行「完成后动作」。停止 / 暂停是用户主动打断，不触发 ——
    /// 否则按了停止反而退出程序。
    /// </summary>
    private void ApplyAfterCompletion(string kind)
    {
        _workspace.Log.Info("Runner", $"整轮完成：按设置执行完成后动作「{Describe(kind)}」", "queue");

        void Dispatch(Action action)
        {
            Application? app = Application.Current;
            if (app is null)
            {
                action();
                return;
            }

            app.Dispatcher.BeginInvoke(action);
        }

        if (string.Equals(kind, "exit", StringComparison.Ordinal))
        {
            Dispatch(() => Application.Current?.Shutdown());
            return;
        }

        if (string.Equals(kind, "shutdown", StringComparison.Ordinal))
        {
            try
            {
                var psi = new ProcessStartInfo("shutdown", "/s /t 60")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                };
                Process.Start(psi)?.Dispose();
                _workspace.Log.Warn("Runner", "已发出关机命令：60 秒倒计时，取消请执行 shutdown /a", "queue");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _workspace.Log.Error("Runner", $"关机命令执行失败：{ex.Message}", "queue");
            }
        }
    }

    private static string Describe(string kind) => kind switch
    {
        "exit" => "退出程序",
        "shutdown" => "关机",
        _ => "无动作",
    };

    /// <summary>
    /// 执行前后置脚本（.bat / .cmd / .exe）。失败与超时只记日志：
    /// 用户脚本不该成为任务本身的阻断点。
    /// </summary>
    private async Task RunScriptAsync(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!File.Exists(full))
        {
            _workspace.Log.Warn("Runner", $"{label}不存在，已跳过：{full}", "script");
            return;
        }

        _workspace.Log.Info("Runner", $"开始执行{label}：{full}", "script");
        (int exitCode, bool timedOut, string? error) = await Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{full}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(full) ?? string.Empty,
                };

                using Process? process = Process.Start(psi);
                if (process is null)
                {
                    return (0, false, "进程未能启动");
                }

                bool exited = process.WaitForExit((int)ScriptTimeout.TotalMilliseconds);
                return (exited ? process.ExitCode : 0, !exited, (string?)null);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
            {
                return (0, false, ex.Message);
            }
        }).ConfigureAwait(false);

        if (error is not null)
        {
            _workspace.Log.Error("Runner", $"{label}启动失败：{error}", "script");
        }
        else if (timedOut)
        {
            _workspace.Log.Warn("Runner", $"{label}超过 {ScriptTimeout.TotalSeconds:F0} 秒未结束，已放弃等待", "script");
        }
        else
        {
            _workspace.Log.Info("Runner", $"{label}结束（退出码 {exitCode}）", "script");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopNotifyTimer();
        ReleaseSleepGuard();
        DisposeExecutor();
    }
}
