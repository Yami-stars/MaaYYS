using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MaaYys.Engine.Config;

/// <summary>实例绑定的设备类型（与 <c>Devices.DeviceKind</c> 同值，独立声明避免配置层依赖设备层）。</summary>
public enum InstanceDeviceKind
{
    Win32 = 0,
    Adb = 1,
}

/// <summary>
/// 一个「实例」= 控制器绑定 + 资源集 + 任务集的组合（对标 MXU instances / MAA 多配置）。
/// 纯数据对象，不含运行态：可安全序列化、克隆、在多个标签页之间复用。
/// 运行态（引擎会话、帧采集）在 App 层的 <c>InstanceViewModel</c> 上。
/// </summary>
public sealed class InstanceDefinition
{
    /// <summary>实例名长度上限（标签条宽度约束）。</summary>
    public const int MaxNameLength = 24;

    /// <summary>Win32 可选截图方法（与 interface.json controller 的 screencap 参数一致）。</summary>
    public static readonly IReadOnlyList<string> Win32ScreencapMethods =
        ["FramePool", "PrintWindow", "GDI", "DXGI_DesktopDup", "DXGI_DesktopDup_Window"];

    /// <summary>Win32 可选输入方法（P1.4 实测：主界面需前台、二级面板可后台，故默认 SendMessage）。</summary>
    public static readonly IReadOnlyList<string> Win32InputMethods =
        ["SendMessage", "PostMessage", "PostMessageWithCursorPos", "Seize", "LegacyEvent"];

    /// <summary>稳定标识（实例改名后仍指向同一份配置，标签条 / 设备占用都靠它）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = NewId();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "实例";

    [JsonPropertyName("device_kind")]
    public InstanceDeviceKind DeviceKind { get; set; } = InstanceDeviceKind.Win32;

    [JsonPropertyName("win32_title_regex")]
    public string Win32TitleRegex { get; set; } = "阴阳师";

    [JsonPropertyName("win32_class_regex")]
    public string Win32ClassRegex { get; set; } = string.Empty;

    /// <summary>上次连接成功的窗口句柄（仅作首选提示；窗口重开后自动按正则重扫）。</summary>
    [JsonPropertyName("win32_hwnd")]
    public long Win32WindowHandle { get; set; }

    [JsonPropertyName("adb_address")]
    public string AdbAddress { get; set; } = string.Empty;

    /// <summary>ADB 配置 JSON（MaaAdbControllerCreate 的 config 参数原样透传）。</summary>
    [JsonPropertyName("adb_config")]
    public string AdbConfig { get; set; } = "{}";

    [JsonPropertyName("screencap_method")]
    public string ScreencapMethod { get; set; } = "FramePool";

    [JsonPropertyName("input_method")]
    public string InputMethod { get; set; } = "SendMessage";

    [JsonPropertyName("preview_fps")]
    public int PreviewFps { get; set; } = 5;

    /// <summary>资源集名（对应 interface.json <c>resource[].name</c>；P2.5 起决定加载哪些 bundle）。</summary>
    [JsonPropertyName("resource_name")]
    public string ResourceName { get; set; } = "base";

    /// <summary>勾选的任务入口名（pipeline entry，投递 MaaTaskerPostTask 用）。</summary>
    [JsonPropertyName("task_entries")]
    public List<string> TaskEntries { get; set; } = [];

