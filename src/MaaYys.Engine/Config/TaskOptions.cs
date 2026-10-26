using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaaYys.Engine.Config;

/// <summary>select/switch/checkbox 的一个可选项（供界面直接渲染）。</summary>
public sealed record OptionChoice(string Name, string Label, string? Description);

/// <summary>input 类型的一个输入字段（供界面直接渲染）。</summary>
public sealed record OptionInputField(string Name, string Label, string? Description, string Default, string PipelineType);

/// <summary>
/// 选项面板的一行。<b>已按「当前取值」展开成扁平树</b>：只有被选中的 case 才会把它的子选项带出来，
/// 所以界面不需要自己判断可见性——重建一次列表即可（选项总数只有 104 个，重建开销可忽略）。
/// </summary>
public sealed record TaskOptionDefinition(
    string Key,
    string Label,
    string? Description,
    string Type,
    IReadOnlyList<OptionChoice> Choices,
    IReadOnlyList<OptionInputField> Inputs,
    int Level,
    string ParentKey,
    string? ParentCase)
{
    public bool IsSelect => Type == "select";

    public bool IsSwitch => Type == "switch";

    public bool IsCheckbox => Type == "checkbox";

    public bool IsInput => Type == "input";

    /// <summary>嵌套层级的左缩进（像素），界面直接绑到 Margin。</summary>
    public int Indent => Level * 18;
}

/// <summary>
/// 任务选项的「读取 + 编译」。
/// <para>
/// <b>取值的存储约定</b>（<see cref="TaskSelection.Options"/> 是 <c>string→string</c>，这里定死编码方式）：
/// </para>
/// <list type="bullet">
///   <item><c>select</c> / <c>switch</c>：键 = 选项名，值 = 选中的 case 名；</item>
///   <item><c>checkbox</c>：键 = 选项名，值 = 多个 case 名用 <c>,</c> 连接；</item>
///   <item><c>input</c>：键 = <c>选项名/字段名</c>，值 = 用户填的字符串。</item>
/// </list>
/// 未设置时一律回落到 <c>default_case</c> / <c>inputs[].default</c>，
/// 所以资源更新新增选项时旧配置自动拿到默认值，不会失效。
/// </summary>
public static partial class TaskOptions
{
    public const char ListSeparator = ',';

    /// <summary>编译输出用：不转义中文，日志与界面预览里能直接读。</summary>
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>input 字段在存储里的键：<c>选项名/字段名</c>。</summary>
    public static string InputKey(string optionKey, string inputName) => $"{optionKey}/{inputName}";

    // ---- 取值 ----

    /// <summary>select/switch 的当前值：用户设置优先，其次 <c>default_case</c>，最后 null。</summary>
    public static string? ResolveValue(OptionConfig option, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not null && values.TryGetValue(option.Key, out string? stored) && !string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        return option.DefaultCaseString;
    }

