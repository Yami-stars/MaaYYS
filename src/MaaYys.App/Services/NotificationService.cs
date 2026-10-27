using System.Threading;
using MaaYys.App.ViewModels;
using MaaYys.Engine.Config;
using MaaYys.Engine.Logging;
using MaaYys.Engine.Notification;

namespace MaaYys.App.Services;

/// <summary>应用内 Toast 事件参数（标题 + 正文）。</summary>
public sealed class ToastEventArgs : EventArgs
{
    public ToastEventArgs(string title, string message)
    {
        Title = title ?? string.Empty;
        Message = message ?? string.Empty;
    }

    public string Title { get; }

    public string Message { get; }
}

/// <summary>
/// 通知聚合服务（P6.3 首批，应用层）：把「应用内 Toast」和「引擎外部通知」两条路并在一起。
/// <para>
/// 队列跑到关键节点（完成 / 失败 / 卡住 / 提醒）时调用 <see cref="NotifyAsync"/>：
/// 1. 先在 UI 线程弹一条应用内提示（不等网络）；
/// 2. 再按设置把外部 Webhook 异步发出去（失败只记日志，绝不阻断任务）。
/// </para>
/// <para>
/// Toast 通过 <see cref="ToastRequested"/> 事件暴露，由 MainWindow 订阅并展示横幅；
/// 因为队列回调在引擎线程上跑，这里用工作区捕获的 UI 同步上下文把事件抛回主线程。
/// </para>
/// </summary>
public sealed class NotificationService
{
    private readonly InstanceWorkspace _workspace;
    private readonly SynchronizationContext? _uiContext;
    private readonly Engine.Notification.NotificationService _engine = new();

    /// <summary>应用内提示请求（MainWindow 订阅，展示 4 秒横幅）。</summary>
    public event EventHandler<ToastEventArgs>? ToastRequested;

    public NotificationService(InstanceWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        // 工作区在 App 启动早期构造，此时 UI 同步上下文已就绪（WPF DispatcherSynchronizationContext）
        _uiContext = SynchronizationContext.Current;
    }

    /// <summary>本次通知的实际设置（便于设置页预览「外部通知是否就绪」）。</summary>
    public NotificationSettings Current => _workspace.Configurations.Settings.Notification;

    /// <summary>
    /// 触发一次通知：应用内 Toast 立即弹（UI 线程），外部 Webhook 按 <paramref name="sendExternal"/> 决定发不发。
    /// 方法本身不抛异常；外部发送失败由引擎层降级并记日志。
    /// </summary>
    /// <param name="sendExternal">是否在弹 Toast 之外再发外部 Webhook（提醒间隔只弹应用内，传 false）。</param>
    public async Task NotifyAsync(
        NotificationTrigger trigger,
        string title,
        string message,
        bool sendExternal = true,
        CancellationToken cancellationToken = default)
    {
        RaiseToast(title, message);

        if (!sendExternal)
        {
            return;
        }

        NotificationSettings settings = _workspace.Configurations.Settings.Notification;
        NotificationContext context = new(title, message, DateTimeOffset.Now, trigger);

        Engine.Notification.NotificationResult result = await _engine
            .SendAsync(settings, context, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.Failed)
        {
            _workspace.Log.Warn("Notify", $"外部通知发送失败（{trigger}）：{result.Error}", "notify");
        }
        else if (result.Sent)
        {
            _workspace.Log.Info("Notify", $"外部通知已发送（{trigger}）", "notify");
        }
    }

    /// <summary>只在 UI 线程抛 Toast 事件；同步上下文缺失时直接抛（设计器/测试场景）。</summary>
    private void RaiseToast(string title, string message)
    {
        void Invoke() => ToastRequested?.Invoke(this, new ToastEventArgs(title, message));

        if (_uiContext is null)
        {
            Invoke();
        }
        else
        {
            _uiContext.Post(_ => Invoke(), null);
        }
    }
}
