using System.Diagnostics;
using System.IO;
using MaaYys.Engine.Config;
using MaaYys.Engine.Devices;

namespace MaaYys.App.Services;

/// <summary>前置程序启动结果。</summary>
public enum LaunchOutcome
{
    /// <summary>设置里没配路径，或关闭了「连接时自动启动」。</summary>
    Disabled = 0,

    /// <summary>配了路径但文件不存在。</summary>
    NotFound,

    /// <summary>进程启动失败（权限 / 路径被占用等）。</summary>
    Failed,

    /// <summary>进程已启动，但等到超时也没看到目标窗口。</summary>
    WindowMissing,

    /// <summary>目标窗口已出现，可以连接了。</summary>
    WindowReady,
}

/// <summary>一次启动尝试的完整结果（供界面与日志展示）。</summary>
public sealed record LaunchResult(LaunchOutcome Outcome, string Message, int ProcessId = 0);

/// <summary>
/// 前置程序启动（P6.2 启动设置）：连接时若没找到目标窗口，先把游戏拉起来再等窗口出现。
/// <para>
/// 为什么要做：挂机场景最常见的失败就是「程序启动了但游戏没开」——MAA 的「启动设置」
/// 就是为这个准备的；这里等的是**窗口出现**而不是死等固定秒数，
/// 游戏启动快就早连上，启动慢也不会因为固定等待不够而失败。
/// </para>
/// </summary>
public sealed class LaunchService
{
    private readonly ConfigurationStore _configurations;

    public LaunchService(ConfigurationStore configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations);
        _configurations = configurations;
    }

    /// <summary>设置里配了路径（不校验文件是否存在，界面用它决定按钮是否可点）。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configurations.Settings.Startup.PreProgramPath);

    /// <summary>
    /// 启动前置程序并等待目标窗口出现。
    /// </summary>
    /// <param name="definition">按它的窗口正则找窗口。</param>
    /// <param name="report">进度回调（UI 状态栏 / 日志），可为 null。</param>
    /// <param name="ignoreToggle">忽略「连接时自动启动」开关（设置页的「立即启动」按钮用）。</param>
    /// <param name="cancellationToken">用户取消（停止连接）。</param>
    public async Task<LaunchResult> LaunchAndWaitAsync(
        InstanceDefinition definition,
        Action<string>? report = null,
        bool ignoreToggle = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        StartupSettings startup = _configurations.Settings.Startup;
        if (!startup.AutoLaunchOnConnect && !ignoreToggle)
        {
            return new LaunchResult(LaunchOutcome.Disabled, "未开启「连接时自动启动前置程序」");
        }

        if (string.IsNullOrWhiteSpace(startup.PreProgramPath))
        {
            return new LaunchResult(LaunchOutcome.Disabled, "未配置前置程序路径");
        }

        string path = Environment.ExpandEnvironmentVariables(startup.PreProgramPath);
        if (!File.Exists(path))
        {
            return new LaunchResult(LaunchOutcome.NotFound, $"前置程序不存在：{path}");
        }

        report?.Invoke($"正在启动前置程序：{Path.GetFileName(path)}");
        int processId;
        try
        {
            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(startup.PreProgramArguments))
            {
                startInfo.Arguments = startup.PreProgramArguments;
            }

            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start 返回 null");
            processId = process.Id;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new LaunchResult(LaunchOutcome.Failed, $"启动前置程序失败：{ex.Message}");
        }

        // 等窗口出现（上限 PreProgramWaitSeconds；每 500ms 扫一次）
        int waitSeconds = Math.Max(0, startup.PreProgramWaitSeconds);
        report?.Invoke($"已启动（PID {processId}），最多等待 {waitSeconds} 秒直到窗口出现…");

        string? titleRegex = string.IsNullOrWhiteSpace(definition.Win32TitleRegex) ? null : definition.Win32TitleRegex;
        string? classRegex = string.IsNullOrWhiteSpace(definition.Win32ClassRegex) ? null : definition.Win32ClassRegex;

        DateTimeOffset deadline = DateTimeOffset.Now.AddSeconds(waitSeconds);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (definition.DeviceKind == InstanceDeviceKind.Win32 && WindowReady(titleRegex, classRegex))
            {
                return new LaunchResult(LaunchOutcome.WindowReady, "窗口已出现", processId);
            }

            if (DateTimeOffset.Now >= deadline)
            {
                return new LaunchResult(
                    LaunchOutcome.WindowMissing,
                    $"已启动前置程序，但 {waitSeconds} 秒内未等到窗口（标题正则「{definition.Win32TitleRegex}」）",
                    processId);
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        }
    }

    private static bool WindowReady(string? titleRegex, string? classRegex)
    {
        try
        {
            return DeviceScanner.ScanWin32(titleRegex, classRegex).Count > 0;
        }
        catch
        {
            // Toolkit 未就绪时不要因为探测失败把连接流程打断
            return false;
        }
    }
}
