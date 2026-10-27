using System.Text.Json.Serialization;
using MaaYys.Engine.Logging;

namespace MaaYys.Engine.Config;

/// <summary>
/// 应用级设置（P6.2）：连接 / 启动 / 运行 / 日志落盘四组。
/// <para>
/// 与 <see cref="TaskConfiguration"/> 的区别：配置组是「任务怎么跑」，这里是「程序怎么跑」——
/// 跨配置组共享，持久化在 <c>config/maayys.json</c> 顶层 <c>settings</c> 段。
/// </para>
/// <para>
/// 全部字段带安全默认值：配置缺失、被手改成非法值时 <see cref="Sanitized"/> 会夹回合法区间，
/// 界面绝不会因为一个坏配置启动失败。
/// </para>
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("connection")]
    public ConnectionSettings Connection { get; set; } = new();

    [JsonPropertyName("startup")]
    public StartupSettings Startup { get; set; } = new();

    [JsonPropertyName("run")]
    public RunSettings Run { get; set; } = new();

    [JsonPropertyName("file_log")]
    public FileLogSettings FileLog { get; set; } = new();

    [JsonPropertyName("notification")]
    public NotificationSettings Notification { get; set; } = new();

    public static AppSettings Default { get; } = new();

    /// <summary>夹取到合法区间（就地把非法值改回来，返回自身便于链式调用）。</summary>
    public AppSettings Sanitized()
    {
        Connection ??= new ConnectionSettings();
        Startup ??= new StartupSettings();
        Run ??= new RunSettings();
        FileLog ??= new FileLogSettings();
        Notification ??= new NotificationSettings();

        Connection.Sanitize();
        Startup.Sanitize();
        Run.Sanitize();
        FileLog.Sanitize();
        Notification.Sanitize();
        return this;
    }

    /// <summary>深拷贝（界面编辑草稿 / 导出用）。</summary>
    public AppSettings Clone() => new AppSettings().CopyFrom(this);

    /// <summary>
    /// 原地覆盖全部字段（**不换对象**）：存储持有同一实例，
    /// 各服务（执行器、定时器、日志 sink）持有的是组内子对象引用，
    /// 因此 CopyFrom 逐组原地赋值、不替换子对象。
    /// </summary>
    public AppSettings CopyFrom(AppSettings? other)
    {
        if (other is null || ReferenceEquals(other, this))
        {
            return this;
        }

        Connection ??= new ConnectionSettings();
        Startup ??= new StartupSettings();
        Run ??= new RunSettings();
        FileLog ??= new FileLogSettings();
        Notification ??= new NotificationSettings();

        Connection.CopyFrom(other.Connection);
        Startup.CopyFrom(other.Startup);
        Run.CopyFrom(other.Run);
        FileLog.CopyFrom(other.FileLog);
        Notification.CopyFrom(other.Notification);
        return this;
    }
}

