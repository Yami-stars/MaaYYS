using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaaYss.Engine.Config;

/// <summary>
/// PI V2 控制器配置（interface.json 的 controller[] 元素）。
/// 字段定义见 docs/reference/interface.schema.json 的 controllerItem。
/// </summary>
public sealed class ControllerConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>控制器类型：Adb / Win32 / MacOS / PlayCover / Gamepad / Linux。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("display_short_side")]
    public int? DisplayShortSide { get; set; }

    [JsonPropertyName("display_long_side")]
    public int? DisplayLongSide { get; set; }

    [JsonPropertyName("display_expand")]
    public int[]? DisplayExpand { get; set; }

    [JsonPropertyName("display_raw")]
    public bool? DisplayRaw { get; set; }

    [JsonPropertyName("permission_required")]
    public bool? PermissionRequired { get; set; }

    /// <summary>附加资源路径，在 resource.path 加载完成后额外加载（v2.3.0）。</summary>
    [JsonPropertyName("attach_resource_path")]
    public List<string>? AttachResourcePath { get; set; }

    /// <summary>控制器级选项，取值参与所有使用该控制器的任务的 pipeline override。</summary>
    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }

    [JsonPropertyName("adb")]
    public JsonElement? Adb { get; set; }

    [JsonPropertyName("win32")]
    public Win32ControllerConfig? Win32 { get; set; }

    [JsonPropertyName("playcover")]
    public PlayCoverControllerConfig? PlayCover { get; set; }

    [JsonPropertyName("macos")]
    public MacOsControllerConfig? MacOs { get; set; }

    [JsonPropertyName("gamepad")]
    public JsonElement? Gamepad { get; set; }

    [JsonPropertyName("linux")]
    public JsonElement? Linux { get; set; }

    /// <summary>Schema 枚举的合法控制器类型。</summary>
    public static readonly string[] KnownTypes = ["Adb", "Win32", "MacOS", "PlayCover", "Gamepad", "Linux"];

    public bool IsKnownType => KnownTypes.Contains(Type, StringComparer.Ordinal);
}

/// <summary>Win32 控制器配置：窗口定位正则与键鼠/截图方式。</summary>
public sealed class Win32ControllerConfig
{
    [JsonPropertyName("class_regex")]
    public string? ClassRegex { get; set; }

    [JsonPropertyName("window_regex")]
    public string? WindowRegex { get; set; }

    [JsonPropertyName("mouse")]
    public string? Mouse { get; set; }

    [JsonPropertyName("keyboard")]
    public string? Keyboard { get; set; }

    [JsonPropertyName("screencap")]
    public string? Screencap { get; set; }
}

/// <summary>PlayCover 控制器配置（仅 macOS）。</summary>
public sealed class PlayCoverControllerConfig
{
    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }
}

/// <summary>MacOS 控制器配置。</summary>
public sealed class MacOsControllerConfig
{
    [JsonPropertyName("title_regex")]
    public string? TitleRegex { get; set; }

    [JsonPropertyName("input")]
    public string? Input { get; set; }

    [JsonPropertyName("screencap")]
    public string? Screencap { get; set; }
}

/// <summary>
/// PI V2 资源配置（interface.json 的 resource[] 元素）。
/// path 为有序加载列表：后加载的覆盖先加载的（Schema 原文语义）。
/// </summary>
public sealed class ResourceConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("path")]
    public List<string> Path { get; set; } = [];

    /// <summary>支持的控制器 name 列表；空表示全部支持。</summary>
    [JsonPropertyName("controller")]
    public List<string>? Controller { get; set; }

    /// <summary>资源包级选项，取值参与所有任务的 pipeline override。</summary>
    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }

    /// <summary>资源 hash 校验值（加载 resource.path 后、attach_resource_path 前校验）。</summary>
    [JsonPropertyName("hash")]
    public string? Hash { get; set; }

    /// <summary>显示名称，未设置 label 时回退 name。</summary>
    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : Label!;
}

/// <summary>任务分组（interface.json 的 group[] 元素，v2.4.0）。</summary>
public sealed class GroupConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("default_expand")]
    public bool? DefaultExpand { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : Label!;
}

/// <summary>
/// 任务配置（interface.json 的 task[] 元素或 import 文件中的 task[] 元素）。
/// </summary>
public sealed class TaskConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    /// <summary>pipeline 中的入口名，投递 MaaTaskerPostTask 时使用。</summary>
    [JsonPropertyName("entry")]
    public string Entry { get; set; } = string.Empty;

    [JsonPropertyName("default_check")]
    public bool? DefaultCheck { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("doc")]
    public JsonElement? Doc { get; set; }

    [JsonPropertyName("desc")]
    public JsonElement? Desc { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>所属分组 name 列表，引用顶层 group[].name。</summary>
    [JsonPropertyName("group")]
    public List<string>? Group { get; set; }

    /// <summary>支持的资源包 name 列表；空表示全部可用。</summary>
    [JsonPropertyName("resource")]
    public List<string>? Resource { get; set; }

    /// <summary>支持的控制器 name 列表；空表示全部可用。</summary>
    [JsonPropertyName("controller")]
    public List<string>? Controller { get; set; }

    [JsonPropertyName("pipeline_override")]
    public JsonElement? PipelineOverride { get; set; }

    /// <summary>任务引用的配置项键名列表，必须在合并后的 option 池中存在。</summary>
    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }

    /// <summary>来源 import 文件（顶层任务为 null），用于溯源。</summary>
    [JsonIgnore]
    public string? SourceFile { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : Label!;
}

