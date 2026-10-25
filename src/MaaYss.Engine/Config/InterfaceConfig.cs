using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaaYss.Engine.Config;

/// <summary>
/// PI V2 项目接口配置（interface.json）。
/// 加载时按官方语义合并 import 文件（task/preset 追加，option 同名先到先得），
/// 并执行与 MaaPiCli Parser 一致的硬校验。字段定义见 docs/reference/interface.schema.json。
/// </summary>
public sealed class InterfaceConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // ---- 顶层字段（与 Schema properties 一一对应）----

    [JsonPropertyName("interface_version")]
    public int InterfaceVersion { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("icon")]
    public string? Icon { get; set; }

    /// <summary>欢迎消息/公告，字符串或字符串数组（v2.10.0 起支持数组）。</summary>
    [JsonPropertyName("welcome")]
    public JsonElement? Welcome { get; set; }

    [JsonPropertyName("license")]
    public string? License { get; set; }

    [JsonPropertyName("contact")]
    public string? Contact { get; set; }

    [JsonPropertyName("github")]
    public string? Github { get; set; }

    [JsonPropertyName("languages")]
    public Dictionary<string, string>? Languages { get; set; }

    [JsonPropertyName("mirrorchyan_rid")]
    public string? MirrorchyanRid { get; set; }

    [JsonPropertyName("mirrorchyan_multiplatform")]
    public bool? MirrorchyanMultiplatform { get; set; }

    [JsonPropertyName("telemetry")]
    public TelemetryConfig? Telemetry { get; set; }

    [JsonPropertyName("controller")]
    public List<ControllerConfig> Controllers { get; set; } = [];

    [JsonPropertyName("resource")]
    public List<ResourceConfig> Resources { get; set; } = [];

    /// <summary>Agent 配置，单对象或对象数组（Schema oneOf），经 <see cref="Agents"/> 规整后使用。</summary>
    [JsonPropertyName("agent")]
    public JsonElement? Agent { get; set; }

    [JsonPropertyName("group")]
    public List<GroupConfig> Groups { get; set; } = [];

    [JsonPropertyName("task")]
    public List<TaskConfig> Tasks { get; set; } = [];

    [JsonPropertyName("option")]
    public Dictionary<string, OptionConfig>? Options { get; set; }

    [JsonPropertyName("global_option")]
    public List<string>? GlobalOption { get; set; }

    [JsonPropertyName("import")]
    public List<string>? Import { get; set; }

    [JsonPropertyName("preset")]
    public List<PresetConfig>? Presets { get; set; }

    /// <summary>预任务配置，单对象或对象数组（Schema oneOf），经 <see cref="Pretasks"/> 规整后使用。</summary>
    [JsonPropertyName("pretask")]
    public JsonElement? Pretask { get; set; }

    [JsonPropertyName("setting")]
    public List<SettingSectionConfig>? Setting { get; set; }

    // ---- 加载后填充的合并结果（非 JSON 字段）----

    /// <summary>interface.json 所在目录，import 与资源相对路径的解析基准。</summary>
    [JsonIgnore]
    public string BaseDirectory { get; private set; } = string.Empty;

    /// <summary>实际解析成功的 import 文件（相对路径，按数组顺序）。</summary>
    [JsonIgnore]
    public List<string> ImportedFiles { get; } = [];

    /// <summary>合并后的全量任务（顶层 task + 各 import 的 task，按顺序追加）。</summary>
    [JsonIgnore]
    public List<TaskConfig> MergedTasks { get; private set; } = [];

    /// <summary>
    /// 合并后的 option 池。语义对齐官方 MaaPiCli（std::unordered_map::insert 不覆盖已有键）：
    /// 顶层 option 优先，其次按 import 数组顺序先到先得。
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, OptionConfig> MergedOptions { get; private set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public List<PresetConfig> MergedPresets { get; private set; } = [];

    [JsonIgnore]
    public List<GroupConfig> MergedGroups { get; private set; } = [];

    [JsonIgnore]
    public List<PretaskConfig> MergedPretasks { get; private set; } = [];

    [JsonIgnore]
    public List<SettingSectionConfig> MergedSettings { get; private set; } = [];

    /// <summary>规整后的 Agent 列表（兼容单对象与数组两种写法）。</summary>
    [JsonIgnore]
    public List<AgentConfig> Agents { get; private set; } = [];

    /// <summary>显示名称，未设置 label/title 时回退 name。</summary>
    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrEmpty(Label) ? Label!
        : !string.IsNullOrEmpty(Title) ? Title!
        : Name;

    // ---- 加载与合并 ----

    /// <summary>
    /// 加载 interface.json 并按官方语义合并 import。
    /// </summary>
    /// <param name="interfaceJsonPath">interface.json 绝对路径。</param>
    /// <param name="errors">加载/合并/校验产生的错误列表（官方硬校验项）。</param>
    public static InterfaceConfig Load(string interfaceJsonPath, List<string> errors)
    {
        var full = Path.GetFullPath(interfaceJsonPath);
        var json = File.ReadAllText(full);
        var config = JsonSerializer.Deserialize<InterfaceConfig>(json, SerializerOptions)
                     ?? throw new InvalidDataException($"interface.json 反序列化为 null: {full}");
        config.BaseDirectory = Path.GetDirectoryName(full)!;

        // 官方硬校验：interface_version 必须为 2（MaaPiCli Parser::parse_interface）
        if (config.InterfaceVersion != 2)
        {
            errors.Add($"interface_version 必须为 2，实际为 {config.InterfaceVersion}");
        }

        // 官方硬校验：控制器类型必须合法
        foreach (var ctrl in config.Controllers)
        {
            if (!ctrl.IsKnownType)
            {
                errors.Add($"控制器 [{ctrl.Name}] 类型非法: \"{ctrl.Type}\"（合法值: {string.Join("/", ControllerConfig.KnownTypes)}）");
            }
        }

        // 初始化合并集合：顶层优先
        config.MergedTasks.AddRange(config.Tasks);
        foreach (var (key, opt) in config.Options ?? [])
        {
            opt.Key = key;
            config.MergedOptions[key] = opt;
        }

        config.MergedPresets.AddRange(config.Presets ?? []);
        config.MergedGroups.AddRange(config.Groups);
        config.MergedSettings.AddRange(config.Setting ?? []);

        // 按 import 数组顺序合并（官方：data.option.insert 不覆盖已有键 → 先到先得）
        foreach (var rel in config.Import ?? [])
        {
            var importPath = Path.GetFullPath(Path.Combine(config.BaseDirectory, rel));
            if (!File.Exists(importPath))
            {
                errors.Add($"import 文件不存在: {rel}");
                continue;
            }

            ImportFile imported;
            try
            {
                imported = JsonSerializer.Deserialize<ImportFile>(File.ReadAllText(importPath), SerializerOptions)
                           ?? throw new InvalidDataException("null");
            }
            catch (Exception ex)
            {
                errors.Add($"import 文件解析失败: {rel} ({ex.Message})");
                continue;
            }

            config.ImportedFiles.Add(rel);

            foreach (var t in imported.Tasks ?? [])
            {
                t.SourceFile = rel;
                config.MergedTasks.Add(t);
            }

            foreach (var (key, opt) in imported.Options ?? [])
            {
                opt.Key = key;
                // 对齐 C++ unordered_map::insert：已存在的键不覆盖（顶层/先到者优先）
                config.MergedOptions.TryAdd(key, opt);
            }

            config.MergedPresets.AddRange(imported.Presets ?? []);
            config.MergedGroups.AddRange(imported.Groups ?? []);
            config.MergedSettings.AddRange(imported.Setting ?? []);

            foreach (var pre in NormalizePretasks(imported.Pretask))
            {
                config.MergedPretasks.Add(pre);
            }
        }

        // 顶层 pretask / agent 规整（多态单对象或数组）
        config.MergedPretasks.InsertRange(0, NormalizePretasks(config.Pretask));
        config.Agents = NormalizeAgents(config.Agent);

        // 官方硬校验：每个任务引用的 option 必须在合并后的 option 池中存在
        foreach (var task in config.MergedTasks)
        {
            foreach (var optKey in task.Option ?? [])
            {
                if (!config.MergedOptions.ContainsKey(optKey))
                {
                    errors.Add($"任务 [{task.Name}] 引用了不存在的 option: \"{optKey}\"");
                }
            }
        }

        return config;
    }

    private static List<PretaskConfig> NormalizePretasks(JsonElement? element)
    {
        if (element is not { } el)
        {
            return [];
        }

        try
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                var single = el.Deserialize<PretaskConfig>(SerializerOptions);
                return single is null ? [] : [single];
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                return el.Deserialize<List<PretaskConfig>>(SerializerOptions) ?? [];
            }
        }
        catch (Exception)
        {
            // 忽略无法规整的 pretask，由调用方按缺失处理
        }

        return [];
    }

    private static List<AgentConfig> NormalizeAgents(JsonElement? element)
    {
        if (element is not { } el)
        {
            return [];
        }

        try
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                var single = el.Deserialize<AgentConfig>(SerializerOptions);
                return single is null ? [] : [single];
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                return el.Deserialize<List<AgentConfig>>(SerializerOptions) ?? [];
            }
        }
        catch (Exception)
        {
            // 忽略无法规整的 agent
        }

        return [];
    }

    // ---- 查询辅助 ----

    public ControllerConfig? FindController(string name) =>
        Controllers.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    public ResourceConfig? FindResource(string name) =>
        Resources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));

    public GroupConfig? FindGroup(string name) =>
        MergedGroups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.Ordinal));

    /// <summary>任务按分组归类（未声明 group 的任务归入“未分组”）。</summary>
    public Dictionary<string, List<TaskConfig>> TasksByGroup()
    {
        var result = new Dictionary<string, List<TaskConfig>>(StringComparer.Ordinal);
        foreach (var g in MergedGroups)
        {
            result[g.Name] = [];
        }

        result[string.Empty] = [];
        foreach (var t in MergedTasks)
        {
            if (t.Group is { Count: > 0 })
            {
                foreach (var g in t.Group)
                {
                    if (!result.TryGetValue(g, out var list))
                    {
                        list = [];
                        result[g] = list;
                    }

                    list.Add(t);
                }
            }
            else
            {
                result[string.Empty].Add(t);
            }
        }

        return result;
    }

    /// <summary>import 文件的 DTO：Schema 规定 import 仅合并 task/option/preset/group/pretask/global_option/setting。</summary>
    private sealed class ImportFile
    {
        [JsonPropertyName("task")]
        public List<TaskConfig>? Tasks { get; set; }

        [JsonPropertyName("option")]
        public Dictionary<string, OptionConfig>? Options { get; set; }

        [JsonPropertyName("preset")]
        public List<PresetConfig>? Presets { get; set; }

        [JsonPropertyName("group")]
        public List<GroupConfig>? Groups { get; set; }

        [JsonPropertyName("pretask")]
        public JsonElement? Pretask { get; set; }

        [JsonPropertyName("global_option")]
        public List<string>? GlobalOption { get; set; }

        [JsonPropertyName("setting")]
        public List<SettingSectionConfig>? Setting { get; set; }
    }
}
