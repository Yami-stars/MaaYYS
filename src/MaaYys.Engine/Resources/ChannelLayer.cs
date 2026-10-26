using System.Text.Json.Nodes;

namespace MaaYys.Engine.Resources;

/// <summary>渠道层里一个节点的定义（含它所在的文件、字段清单与字段值）。</summary>
public sealed record ChannelNodeOverride(
    string Name,
    string RelativeFile,
    IReadOnlyList<string> Fields,
    bool IsNewNode)
{
    /// <summary>渠道层写的字段值（字段名 → JSON 原文），用于与 base 做冗余比对。</summary>
    public IReadOnlyDictionary<string, string> Values { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string? ValueOf(string field)
        => Values.TryGetValue(field, out string? text) ? text : null;
}

/// <summary>渠道层解析结果：这一层相对 base 改了什么、包名是什么、有没有踩坑。</summary>
public sealed class ChannelLayer
{
    /// <summary>渠道目录名（official / huawei …）。</summary>
    public required string Name { get; init; }

    /// <summary>渠道 bundle 的绝对路径。</summary>
    public required string BundlePath { get; init; }

    /// <summary>渠道层相对 base 的改动（按节点名排序）。</summary>
    public IReadOnlyList<ChannelNodeOverride> Overrides { get; init; } = [];

    /// <summary>只在渠道层出现、base 里没有的节点（渠道专属玩法/弹窗）。</summary>
    public IReadOnlyList<string> ExclusiveNodes { get; init; } = [];

    /// <summary>启动应用用的 intent（可带 activity，给 <c>am start</c> 用）。</summary>
    public string? StartPackage { get; init; }

    /// <summary>关闭应用用的包名（必须是纯包名，<c>am force-stop</c> 不接受 activity）。</summary>
    public string? StopPackage { get; init; }

    /// <summary>解析/规范性问题（不是致命错误，但值得在界面上提示）。</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    public bool HasIssues => Issues.Count > 0;

    public string Summary => ExclusiveNodes.Count == 0
        ? $"{Overrides.Count} 处覆盖"
        : $"{Overrides.Count} 处覆盖 + {ExclusiveNodes.Count} 个专属节点";
}

/// <summary>一个资源集的「启动/关闭应用」包名。</summary>
public sealed record ChannelPackages(string? Start, string? Stop)
{
    /// <summary>两个包名都拿不到（纯 base 资源集且 base 也没写）时为真。</summary>
    public bool IsEmpty => Start is null && Stop is null;

    public override string ToString() => $"启动={Start ?? "—"} 关闭={Stop ?? "—"}";
}

/// <summary>
/// 渠道服分层解析（P3.5）。
/// <para>
/// 资源链约定：<c>resource[].path</c> 的<b>第一个</b> bundle 是 base（完整定义），
/// 后面每一个都是「渠道层」——只写与 base 的差异。MaaFramework 合并是<b>字段级</b>的，
/// 所以渠道层写一个字段就只覆盖那一个字段，没写的保持 base 的值。
/// </para>
/// <para>
/// <b>package 必须直读文件，不能用 <c>MaaResourceGetNodeData</c></b>：框架规定 <c>package</c>
/// 只在 <c>action</c> 为 <c>StartApp</c>/<c>StopApp</c> 时合法，base 的「启动游戏」是
/// <c>DirectHit</c>（桌面端语义：游戏已经在跑），渠道层写的 package 会被框架当未知字段丢弃，
/// 从节点数据里读不出来。所以引擎自己读文件，拿到包名后在 Adb 控制器下显式
/// <c>MaaControllerPostStartApp</c>。
/// </para>
/// </summary>
public static class ChannelLayerResolver
{
    /// <summary>base 的节点索引（节点名 → 定义），用于判定「渠道层是不是新增节点」。</summary>
    private static Dictionary<string, JsonObject> IndexNodes(string bundlePath)
    {
        Dictionary<string, JsonObject> index = new(StringComparer.Ordinal);
        string pipeline = Path.Combine(bundlePath, "pipeline");
        if (!Directory.Exists(pipeline))
        {
            return index;
        }

        foreach (string file in Directory.GetFiles(pipeline, "*.json", SearchOption.AllDirectories))
        {
            JsonObject? document = TryReadObject(file);
            if (document is null)
            {
                continue;
            }

            foreach (KeyValuePair<string, JsonNode?> property in document)
            {
                if (property.Value is JsonObject node)
                {
                    index[property.Key] = node;
                }
            }
        }

        return index;
    }

    /// <summary>
    /// 解析一个资源集的渠道层。链上只有 base（如「官服2」）时返回 null。
    /// </summary>
    public static ChannelLayer? Resolve(ResourceChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.BundlePaths.Count < 2)
        {
            return null;
        }

        string basePath = chain.BundlePaths[0];
        Dictionary<string, JsonObject> baseNodes = IndexNodes(basePath);

        // 多个渠道层的情况（目前上游没有，但要保证语义：后面覆盖前面）
        string channelPath = chain.BundlePaths[^1];
        string pipeline = Path.Combine(channelPath, "pipeline");
        var overrides = new List<ChannelNodeOverride>();
        var exclusive = new List<string>();
        var issues = new List<string>();
        string? startPackage = null;
        string? stopPackage = null;

        if (Directory.Exists(pipeline))
        {
            string[] files = Directory.GetFiles(pipeline, "*.json", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                JsonObject? document = TryReadObject(file);
                if (document is null)
                {
                    issues.Add($"渠道层 {Path.GetFileName(file)} 无法解析");
                    continue;
                }

                string relative = Path.GetRelativePath(pipeline, file).Replace('\\', '/');
                foreach (KeyValuePair<string, JsonNode?> property in document)
                {
                    if (property.Value is not JsonObject node)
                    {
                        continue;
                    }

                    List<string> fields = [.. node.Select(f => f.Key)];
                    Dictionary<string, string> values = new(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, JsonNode?> field in node)
                    {
                        values[field.Key] = field.Value?.ToJsonString() ?? "null";
                    }

                    if (!baseNodes.ContainsKey(property.Key))
                    {
                        exclusive.Add(property.Key);
                    }

                    bool isNew = !baseNodes.ContainsKey(property.Key);
                    overrides.Add(new ChannelNodeOverride(property.Key, relative, fields, isNew)
                    {
                        Values = values,
                    });

                    if (string.Equals(property.Key, StartNodeName, StringComparison.Ordinal)
                        && node.TryGetPropertyValue("package", out JsonNode? startNode)
                        && startNode is JsonValue startValue
                        && startValue.TryGetValue<string>(out string? start))
                    {
                        startPackage = start;
                    }

                    if (string.Equals(property.Key, StopNodeName, StringComparison.Ordinal)
                        && node.TryGetPropertyValue("package", out JsonNode? stopNode)
                        && stopNode is JsonValue stopValue
                        && stopValue.TryGetValue<string>(out string? stop))
                    {
                        stopPackage = stop;
                    }
                }
            }
        }
        else
        {
            issues.Add($"渠道层 {Name(channelPath)} 没有 pipeline 目录");
        }

        CollectIssues(issues, baseNodes, overrides, startPackage, stopPackage);

        return new ChannelLayer
        {
            Name = Name(channelPath),
            BundlePath = channelPath,
            Overrides = [.. overrides.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)],
            ExclusiveNodes = [.. exclusive.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)],
            StartPackage = startPackage,
            StopPackage = stopPackage,
            Issues = issues,
        };
    }

    /// <summary>
    /// 取资源集的启动/关闭包名。渠道层没写就回落到 base（base 的「关闭阴阳师」带官服包名），
    /// 这样「官服2」这种纯 base 资源集也能拿到一个可用的包名。
    /// </summary>
    public static ChannelPackages ResolvePackages(ResourceChain chain)
    {
        ArgumentNullException.ThrowIfNull(chain);

        string? start = null;
        string? stop = null;

        // 按加载顺序读：后加载的渠道层覆盖 base
        foreach (string bundle in chain.BundlePaths)
        {
            string file = Path.Combine(bundle, "pipeline", "启动游戏.json");
            JsonObject? document = TryReadObject(file);
            if (document is null)
            {
                continue;
            }

            if (document.TryGetPropertyValue(StartNodeName, out JsonNode? startNode)
                && startNode is JsonObject startObject
                && startObject.TryGetPropertyValue("package", out JsonNode? sp)
                && sp is JsonValue sv && sv.TryGetValue<string>(out string? s))
            {
                start = s;
            }

            if (document.TryGetPropertyValue(StopNodeName, out JsonNode? stopNode)
                && stopNode is JsonObject stopObject
                && stopObject.TryGetPropertyValue("package", out JsonNode? tp)
                && tp is JsonValue tv && tv.TryGetValue<string>(out string? t))
            {
                stop = t;
            }
        }

        return new ChannelPackages(start, stop);
    }

    /// <summary>规范性问题检查：都是「上游这么写会导致行为不对」的硬伤。</summary>
    private static void CollectIssues(
        List<string> issues,
        Dictionary<string, JsonObject> baseNodes,
        List<ChannelNodeOverride> overrides,
        string? startPackage,
        string? stopPackage)
    {
        foreach (ChannelNodeOverride item in overrides)
        {
            // 渠道层改「启动游戏」的 next 会整条替换掉 base 的 next，
            // 而 base 的 next 是 [启动游戏-跳过动画] —— 一改就再也不跳开场动画了。
            if (string.Equals(item.Name, StartNodeName, StringComparison.Ordinal)
                && item.Fields.Contains("next", StringComparer.Ordinal))
            {
                issues.Add($"渠道层覆盖了「{StartNodeName}」的 next，会跳过 base 的「{SkipNodeName}」（开场动画）；"
                           + $"应改为覆盖「{SkipNodeName}」的 next");
            }

            // 渠道层重复声明 base 已有的同值字段：说明这层没跟上 base 的改动。
            // package 例外：它是「渠道身份」的核心声明，显式写出来比隐式继承清晰，不算问题。
            if (baseNodes.TryGetValue(item.Name, out JsonObject? baseNode))
            {
                foreach (string field in item.Fields)
                {
                    if (string.Equals(field, "package", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!baseNode.TryGetPropertyValue(field, out JsonNode? baseValue)
                        || item.ValueOf(field) is not { } channelValue)
                    {
                        continue;
                    }

                    if (string.Equals(baseValue?.ToJsonString(), channelValue, StringComparison.Ordinal))
                    {
                        issues.Add($"「{item.Name}.{field}」与 base 完全相同，属冗余声明");
                    }
                }
            }
        }

        if (stopPackage is not null && stopPackage.Contains('/', StringComparison.Ordinal))
        {
            issues.Add($"「{StopNodeName}」的 package 带了 activity（{stopPackage}），"
                       + "am force-stop 需要纯包名，关不掉游戏");
        }
    }

    private static JsonObject? TryReadObject(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            return JsonNode.Parse(
                File.ReadAllText(file),
                documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Name(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileName(trimmed);
    }

    /// <summary>「打开游戏」任务的入口节点。</summary>
    public const string StartNodeName = "启动游戏";

    /// <summary>「关闭游戏」任务的入口节点。</summary>
    public const string StopNodeName = "关闭阴阳师";

    /// <summary>base 里负责跳过开场动画的节点（渠道专属节点要挂到它的 next 上）。</summary>
    public const string SkipNodeName = "启动游戏-跳过动画";
}