    /// <summary>
    /// 绑定的配置组名（P2.6，对应 <c>maayys.json</c> 的 configurations 键）。
    /// <para>
    /// 空串表示「跟随全局 Current 指针」——单开时不用关心配置组，多开时可给每个实例各指定一套。
    /// P2.4 的旧清单没有这个字段，加载时由 <see cref="ConfigurationStore"/> 迁移为同名配置组。
    /// </para>
    /// </summary>
    [JsonPropertyName("configuration_name")]
    public string ConfigurationName { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string Note { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>设备目标摘要（标签条 / 列表第二行显示）。</summary>
    [JsonIgnore]
    public string DeviceSummary => DeviceKind == InstanceDeviceKind.Win32
        ? string.IsNullOrWhiteSpace(Win32ClassRegex)
            ? $"桌面端 · {Win32TitleRegex}"
            : $"桌面端 · {Win32TitleRegex} / {Win32ClassRegex}"
        : $"模拟器 · {(string.IsNullOrWhiteSpace(AdbAddress) ? "未指定地址" : AdbAddress)}";

    /// <summary>
    /// 配置层「同一目标」签名：仅用于新建时的重复提示与占用冲突预警。
    /// 真正连接时的设备占用以实际句柄 / ADB 地址为准（见 <see cref="DeviceLease"/>）。
    /// </summary>
    [JsonIgnore]
    public string TargetSignature => DeviceKind == InstanceDeviceKind.Win32
        ? $"win32|{Win32TitleRegex.Trim()}|{Win32ClassRegex.Trim()}"
        : $"adb|{AdbAddress.Trim()}";

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>深拷贝（新 Id + 可改名），用于「复制实例」。</summary>
    public InstanceDefinition Clone(string? newName = null)
    {
        var copy = (InstanceDefinition)MemberwiseClone();
        copy.Id = NewId();
        copy.Name = newName ?? $"{Name} 副本";
        copy.TaskEntries = [.. TaskEntries];
        copy.CreatedAt = DateTimeOffset.Now;
        copy.UpdatedAt = copy.CreatedAt;
        return copy;
    }

    /// <summary>把可编辑字段从另一份定义搬过来（保留本实例 Id / 创建时间）。</summary>
    public void CopyEditableFrom(InstanceDefinition other)
    {
        Name = other.Name;
        DeviceKind = other.DeviceKind;
        Win32TitleRegex = other.Win32TitleRegex;
        Win32ClassRegex = other.Win32ClassRegex;
        AdbAddress = other.AdbAddress;
        AdbConfig = other.AdbConfig;
        ScreencapMethod = other.ScreencapMethod;
        InputMethod = other.InputMethod;
        PreviewFps = other.PreviewFps;
        ResourceName = other.ResourceName;
        TaskEntries = [.. other.TaskEntries];
        ConfigurationName = other.ConfigurationName;
        Note = other.Note;
        UpdatedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// 校验配置合法性。返回中文错误列表，空表示通过。
    /// 校验项刻意保守（宁可拦住明显错误，不阻断高级用法）。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [];

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("实例名不能为空");
        }
        else
        {
            if (Name.Length > MaxNameLength)
            {
                errors.Add($"实例名不能超过 {MaxNameLength} 个字符（当前 {Name.Length}）");
            }

            if (Name.Any(char.IsControl))
            {
                errors.Add("实例名不能包含控制字符");
            }
        }

        if (DeviceKind == InstanceDeviceKind.Win32)
        {
            if (string.IsNullOrWhiteSpace(Win32TitleRegex))
            {
                errors.Add("窗口标题正则不能为空（桌面端靠它定位游戏窗口）");
            }
            else if (!IsValidRegex(Win32TitleRegex))
            {
                errors.Add($"窗口标题正则非法：{Win32TitleRegex}");
            }

            if (!string.IsNullOrWhiteSpace(Win32ClassRegex) && !IsValidRegex(Win32ClassRegex))
            {
                errors.Add($"窗口类名正则非法：{Win32ClassRegex}");
            }

            if (!Win32ScreencapMethods.Contains(ScreencapMethod))
            {
                errors.Add($"截图方法非法：{ScreencapMethod}（可选 {string.Join("/", Win32ScreencapMethods)}）");
            }

            if (!Win32InputMethods.Contains(InputMethod))
            {
                errors.Add($"输入方法非法：{InputMethod}（可选 {string.Join("/", Win32InputMethods)}）");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(AdbAddress))
            {
                errors.Add("ADB 设备地址不能为空（如 127.0.0.1:16384）");
            }
            else if (!AdbAddress.Contains(':'))
            {
                errors.Add($"ADB 设备地址格式应为 host:port，当前为 {AdbAddress}");
            }

            if (!IsValidJson(AdbConfig))
            {
                errors.Add("ADB 配置不是合法 JSON");
            }
        }

        if (PreviewFps is < 1 or > 60)
        {
            errors.Add($"预览帧率应在 1–60 之间，当前 {PreviewFps}");
        }

        if (string.IsNullOrWhiteSpace(ResourceName))
        {
            errors.Add("资源集不能为空");
        }

        return errors;
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