/// <summary>
/// 连接设置：Win32 控制器的方式选择与预览帧率。
/// <para>
/// 这些是**全局默认值**——新建实例时按它初始化；已有实例仍以「多实例」页的
/// 单独配置为准（多开不同游戏时各不相同）。设置页提供「应用到全部实例」做批量下发。
/// </para>
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>截图方式（默认 FramePool：P1.4 实测最快且对后台窗口有效）。</summary>
    [JsonPropertyName("screencap_method")]
    public string ScreencapMethod { get; set; } = "FramePool";

    /// <summary>鼠标输入方式（默认 SendMessage：后台点击）。</summary>
    [JsonPropertyName("input_method")]
    public string InputMethod { get; set; } = "SendMessage";

    /// <summary>键盘输入方式（默认 SendMessage）。</summary>
    [JsonPropertyName("keyboard_method")]
    public string KeyboardMethod { get; set; } = "SendMessage";

    /// <summary>新建实例时的窗口标题正则默认值。</summary>
    [JsonPropertyName("win32_title_regex")]
    public string Win32TitleRegex { get; set; } = "阴阳师";

    /// <summary>预览帧率（0 = 关闭预览泵，省 CPU）。</summary>
    [JsonPropertyName("preview_fps")]
    public int PreviewFps { get; set; } = 5;

    public void Sanitize()
    {
        ScreencapMethod = Pick(ScreencapMethod, InstanceDefinition.Win32ScreencapMethods, "FramePool");
        InputMethod = Pick(InputMethod, InstanceDefinition.Win32InputMethods, "SendMessage");
        KeyboardMethod = Pick(KeyboardMethod, InstanceDefinition.Win32InputMethods, "SendMessage");
        Win32TitleRegex = string.IsNullOrWhiteSpace(Win32TitleRegex) ? "阴阳师" : Win32TitleRegex.Trim();
        PreviewFps = Math.Clamp(PreviewFps, 0, 30);
    }

    public void CopyFrom(ConnectionSettings? other)
    {
        if (other is null)
        {
            return;
        }

        ScreencapMethod = other.ScreencapMethod;
        InputMethod = other.InputMethod;
        KeyboardMethod = other.KeyboardMethod;
        Win32TitleRegex = other.Win32TitleRegex;
        PreviewFps = other.PreviewFps;
    }

    private static string Pick(string? value, IReadOnlyList<string> allowed, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        foreach (string candidate in allowed)
        {
            if (string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return fallback;
    }
}

/// <summary>
/// 启动设置：前置程序（游戏本体）与开机自启（对标 MAA「启动设置」）。
/// </summary>
public sealed class StartupSettings
{
    /// <summary>前置程序路径（游戏 exe / 快捷方式；空 = 不自动启动）。</summary>
    [JsonPropertyName("pre_program_path")]
    public string PreProgramPath { get; set; } = string.Empty;

    /// <summary>前置程序命令行参数。</summary>
    [JsonPropertyName("pre_program_arguments")]
    public string PreProgramArguments { get; set; } = string.Empty;

    /// <summary>启动后等待秒数（等游戏窗口出现再连接）。</summary>
    [JsonPropertyName("pre_program_wait_seconds")]
    public int PreProgramWaitSeconds { get; set; } = 15;

    /// <summary>
    /// 连接时若没找到目标窗口，是否自动拉起前置程序再重试一次。
    /// 关掉时前置程序只在手动点「启动游戏」时执行。
    /// </summary>
    [JsonPropertyName("auto_launch_on_connect")]
    public bool AutoLaunchOnConnect { get; set; } = true;

    /// <summary>随 Windows 启动（写 HKCU 的 Run 键，不需要管理员权限）。</summary>
    [JsonPropertyName("auto_start_with_windows")]
    public bool AutoStartWithWindows { get; set; }

    /// <summary>开机自启时最小化启动（不弹主窗口，只留托盘）。</summary>
    [JsonPropertyName("silent_start")]
    public bool SilentStart { get; set; }

    public void Sanitize()
    {
        PreProgramPath = (PreProgramPath ?? string.Empty).Trim();
        PreProgramArguments = (PreProgramArguments ?? string.Empty).Trim();
        PreProgramWaitSeconds = Math.Clamp(PreProgramWaitSeconds, 0, 600);
    }

    public void CopyFrom(StartupSettings? other)
    {
        if (other is null)
        {
            return;
        }

        PreProgramPath = other.PreProgramPath;
        PreProgramArguments = other.PreProgramArguments;
        PreProgramWaitSeconds = other.PreProgramWaitSeconds;
        AutoLaunchOnConnect = other.AutoLaunchOnConnect;
        AutoStartWithWindows = other.AutoStartWithWindows;
        SilentStart = other.SilentStart;
    }
}

/// <summary>
/// 运行设置：P5.3/P5.4 里写死的策略参数 + 防休眠 / 前后置脚本 / 完成后动作。
/// </summary>
public sealed class RunSettings
{
    /// <summary>完成后动作的取值（与界面下拉一一对应）。</summary>
    public static readonly string[] AfterCompletionKinds = ["none", "exit", "shutdown"];

    /// <summary>停滞超时（秒，0 = 不检测）。超过该时长没有任何框架事件即判卡死。</summary>
    [JsonPropertyName("stall_timeout_seconds")]
    public int StallTimeoutSeconds { get; set; } = 60;

    /// <summary>失败后的重试次数（不含首次，0 = 不重试）。</summary>
    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; }

    /// <summary>两次尝试之间的等待毫秒数。</summary>
    [JsonPropertyName("retry_delay_ms")]
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>任务最终失败后是否停止整个队列。</summary>
    [JsonPropertyName("stop_on_failure")]
    public bool StopOnFailure { get; set; } = true;

    /// <summary>单个任务的时长上限（秒，0 = 不限）。</summary>
    [JsonPropertyName("max_duration_seconds")]
    public int MaxDurationSeconds { get; set; }

    /// <summary>定时器轮询间隔（秒）：越短越准时，越长越省电。</summary>
    [JsonPropertyName("timer_poll_seconds")]
    public int TimerPollSeconds { get; set; } = 20;

    /// <summary>执行期间阻止系统休眠（长跑必备，否则挂机中途睡过去）。</summary>
    [JsonPropertyName("prevent_sleep")]
    public bool PreventSleep { get; set; } = true;

    /// <summary>完成后动作：none / exit / shutdown。</summary>
    [JsonPropertyName("after_completion")]
    public string AfterCompletion { get; set; } = "none";

    /// <summary>前置脚本（队列开始前执行，.bat/.cmd/.exe；空 = 不执行）。</summary>
    [JsonPropertyName("pre_script_path")]
    public string PreScriptPath { get; set; } = string.Empty;

    /// <summary>后置脚本（队列结束后执行，无论成功失败）。</summary>
    [JsonPropertyName("post_script_path")]
    public string PostScriptPath { get; set; } = string.Empty;

    public void Sanitize()
    {
        StallTimeoutSeconds = Math.Clamp(StallTimeoutSeconds, 0, 3600);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        RetryDelayMs = Math.Clamp(RetryDelayMs, 0, 60_000);
        MaxDurationSeconds = Math.Clamp(MaxDurationSeconds, 0, 24 * 3600);
        TimerPollSeconds = Math.Clamp(TimerPollSeconds, 5, 600);
        AfterCompletion = Normalize(AfterCompletion);
        PreScriptPath = (PreScriptPath ?? string.Empty).Trim();
        PostScriptPath = (PostScriptPath ?? string.Empty).Trim();
    }

    public void CopyFrom(RunSettings? other)
    {
        if (other is null)
        {
            return;
        }

        StallTimeoutSeconds = other.StallTimeoutSeconds;
        MaxRetries = other.MaxRetries;
        RetryDelayMs = other.RetryDelayMs;
        StopOnFailure = other.StopOnFailure;
        MaxDurationSeconds = other.MaxDurationSeconds;
        TimerPollSeconds = other.TimerPollSeconds;
        PreventSleep = other.PreventSleep;
        AfterCompletion = other.AfterCompletion;
        PreScriptPath = other.PreScriptPath;
        PostScriptPath = other.PostScriptPath;
    }

    /// <summary>把任意输入规整为合法取值（未知值回落 none，不抛异常）。</summary>
    public static string Normalize(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (string kind in AfterCompletionKinds)
            {
                if (string.Equals(kind, value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return kind;
                }
            }
        }

        return "none";
    }
}