    /// <summary>checkbox 的当前值（多个 case）。</summary>
    public static IReadOnlyList<string> ResolveValues(OptionConfig option, IReadOnlyDictionary<string, string>? values)
    {
        if (values is not null && values.TryGetValue(option.Key, out string? stored) && !string.IsNullOrEmpty(stored))
        {
            return [.. stored.Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        return option.DefaultCaseList ?? [];
    }

    /// <summary>input 字段的当前值：用户填的优先，其次 <c>default</c>。</summary>
    public static string ResolveInput(OptionConfig option, InputConfig input, IReadOnlyDictionary<string, string>? values)
    {
        string key = InputKey(option.Key, input.Name);
        if (values is not null && values.TryGetValue(key, out string? stored))
        {
            return stored;
        }

        return input.Default ?? string.Empty;
    }

    // ---- 描述（给界面渲染）----

    /// <summary>
    /// 把任务的选项展开成界面可直接绑定的扁平列表。
    /// </summary>
    /// <param name="controllerName">当前控制器名（选项可用 <c>controller</c> 限定适用范围）。</param>
    /// <param name="resourceName">当前资源集名（选项可用 <c>resource</c> 限定适用范围）。</param>
    public static IReadOnlyList<TaskOptionDefinition> Describe(
        InterfaceConfig config,
        TaskConfig task,
        IReadOnlyDictionary<string, string>? values = null,
        string? controllerName = null,
        string? resourceName = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(task);

        List<TaskOptionDefinition> rows = [];
        foreach (string key in task.Option ?? [])
        {
            if (!config.MergedOptions.TryGetValue(key, out OptionConfig? option) || !IsApplicable(option, controllerName, resourceName))
            {
                continue;
            }

            AddOption(config, option, values, rows, level: 0, parentKey: string.Empty, parentCase: null, controllerName, resourceName);
        }

        return rows;
    }

    /// <summary>按任务名描述（找不到任务返回空表）。</summary>
    public static IReadOnlyList<TaskOptionDefinition> Describe(
        InterfaceConfig config,
        string taskName,
        IReadOnlyDictionary<string, string>? values = null,
        string? controllerName = null,
        string? resourceName = null)
    {
        TaskConfig? task = config.MergedTasks.FirstOrDefault(t => string.Equals(t.Name, taskName, StringComparison.Ordinal));
        return task is null ? [] : Describe(config, task, values, controllerName, resourceName);
    }

    /// <summary>
    /// 按显式给出的 option 键列表描述（不依赖任务）。用于「全局选项」面板：
    /// 直接渲染 <see cref="InterfaceConfig.GlobalOptionKeys"/> 返回的那一组全局 option。
    /// 同样按当前取值展开联动子选项。
    /// </summary>
    public static IReadOnlyList<TaskOptionDefinition> Describe(
        InterfaceConfig config,
        IReadOnlyList<string> optionKeys,
        IReadOnlyDictionary<string, string>? values = null,
        string? controllerName = null,
        string? resourceName = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<TaskOptionDefinition> rows = [];
        foreach (string key in optionKeys)
        {
            if (!config.MergedOptions.TryGetValue(key, out OptionConfig? option) || !IsApplicable(option, controllerName, resourceName))
            {
                continue;
            }

            AddOption(config, option, values, rows, level: 0, parentKey: string.Empty, parentCase: null, controllerName, resourceName);
        }

        return rows;
    }

    private static void AddOption(
        InterfaceConfig config,
        OptionConfig option,
        IReadOnlyDictionary<string, string>? values,
        List<TaskOptionDefinition> rows,
        int level,
        string parentKey,
        string? parentCase,
        string? controllerName,
        string? resourceName)
    {
        rows.Add(new TaskOptionDefinition(
            option.Key,
            option.DisplayName,
            option.Description,
            option.Type,
            [.. (option.Cases ?? []).Select(c => new OptionChoice(c.Name, c.DisplayName, c.Description))],
            [.. (option.Inputs ?? []).Select(i => new OptionInputField(
                i.Name, i.Label ?? i.Name, i.Description, i.Default ?? string.Empty, i.PipelineType ?? "string"))],
            level,
            parentKey,
            parentCase));

        foreach (CaseConfig chosen in SelectedCases(option, values))
        {
            foreach (string childKey in chosen.Option ?? [])
            {
                if (config.MergedOptions.TryGetValue(childKey, out OptionConfig? child)
                    && IsApplicable(child, controllerName, resourceName))
                {
                    AddOption(config, child, values, rows, level + 1, option.Key, chosen.Name, controllerName, resourceName);
                }
            }
        }
    }

    private static IEnumerable<CaseConfig> SelectedCases(OptionConfig option, IReadOnlyDictionary<string, string>? values)
    {
        if (option.Cases is null || option.Cases.Count == 0)
        {
            yield break;
        }

        if (option.Type == "checkbox")
        {
            HashSet<string> selected = new(ResolveValues(option, values), StringComparer.Ordinal);
            foreach (CaseConfig item in option.Cases.Where(c => selected.Contains(c.Name)))
            {
                yield return item;
            }

            yield break;
        }

        string? value = ResolveValue(option, values);
        if (value is null)
        {
            yield break;
        }

        CaseConfig? match = option.Cases.FirstOrDefault(c => string.Equals(c.Name, value, StringComparison.Ordinal));
        if (match is not null)
        {
            yield return match;
        }
    }

    private static bool IsApplicable(OptionConfig option, string? controllerName, string? resourceName)
    {
        if (option.Controller is { Count: > 0 } && controllerName is not null
            && !option.Controller.Contains(controllerName, StringComparer.Ordinal))
        {
            return false;
        }

        if (option.Resource is { Count: > 0 } && resourceName is not null
            && !option.Resource.Contains(resourceName, StringComparer.Ordinal))
        {
            return false;
        }

        return true;
    }

    // ---- 编译 ----

    /// <summary>
    /// 把一个任务的「选项取值」编译成 <c>pipeline_override</c> JSON。
    /// <para>
    /// 合并顺序：<c>task[].pipeline_override</c> → 全局选项层 → 任务级选项层；
    /// 全局层与任务级都走「选项自身 override → 选中 case override」的递归合并，
    /// 同名节点按<b>字段</b>递归合并（后者覆盖前者的同名字段），数组整体替换。
    /// 任务级选项优先级高于全局选项（同名节点字段被覆盖），便于「全局默认 + 单任务特例」。
    /// 这正是 P5 投递 <c>MaaTaskerPostTask</c> 时要传的那份 JSON。
    /// </para>
    /// </summary>
    /// <param name="warnings">取值非法等非致命问题（不影响编译，界面/日志可展示）。</param>
    public static string Compile(
        InterfaceConfig config,
        TaskConfig task,
        IReadOnlyDictionary<string, string>? values,
        out IReadOnlyList<string> warnings)
        => Compile(config, task, values, null, out warnings);

    /// <summary>
    /// 同 <see cref="Compile(InterfaceConfig, TaskConfig, IReadOnlyDictionary{string,string}?, out IReadOnlyList{string})"/>，
    /// 额外合并全局选项层（MXU setting 扩展）：<paramref name="globalValues"/> 来自配置组的
    /// <see cref="TaskConfiguration.GlobalOptions"/>。
    /// </summary>
    public static string Compile(
        InterfaceConfig config,
        TaskConfig task,
        IReadOnlyDictionary<string, string>? taskValues,
        IReadOnlyDictionary<string, string>? globalValues,
        out IReadOnlyList<string> warnings)
        => CompileNode(config, task, taskValues, globalValues, out warnings).ToJsonString(CompactJson);

    /// <summary>同上，返回可变 JSON 对象（测试与 P5 复用）。</summary>
    public static JsonObject CompileNode(
        InterfaceConfig config,
        TaskConfig task,
        IReadOnlyDictionary<string, string>? taskValues,
        IReadOnlyDictionary<string, string>? globalValues,
        out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(task);

        List<string> issues = [];
        var result = new JsonObject();

        Merge(result, task.PipelineOverride);

        // 全局选项层（低优先级，任务级可覆盖同名节点字段）
        ApplyOptionsByKeys(config, config.GlobalOptionKeys(), globalValues, result, issues);

        // 任务级选项层（高优先级）
        foreach (string key in task.Option ?? [])
        {
            if (!config.MergedOptions.TryGetValue(key, out OptionConfig? option))
            {
                issues.Add($"任务「{task.Name}」引用了不存在的选项「{key}」");
                continue;
            }

            ApplyOption(config, option, taskValues, result, issues);
        }

        warnings = issues;
        return result;
    }

    /// <summary>
    /// 仅编译全局选项层（MXU setting 扩展），返回合并后的 <c>pipeline_override</c> JSON。
    /// 用于「全局选项」面板预览，展示全局设置对所有任务贡献的 override。
    /// </summary>
    public static string CompileGlobal(
        InterfaceConfig config,
        IReadOnlyDictionary<string, string>? globalValues,
        out IReadOnlyList<string> warnings)
        => CompileGlobalNode(config, globalValues, out warnings).ToJsonString(CompactJson);

    /// <summary>同上，返回可变 JSON 对象（测试复用）。</summary>
    public static JsonObject CompileGlobalNode(
        InterfaceConfig config,
        IReadOnlyDictionary<string, string>? globalValues,
        out IReadOnlyList<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(config);

        List<string> issues = [];
        var result = new JsonObject();
        ApplyOptionsByKeys(config, config.GlobalOptionKeys(), globalValues, result, issues);
        warnings = issues;
        return result;
    }

    private static void ApplyOptionsByKeys(
        InterfaceConfig config,
        IReadOnlyList<string> keys,
        IReadOnlyDictionary<string, string>? values,
        JsonObject target,
        List<string> issues)
    {
        foreach (string key in keys)
        {
            if (!config.MergedOptions.TryGetValue(key, out OptionConfig? option))
            {
                continue;
            }

            ApplyOption(config, option, values, target, issues);
        }
    }

    private static void ApplyOption(
        InterfaceConfig config,
        OptionConfig option,
        IReadOnlyDictionary<string, string>? values,
        JsonObject target,
        List<string> issues)
    {
        Dictionary<string, JsonNode?> inputs = BuildInputValues(option, values, issues);

        // 1) 选项自身的 override（input 类型靠它注入占位符）
        Merge(target, Substitute(option.PipelineOverride, inputs));

        // 2) 选中的 case
        foreach (CaseConfig chosen in SelectedCases(option, values))
        {
            Merge(target, Substitute(chosen.PipelineOverride, inputs));

            foreach (string childKey in chosen.Option ?? [])
            {
                if (config.MergedOptions.TryGetValue(childKey, out OptionConfig? child))
                {
                    ApplyOption(config, child, values, target, issues);
                }
            }
        }
    }

    /// <summary>取得 input 选项的各字段取值，并按 <c>pipeline_type</c> 转成 JSON 值。</summary>
    private static Dictionary<string, JsonNode?> BuildInputValues(
        OptionConfig option, IReadOnlyDictionary<string, string>? values, List<string> issues)
    {
        Dictionary<string, JsonNode?> map = new(StringComparer.Ordinal);
        foreach (InputConfig input in option.Inputs ?? [])
        {
            string raw = ResolveInput(option, input, values);
            map[input.Name] = ConvertValue(option, input, raw, issues);
        }

        return map;
    }

    private static JsonNode? ConvertValue(OptionConfig option, InputConfig input, string raw, List<string> issues)
    {
        switch (input.PipelineType)
        {
            case "int":
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
                {
                    return JsonValue.Create(number);
                }

                issues.Add($"选项「{option.DisplayName}」的「{input.Label ?? input.Name}」应为整数，实际是「{raw}」，已按字符串处理");
                return JsonValue.Create(raw);

            case "bool":
                if (bool.TryParse(raw, out bool flag))
                {
                    return JsonValue.Create(flag);
                }

                if (raw is "1" or "0")
                {
                    return JsonValue.Create(raw == "1");
                }

                issues.Add($"选项「{option.DisplayName}」的「{input.Label ?? input.Name}」应为布尔值，实际是「{raw}」，已按字符串处理");
                return JsonValue.Create(raw);

            default:
                return JsonValue.Create(raw);
        }
    }

    // ---- JSON 处理 ----

    /// <summary>深合并：对象递归合并字段，其它类型整体替换。</summary>
    private static void Merge(JsonObject target, JsonElement? source)
    {
        if (source is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        Merge(target, JsonNode.Parse(element.GetRawText()) as JsonObject);
    }

    /// <summary>替换占位符之后的结果（可能是对象，也可能不是）——只在是对象时合并。</summary>
    private static void Merge(JsonObject target, JsonNode? source)
    {
        if (source is JsonObject obj)
        {
            Merge(target, obj);
        }
    }

    private static void Merge(JsonObject target, JsonObject? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> property in source)
        {
            if (target[property.Key] is JsonObject existing && property.Value is JsonObject incoming)
            {
                Merge(existing, incoming);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderPattern { get; }

    /// <summary>替换 <c>{字段名}</c>：整串就是占位符时保留类型（int/bool），否则按文本拼接。</summary>
    private static JsonNode? Substitute(JsonElement? source, IReadOnlyDictionary<string, JsonNode?> inputs)
    {
        if (source is not { } element || inputs.Count == 0)
        {
            return source is { } raw ? JsonNode.Parse(raw.GetRawText()) : null;
        }

        return SubstituteNode(JsonNode.Parse(element.GetRawText()), inputs);
    }

    private static JsonNode? SubstituteNode(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> inputs)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    copy[property.Key] = SubstituteNode(property.Value?.DeepClone(), inputs);
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (JsonNode? item in array)
                {
                    copy.Add(SubstituteNode(item?.DeepClone(), inputs));
                }

                return copy;
            }

            case JsonValue value when value.TryGetValue(out string? text):
            {
                Match exact = PlaceholderPattern.Match(text);
                if (exact.Success && exact.Index == 0 && exact.Length == text.Length
                    && inputs.TryGetValue(exact.Groups[1].Value, out JsonNode? typed))
                {
                    return typed?.DeepClone();
                }

                return JsonValue.Create(PlaceholderPattern.Replace(text, m =>
                    inputs.TryGetValue(m.Groups[1].Value, out JsonNode? replacement)
                        ? Literal(replacement)
                        : m.Value));
            }

            default:
                return node;
        }
    }

    private static string Literal(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue value when value.TryGetValue(out string? text) => text ?? string.Empty,
        _ => node.ToJsonString(),
    };
}