/// <summary>
/// 配置项定义（option 字典的值）。多态：select / switch / input / checkbox / hotkey，
/// 通过 type 字段区分（见 Schema 的 optionDefinition oneOf）。
/// </summary>
public sealed class OptionConfig
{
    /// <summary>select / switch / input / checkbox / hotkey。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("controller")]
    public List<string>? Controller { get; set; }

    [JsonPropertyName("resource")]
    public List<string>? Resource { get; set; }

    /// <summary>select/switch 的 cases 或 checkbox 的 cases。</summary>
    [JsonPropertyName("cases")]
    public List<CaseConfig>? Cases { get; set; }

    /// <summary>
    /// 默认选项。select/switch 为字符串，checkbox 为字符串数组（Schema 允许多态）。
    /// 原始 JsonElement 保留，经 <see cref="DefaultCaseString"/> / <see cref="DefaultCaseList"/> 读取。
    /// </summary>
    [JsonPropertyName("default_case")]
    public JsonElement? DefaultCase { get; set; }

    /// <summary>input 类型的输入字段列表。</summary>
    [JsonPropertyName("inputs")]
    public List<InputConfig>? Inputs { get; set; }

    /// <summary>hotkey 类型的快捷键字段列表。</summary>
    [JsonPropertyName("hotkeys")]
    public List<HotkeyConfig>? Hotkeys { get; set; }

    /// <summary>选项激活时生效的 pipeline 覆盖（可含 {名称} 占位符）。</summary>
    [JsonPropertyName("pipeline_override")]
    public JsonElement? PipelineOverride { get; set; }

    [JsonPropertyName("min_count")]
    public int? MinCount { get; set; }

    [JsonPropertyName("max_count")]
    public int? MaxCount { get; set; }

    /// <summary>option 键名（来自字典 key），非 JSON 字段。</summary>
    [JsonIgnore]
    public string Key { get; set; } = string.Empty;

    public string? DefaultCaseString =>
        DefaultCase is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    public List<string>? DefaultCaseList =>
        DefaultCase is { ValueKind: JsonValueKind.Array } e
            ? e.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : null;

    public string DisplayName => string.IsNullOrEmpty(Label) ? Key : Label!;

    /// <summary>Schema 枚举的合法 option 类型。</summary>
    public static readonly string[] KnownTypes = ["select", "switch", "input", "checkbox", "hotkey"];

    public bool IsKnownType => KnownTypes.Contains(Type, StringComparer.Ordinal);
}

/// <summary>select/switch/checkbox 的可选项（Schema 的 caseItem）。</summary>
public sealed class CaseConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>仅当选中该选项时才显示的子配置项键名。</summary>
    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }

    [JsonPropertyName("pipeline_override")]
    public JsonElement? PipelineOverride { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Label) ? Name : Label!;
}

/// <summary>input 类型的输入字段（Schema 的 inputItem）。</summary>
public sealed class InputConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default")]
    public string? Default { get; set; }

    /// <summary>pipeline_override 中的数据类型：string / int / bool。</summary>
    [JsonPropertyName("pipeline_type")]
    public string? PipelineType { get; set; }

    [JsonPropertyName("verify")]
    public string? Verify { get; set; }

    [JsonPropertyName("pattern_msg")]
    public string? PatternMsg { get; set; }

    [JsonPropertyName("password")]
    public bool? Password { get; set; }
}

/// <summary>hotkey 类型的快捷键字段（Schema 的 hotkeyItem，v2.8.0）。</summary>
public sealed class HotkeyConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("default")]
    public string? Default { get; set; }
}

/// <summary>Agent 子进程配置（Schema 的 agentConfig，v2.5.0 起注入 PI_* 环境变量）。</summary>
public sealed class AgentConfig
{
    [JsonPropertyName("child_exec")]
    public string ChildExec { get; set; } = string.Empty;

    [JsonPropertyName("child_args")]
    public List<string>? ChildArgs { get; set; }

    [JsonPropertyName("identifier")]
    public string? Identifier { get; set; }
}

/// <summary>预任务配置（Schema 的 pretaskConfig，v2.7.0）。</summary>
public sealed class PretaskConfig
{
    [JsonPropertyName("exec")]
    public string Exec { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string>? Args { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("resource")]
    public List<string>? Resource { get; set; }

    [JsonPropertyName("controller")]
    public List<string>? Controller { get; set; }

    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }
}

/// <summary>预设配置（Schema 的 preset，v2.3.0）。</summary>
public sealed class PresetConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("task")]
    public List<JsonElement>? Task { get; set; }

    [JsonPropertyName("pretask")]
    public List<JsonElement>? Pretask { get; set; }
}

/// <summary>任务设置页分区（Schema 的 settingSection，v2.8.0）。</summary>
public sealed class SettingSectionConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    [JsonPropertyName("option")]
    public List<string>? Option { get; set; }

    [JsonPropertyName("default_expand")]
    public bool? DefaultExpand { get; set; }
}

/// <summary>Sentry 遥测配置（v2.9.0）。</summary>
public sealed class SentryConfig
{
    [JsonPropertyName("dsn")]
    public string? Dsn { get; set; }

    [JsonPropertyName("tracing")]
    public bool? Tracing { get; set; }

    [JsonPropertyName("traces_sample_rate")]
    public double? TracesSampleRate { get; set; }

    [JsonPropertyName("failure_attachments_sample_rate")]
    public double? FailureAttachmentsSampleRate { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

/// <summary>遥测配置容器（v2.9.0）。</summary>
public sealed class TelemetryConfig
{
    [JsonPropertyName("sentry")]
    public SentryConfig? Sentry { get; set; }
}
