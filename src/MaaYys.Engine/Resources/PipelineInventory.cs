using System.Text.Json;

namespace MaaYys.Engine.Resources;

/// <summary>自检问题的严重程度。</summary>
public enum PipelineSeverity
{
    /// <summary>仅供参考（例如统计信息、未被引用的入口节点）。</summary>
    Info,

    /// <summary>能跑但有问题：废弃字段、同一 bundle 内节点重名、资源文件缺失等。</summary>
    Warning,

    /// <summary>确定的错误：JSON 解析失败、引用指向不存在的节点、识别/动作类型未知。</summary>
    Error,
}

/// <summary>一条自检结论。<see cref="File"/> 与 <see cref="Node"/> 用于定位，可能为 null。</summary>
public sealed record PipelineDiagnostic(
    PipelineSeverity Severity,
    string Kind,
    string Message,
    string? File = null,
    string? Node = null)
{
    public override string ToString()
    {
        string where = (File, Node) switch
        {
            (not null, not null) => $"{File} :: {Node}",
            (not null, null) => File,
            (null, not null) => Node,
            _ => "-",
        };

        string tag = Severity switch
        {
            PipelineSeverity.Error => "错误",
            PipelineSeverity.Warning => "警告",
            _ => "信息",
        };

        return $"[{tag}][{Kind}] {where} — {Message}";
    }
}

/// <summary>一个 pipeline 节点在文件里的原始定义摘要（已把 v1 / v2 两种写法归一）。</summary>
public sealed record PipelineNodeInfo(
    string Name,
    string File,
    string Recognition,
    string Action,
    bool Enabled,
    IReadOnlyList<string> Next,
    IReadOnlyList<string> OnError,
    IReadOnlyList<string> Anchors,
    string? CustomAction,
    string? CustomRecognition,
    IReadOnlyList<string> Templates,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> UsedAnchors)
{
    /// <summary>本节点会流转出去的全部节点名（next + on_error）。</summary>
    public IEnumerable<string> Outgoing => Next.Concat(OnError);
}

/// <summary>
/// 静态扫描一个 pipeline 目录，回答「P3.1 验收」的两个问题：
/// <list type="number">
///   <item>JSON 是否全部可解析；</item>
///   <item>节点引用是否有悬空（<c>next</c> / <c>on_error</c> / <c>interrupt</c> / 锚点 / 模板图 / 模型）。</item>
/// </list>
/// 只读文件、不依赖 MaaFramework，因此可以在加载资源之前跑，也能用于 GUI 的「资源自检」。
/// </summary>
public sealed class PipelineInventory
{
    /// <summary>协议里定义的全部识别算法（v1 值为准，v2 只是换了嵌套位置）。</summary>
    public static readonly IReadOnlySet<string> KnownRecognitions = new HashSet<string>(StringComparer.Ordinal)
    {
        "DirectHit", "TemplateMatch", "FeatureMatch", "ColorMatch", "OCR",
        "NeuralNetworkClassify", "NeuralNetworkDetect", "And", "Or", "Custom",
    };

    /// <summary>协议里定义的全部动作。</summary>
    public static readonly IReadOnlySet<string> KnownActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "DoNothing", "Click", "LongPress", "Swipe", "MultiSwipe", "TouchDown", "TouchMove",
        "TouchUp", "Scroll", "ClickKey", "LongPressKey", "KeyDown", "KeyUp", "InputText",
        "StartApp", "StopApp", "StopTask", "Command", "Shell", "Screencap", "Custom",
    };

    /// <summary>框架 5.1 起废弃、应改用 <c>[JumpBack]</c> 的字段。</summary>
    private static readonly string[] DeprecatedFields = ["is_sub", "interrupt"];

    private readonly List<PipelineDiagnostic> _diagnostics = [];
    private readonly Dictionary<string, PipelineNodeInfo> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeFile = new(StringComparer.Ordinal);
    private readonly HashSet<string> _referenced = new(StringComparer.Ordinal);
    private readonly HashSet<string> _declaredAnchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _usedAnchors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _templates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _models = new(StringComparer.Ordinal);
    private readonly HashSet<string> _customActions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _customRecognitions = new(StringComparer.Ordinal);
    private readonly List<string> _files = [];
    private readonly List<string> _parseFailures = [];
    private HashSet<string> _overrideTargets = new(StringComparer.Ordinal);
    private HashSet<string> _overrideReferences = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _anchorOwners = new(StringComparer.Ordinal);
    private readonly Lazy<HashSet<string>> _liveNodes;

    private PipelineInventory(string pipelineDirectory, string? imageDirectory)
    {
        PipelineDirectory = pipelineDirectory;
        ImageDirectory = imageDirectory;
        _liveNodes = new Lazy<HashSet<string>>(ComputeLiveNodes);
    }

    public string PipelineDirectory { get; }

    /// <summary>用于校验模板图的 <c>image/</c> 目录；为 null 则跳过模板存在性检查。</summary>
    public string? ImageDirectory { get; }

    /// <summary>生效的 <c>default_pipeline.json</c> 默认值。</summary>
    public PipelineDefaults Defaults { get; private init; } = PipelineDefaults.None;

    /// <summary>成功解析的文件数。</summary>
    public int ParsedFileCount => _files.Count;

    /// <summary>解析失败的文件（相对路径）。</summary>
    public IReadOnlyList<string> ParseFailures => _parseFailures;

    public IReadOnlyList<PipelineDiagnostic> Diagnostics => _diagnostics;

    /// <summary>全部节点（同名节点以最后一次出现为准，并在诊断里给出重名警告）。</summary>
    public IReadOnlyDictionary<string, PipelineNodeInfo> Nodes => _nodes;

    /// <summary>pipeline 里出现的 <c>Custom</c> 动作名，agent 必须实现这些。</summary>
    public IReadOnlyCollection<string> CustomActions => _customActions;

    /// <summary>pipeline 里出现的 <c>Custom</c> 识别名，agent 必须实现这些。</summary>
    public IReadOnlyCollection<string> CustomRecognitions => _customRecognitions;

    /// <summary>被引用的模板图相对路径（相对 <c>image/</c>）。</summary>
    public IReadOnlyCollection<string> Templates => _templates;

    /// <summary>被引用的模型文件名（相对 <c>model/</c>）。</summary>
    public IReadOnlyCollection<string> Models => _models;

    /// <summary>没有被任何节点引用的节点——通常是任务入口，也可能是死节点。</summary>
    public IReadOnlyCollection<string> OrphanNodes =>
        [.. _nodes.Keys.Where(n => !_referenced.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>tasks/*.json 里声明的任务入口节点名。</summary>
    public IReadOnlyCollection<string> TaskEntries { get; private set; } = [];

    /// <summary>任务声明了入口、但 pipeline 里根本没有这个节点——该任务永远跑不起来。</summary>
    public IReadOnlyCollection<string> MissingTaskEntries =>
        [.. TaskEntries.Where(e => !_nodes.ContainsKey(e)).OrderBy(e => e, StringComparer.Ordinal)];

    /// <summary>被 option 的 pipeline_override 覆盖到的节点名（这些节点是活的，不能当成死节点删）。</summary>
    public IReadOnlyCollection<string> OverrideTargets => _overrideTargets;

    /// <summary>
    /// 命名不规范的节点：编辑器自动生成的 <c>N-1234567890123</c>、拖拽复制出来的 <c>xxx_copy1</c>、
    /// 以及 <c>test</c> 开头的一次性节点。流程表 P3.1 要求「统一命名规范」，
    /// 但批量重命名现有节点风险太高（1500+ 个节点、800+ 处 next 引用），所以这里只做统计、不动数据。
    /// </summary>
    public IReadOnlyCollection<string> NamingIssues =>
        [.. _nodes.Keys.Where(IsPoorlyNamed).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 节点名像「编辑器残留」：<c>N-时间戳</c>、<c>xxx_copy1</c>、<c>test*</c>。
    /// 数据清理用它给不可达节点定风险等级（都像残留 → 可放心清理）。
    /// </summary>
    public static bool IsPoorlyNamed(string name)
    {
        // 编辑器自动生成：N-1766075283687
        if (name.Length > 2 && name[0] == 'N' && name[1] == '-' && IsAllDigits(name[2..]))
        {
            return true;
        }

        // 拖拽复制：寮3039_copy1_copy1
        if (name.Contains("_copy", StringComparison.Ordinal))
        {
            return true;
        }

        // 一次性测试节点：test / testAction / testOCR
        return name.StartsWith("test", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllDigits(string text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }


    /// <summary>
    /// 从任务入口 / 覆盖目标出发<b>可达</b>的节点集合。
    /// <para>
    /// 只看「有没有人引用」是不够的：<c>test.json</c> 里的 <c>N-1766075283687</c> 确实被人引用，
    /// 但引用它的 <c>测试-停止任务</c> 自己就是死节点，整条链永远走不到。
    /// 所以这里做真实可达性遍历（含 <c>[Anchor]</c> 锚点解析），迭代到不动点。
    /// </para>
    /// </summary>
    public IReadOnlyCollection<string> LiveNodes => _liveNodes.Value;

    /// <summary>
    /// 真死节点：从任何任务入口/覆盖目标出发都走不到。
    /// 永远不会被执行，是整理清单里最安全的删除对象。
    /// </summary>
    public IReadOnlyCollection<string> DeadNodes =>
        [.. _nodes.Keys.Where(n => !_liveNodes.Value.Contains(n)).OrderBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// 死节点按文件分组——判断「整份文件是不是垃圾」靠它：文件里所有节点都是死节点，文件即可整体剔除。
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DeadNodesByFile
    {
        get
        {
            HashSet<string> dead = [.. DeadNodes];
            return _nodes.Values
                .Where(n => dead.Contains(n.Name))
                .GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<string>)[.. g.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal)],
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public IEnumerable<PipelineDiagnostic> Errors =>
        _diagnostics.Where(d => d.Severity == PipelineSeverity.Error);

    public IEnumerable<PipelineDiagnostic> Warnings =>
        _diagnostics.Where(d => d.Severity == PipelineSeverity.Warning);

    public bool IsHealthy => !Errors.Any();

    /// <summary>
    /// 扫描目录（递归）。
    /// </summary>
    /// <param name="pipelineDirectory">含 <c>*.json</c> 的 pipeline 目录。</param>
    /// <param name="imageDirectory">模板图根目录；传 null 跳过模板存在性检查。</param>
    /// <param name="cleanupCandidates">额外视为「调试垃圾」的节点名/文件名（如 <c>debug.json</c>、<c>test.json</c>）。</param>
    /// <param name="scope">
    /// pipeline 之外的引用来源（任务入口、option 的 pipeline_override）。
    /// 不传会把「只被 tasks 引用的节点」误判成死节点。
    /// </param>
    /// <param name="defaults">
    /// <c>default_pipeline.json</c> 的 <c>Default</c> 段。默认 <c>on_error</c> 会给每个节点
    /// 挂一条兜底边，不传会把兜底链上的节点误判成死节点。
    /// </param>
    public static PipelineInventory Scan(
        string pipelineDirectory,
        string? imageDirectory = null,
        IReadOnlyCollection<string>? cleanupCandidates = null,
        ResourceReferenceScope? scope = null,
        PipelineDefaults? defaults = null)
    {
        var inventory = new PipelineInventory(pipelineDirectory, imageDirectory)
        {
            TaskEntries = scope is null ? [] : [.. scope.TaskEntries.Where(e => !string.IsNullOrWhiteSpace(e))],
            Defaults = defaults ?? PipelineDefaults.None,
        };

        if (scope is not null)
        {
            inventory._overrideTargets = [.. scope.OverrideTargets];
            inventory._overrideReferences = [.. scope.OverrideReferences];
            foreach (string reference in scope.OverrideReferences)
            {
                inventory._referenced.Add(reference);
            }
        }

        inventory.ScanCore(cleanupCandidates ?? []);
        return inventory;
    }

    private void ScanCore(IReadOnlyCollection<string> cleanupCandidates)
    {
        if (!Directory.Exists(PipelineDirectory))
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "目录缺失", $"pipeline 目录不存在：{PipelineDirectory}"));
            return;
        }

        string[] files = Directory.GetFiles(PipelineDirectory, "*.json", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            ParseFile(file);
        }

        CheckReferences();
        CheckTemplateAndModel();
        ReportOrphans();
        ReportCustomHooks();
        ReportCleanupCandidates(cleanupCandidates);
        ReportUnusedAnchors();
    }

    private string Relative(string fullPath)
        => Path.GetRelativePath(PipelineDirectory, fullPath).Replace('\\', '/');

    private void ParseFile(string file)
    {
        string relative = Relative(file);
        JsonDocument document;

        try
        {
            using FileStream stream = File.OpenRead(file);
            document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _parseFailures.Add(relative);
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "解析失败", $"JSON 无法解析：{ex.Message}", relative));
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "结构错误",
                    $"pipeline 文件顶层必须是「节点名 → 节点定义」的对象，实际是 {root.ValueKind}", relative));
                return;
            }

            _files.Add(relative);

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    _diagnostics.Add(new PipelineDiagnostic(
                        PipelineSeverity.Error, "结构错误",
                        $"节点定义必须是对象，实际是 {property.Value.ValueKind}", relative, property.Name));
                    continue;
                }

                if (_nodeFile.TryGetValue(property.Name, out string? previousFile))
                {
                    _diagnostics.Add(new PipelineDiagnostic(
                        PipelineSeverity.Warning, "节点重名",
                        $"节点在 {previousFile} 与 {relative} 中重复定义；同一 bundle 内的覆盖顺序由框架决定，行为不可预期",
                        relative, property.Name));
                }

                _nodeFile[property.Name] = relative;
                _nodes[property.Name] = ReadNode(property.Name, relative, property.Value);
            }
        }
    }

    private PipelineNodeInfo ReadNode(string name, string file, JsonElement node)
    {
        // ---- v1 / v2 归一：v2 把类型放 recognition.type、参数放 recognition.param ----
        (string recognition, JsonElement recognitionParam) = ReadTypeAndParam(node, "recognition", "DirectHit");
        (string action, JsonElement actionParam) = ReadTypeAndParam(node, "action", "DoNothing");

        if (!KnownRecognitions.Contains(recognition))
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "未知识别算法", $"recognition = \"{recognition}\" 不在协议定义内", file, name));
        }

        if (!KnownActions.Contains(action))
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "未知动作", $"action = \"{action}\" 不在协议定义内", file, name));
        }

        foreach (string deprecated in DeprecatedFields)
        {
            if (node.TryGetProperty(deprecated, out _))
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Warning, "废弃字段",
                    $"`{deprecated}` 自 5.1 起废弃，应改用 `[JumpBack]` 节点属性", file, name));
            }
        }

        // ---- 引用收集 ----
        List<string> next = ReadNodeRefs(node, "next", file, name, out List<string> nextAnchors);
        List<string> onError = ReadNodeRefs(node, "on_error", file, name, out List<string> onErrorAnchors);
        List<string> legacyInterrupt = ReadInterruptRefs(node, "interrupt", file, name);

        foreach (string reference in next.Concat(onError).Concat(legacyInterrupt))
        {
            _referenced.Add(reference);
        }

        foreach (string anchor in nextAnchors.Concat(onErrorAnchors))
        {
            _usedAnchors.Add(anchor);
        }

        // roi / target 的字符串形式也可以是节点名（v5.9 还支持 [Anchor] 前缀）
        foreach (string field in (string[])["roi", "target"])
        {
            if (TryGetField(node, recognitionParam, actionParam, field, out JsonElement value))
            {
                foreach (string reference in CollectPlainRefs(value, file, name, field))
                {
                    _referenced.Add(reference);
                }
            }
        }

        // ---- 锚点声明 ----
        List<string> anchors = ReadAnchors(node, file, name);

        // ---- 模板 / 模型 / 自定义钩子 ----
        List<string> templates = ReadStringList(node, recognitionParam, "template");
        foreach (string template in templates)
        {
            _templates.Add(NormalizeAssetPath(template));
        }

        foreach (string model in ReadStringList(node, recognitionParam, "model"))
        {
            _models.Add(NormalizeAssetPath(model));
        }

        string? customAction = null;
        if (action == "Custom")
        {
            customAction = ReadString(node, actionParam, "custom_action");
            if (string.IsNullOrWhiteSpace(customAction))
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "缺参数", "action = Custom 但未填 custom_action", file, name));
            }
            else
            {
                _customActions.Add(customAction);
            }
        }

        string? customRecognition = null;
        if (recognition == "Custom")
        {
            customRecognition = ReadString(node, recognitionParam, "custom_recognition");
            if (string.IsNullOrWhiteSpace(customRecognition))
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "缺参数", "recognition = Custom 但未填 custom_recognition", file, name));
            }
            else
            {
                _customRecognitions.Add(customRecognition);
            }
        }

        if (recognition == "TemplateMatch" && templates.Count == 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "缺参数", "recognition = TemplateMatch 但没有 template", file, name));
        }

        if ((recognition is "NeuralNetworkDetect" or "NeuralNetworkClassify")
            && ReadStringList(node, recognitionParam, "model").Count == 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "缺参数", $"recognition = {recognition} 但没有 model", file, name));
        }

        bool enabled = !node.TryGetProperty("enabled", out JsonElement enabledElement)
            || enabledElement.ValueKind != JsonValueKind.False;

        return new PipelineNodeInfo(
            name, file, recognition, action, enabled,
            next, onError, anchors, customAction, customRecognition, templates,
            ReadStringList(node, recognitionParam, "model"),
            [.. nextAnchors.Concat(onErrorAnchors).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>读 <c>type</c> 与 <c>param</c>：v1 时 param 就是节点自身，v2 时是嵌套对象。</summary>
    private static (string Type, JsonElement Param) ReadTypeAndParam(JsonElement node, string field, string fallback)
    {
        if (!node.TryGetProperty(field, out JsonElement value))
        {
            return (fallback, node);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return (value.GetString() ?? fallback, node);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            string type = fallback;
            JsonElement param = node;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                {
                    type = property.Value.GetString() ?? fallback;
                }
                else if (property.NameEquals("param") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    param = property.Value;
                }
            }

            return (type, param);
        }

        return (fallback, node);
    }

    /// <summary>在 v1（节点顶层）与 v2（param 内）两处查字段。</summary>
    private static bool TryGetField(
        JsonElement node, JsonElement recognitionParam, JsonElement actionParam, string field, out JsonElement value)
    {
        if (recognitionParam.ValueKind == JsonValueKind.Object
            && recognitionParam.TryGetProperty(field, out value))
        {
            return true;
        }

        if (actionParam.ValueKind == JsonValueKind.Object
            && actionParam.TryGetProperty(field, out value))
        {
            return true;
        }

        return node.TryGetProperty(field, out value);
    }

    private static string? ReadString(JsonElement node, JsonElement param, string field)
        => param.ValueKind == JsonValueKind.Object && param.TryGetProperty(field, out JsonElement inner)
            && inner.ValueKind == JsonValueKind.String
            ? inner.GetString()
            : node.TryGetProperty(field, out JsonElement outer) && outer.ValueKind == JsonValueKind.String
                ? outer.GetString()
                : null;

    private static List<string> ReadStringList(JsonElement node, JsonElement param, string field)
    {
        JsonElement value = default;
        bool found = param.ValueKind == JsonValueKind.Object && param.TryGetProperty(field, out value);
        found = found || node.TryGetProperty(field, out value);
        if (!found)
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => [value.GetString()!.Replace('\\', '/')],
            JsonValueKind.Array => [.. value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Replace('\\', '/'))],
            _ => [],
        };
    }

    /// <summary>读取 <c>next</c> / <c>on_error</c>：字符串、NodeAttr 对象、或二者的异质数组。</summary>
    private List<string> ReadNodeRefs(
        JsonElement node, string field, string file, string name, out List<string> anchors)
    {
        anchors = [];
        if (!node.TryGetProperty(field, out JsonElement value))
        {
            return [];
        }

        var refs = new List<string>();
        Collect(value, refs, anchors, field, file, name, _diagnostics);
        return refs;
    }

    private static void Collect(
        JsonElement value, List<string> refs, List<string> anchors, string field, string file, string node,
        List<PipelineDiagnostic> diagnostics)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                string raw = value.GetString() ?? string.Empty;
                if (PullPrefix(ref raw, "[JumpBack]"))
                {
                    // 前缀只控制回跳，节点名照常解析
                }

                if (PullPrefix(ref raw, "[Anchor]"))
                {
                    // 锚点引用不是节点名，单独登记
                    if (raw.Length > 0)
                    {
                        anchors.Add(raw);
                    }

                    return;
                }

                if (raw.Length > 0)
                {
                    refs.Add(raw);
                }

                break;
            }

            case JsonValueKind.Object:
            {
                // NodeAttr: { "name": "...", "jump_back": true }
                if (value.TryGetProperty("name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String)
                {
                    string raw = nameElement.GetString() ?? string.Empty;
                    if (PullPrefix(ref raw, "[Anchor]"))
                    {
                        if (raw.Length > 0)
                        {
                            anchors.Add(raw);
                        }

                        return;
                    }

                    if (raw.Length > 0)
                    {
                        refs.Add(raw);
                    }
                }
                else
                {
                    diagnostics.Add(new PipelineDiagnostic(
                        PipelineSeverity.Warning, "结构疑问",
                        $"`{field}` 里的对象缺少 name 字段，无法确定引用目标", file, node));
                }

                break;
            }

            case JsonValueKind.Array:
                foreach (JsonElement element in value.EnumerateArray())
                {
                    Collect(element, refs, anchors, field, file, node, diagnostics);
                }

                break;

            default:
                diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "结构错误",
                    $"`{field}` 的元素类型应为字符串或对象，实际是 {value.ValueKind}", file, node));
                break;
        }
    }

    private static bool PullPrefix(ref string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        value = value[prefix.Length..];
        return true;
    }

    /// <summary>不带前缀语义的引用列表（<c>interrupt</c> 这类纯节点名数组）。</summary>
    private List<string> ReadInterruptRefs(JsonElement node, string field, string file, string name)
    {
        if (!node.TryGetProperty(field, out JsonElement value))
        {
            return [];
        }

        return CollectPlainRefs(value, file, name, field);
    }

    private List<string> CollectPlainRefs(JsonElement value, string file, string name, string field)
    {
        var refs = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                string raw = value.GetString() ?? string.Empty;
                _ = PullPrefix(ref raw, "[JumpBack]");
                if (PullPrefix(ref raw, "[Anchor]"))
                {
                    if (raw.Length > 0)
                    {
                        _usedAnchors.Add(raw);
                    }

                    return refs;
                }

                if (raw.Length > 0)
                {
                    refs.Add(raw);
                }

                break;
            }

            case JsonValueKind.Array:
                foreach (JsonElement element in value.EnumerateArray())
                {
                    refs.AddRange(CollectPlainRefs(element, file, name, field));
                }

                break;

            case JsonValueKind.Object:
                // roi / target 的 NodeAttr 形式不多见，但结构上允许 { "name": ... }
                if (value.TryGetProperty("name", out JsonElement inner)
                    && inner.ValueKind == JsonValueKind.String)
                {
                    refs.AddRange(CollectPlainRefs(inner, file, name, field));
                }

                break;
        }

        return refs;
    }

    /// <summary>读取 <c>anchor</c>：字符串、字符串数组、或 v5.7 的 <c>{锚点名: 目标节点}</c> 对象。</summary>
    private List<string> ReadAnchors(JsonElement node, string file, string name)
    {
        if (!node.TryGetProperty("anchor", out JsonElement value))
        {
            return [];
        }

        var anchors = new List<string>();
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                anchors.Add(value.GetString()!);
                break;

            case JsonValueKind.Array:
                anchors.AddRange(value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!));
                break;

            case JsonValueKind.Object:
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    anchors.Add(property.Name);

                    // 值为目标节点名（空字符串表示清除锚点）
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        string target = property.Value.GetString() ?? string.Empty;
                        if (target.Length > 0)
                        {
                            _referenced.Add(target);
                        }
                    }
                }

                break;

            default:
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "结构错误",
                    $"`anchor` 类型应为字符串/数组/对象，实际是 {value.ValueKind}", file, name));
                break;
        }

        foreach (string anchor in anchors)
        {
            _declaredAnchors.Add(anchor);
            if (!_anchorOwners.TryGetValue(anchor, out List<string>? owners))
            {
                owners = [];
                _anchorOwners[anchor] = owners;
            }

            owners.Add(name);
        }

        return anchors;
    }

    /// <summary>
    /// 从「根」出发做可达性遍历，迭代到不动点。
    /// <para>
    /// 根 = 任务入口 ∪ option 覆盖目标 ∪ 覆盖里 next/on_error 指向的节点。
    /// 边 = <c>next</c> / <c>on_error</c>；锚点按「谁引用了这个锚点名 → 所有声明该锚点的节点都算可达」处理
    /// （保守：宁可多留，不可误删）。
    /// </para>
    /// </summary>
    private HashSet<string> ComputeLiveNodes()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Push(string name)
        {
            if (_nodes.ContainsKey(name) && live.Add(name))
            {
                queue.Enqueue(name);
            }
        }

        foreach (string root in TaskEntries)
        {
            Push(root);
        }

        foreach (string target in _overrideTargets)
        {
            Push(target);
        }

        foreach (string reference in _overrideReferences)
        {
            Push(reference);
        }

        while (queue.Count > 0)
        {
            PipelineNodeInfo node = _nodes[queue.Dequeue()];

            // 节点自己没写 next / on_error 时，继承 default_pipeline.json 的 Default 段
            IEnumerable<string> outgoing = node.Outgoing;
            if (node.Next.Count == 0)
            {
                outgoing = outgoing.Concat(Defaults.Next);
            }

            if (node.OnError.Count == 0)
            {
                outgoing = outgoing.Concat(Defaults.OnError);
            }

            foreach (string next in outgoing)
            {
                Push(next);
            }

            // [Anchor]X：X 是运行时解析的锚点名，谁最后设置过它就跳到谁
            IEnumerable<string> usedAnchors = node.UsedAnchors;
            if (node.Next.Count == 0)
            {
                usedAnchors = usedAnchors.Concat(Defaults.NextAnchors);
            }

            if (node.OnError.Count == 0)
            {
                usedAnchors = usedAnchors.Concat(Defaults.OnErrorAnchors);
            }

            foreach (string anchor in usedAnchors)
            {
                if (_anchorOwners.TryGetValue(anchor, out List<string>? owners))
                {
                    foreach (string owner in owners)
                    {
                        Push(owner);
                    }
                }
            }
        }

        return live;
    }

    private void CheckReferences()
    {
        foreach (PipelineNodeInfo node in _nodes.Values)
        {
            foreach (string reference in node.Next.Concat(node.OnError))
            {
                if (!_nodes.ContainsKey(reference))
                {
                    _diagnostics.Add(new PipelineDiagnostic(
                        PipelineSeverity.Error, "引用悬空",
                        $"next/on_error 引用了不存在的节点「{reference}」", node.File, node.Name));
                }
            }
        }

        // default_pipeline.json 的 Default 段也算引用来源，它悬空会波及每一个节点
        if (!Defaults.IsEmpty)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "默认属性",
                $"default_pipeline.json 的 Default 段对所有节点生效：{Defaults.Summary}", Defaults.Source));

            foreach (string reference in Defaults.Next.Concat(Defaults.OnError))
            {
                _referenced.Add(reference);
                if (!_nodes.ContainsKey(reference))
                {
                    _diagnostics.Add(new PipelineDiagnostic(
                        PipelineSeverity.Error, "引用悬空",
                        $"default_pipeline.json 的 Default 段引用了不存在的节点「{reference}」"
                        + "（该错误会波及所有没有显式写 next/on_error 的节点）",
                        Defaults.Source, "Default"));
                }
            }
        }
    }

    private void ReportUnusedAnchors()
    {
        foreach (string anchor in _usedAnchors.Where(a => !_declaredAnchors.Contains(a)).OrderBy(a => a, StringComparer.Ordinal))
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Warning, "锚点悬空",
                $"引用了从未声明过的锚点「{anchor}」（运行时会跳过该分支）"));
        }

        foreach (string anchor in _declaredAnchors.Where(a => !_usedAnchors.Contains(a)).OrderBy(a => a, StringComparer.Ordinal))
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "锚点未使用", $"声明了锚点「{anchor}」但没有任何节点引用"));
        }
    }

    private void CheckTemplateAndModel()
    {
        if (ImageDirectory is null || !Directory.Exists(ImageDirectory))
        {
            if (_templates.Count > 0)
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Info, "跳过检查",
                    $"image 目录不存在（{ImageDirectory ?? "(未提供)"}），跳过 {_templates.Count} 个模板图的存在性检查"));
            }

            return;
        }

        foreach (string template in _templates.OrderBy(t => t, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(ImageDirectory, template)))
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Error, "模板图缺失", $"找不到模板图 image/{template}"));
            }
        }
    }

    private void ReportOrphans()
    {
        // 1) 任务入口必须存在，否则任务永远跑不起来
        foreach (string missing in MissingTaskEntries)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Error, "任务入口缺失",
                $"tasks 里声明了入口「{missing}」，但 pipeline 中不存在该节点——这个任务无法启动"));
        }

        int entryCount = TaskEntries.Count - MissingTaskEntries.Count;
        int overrideMissing = _overrideTargets.Count(t => !_nodes.ContainsKey(t));
        _diagnostics.Add(new PipelineDiagnostic(
            PipelineSeverity.Info, "节点统计",
            $"共 {_nodes.Count} 个节点：任务入口 {entryCount} 个、从入口可达 {LiveNodes.Count} 个、"
            + $"option 覆盖 {_overrideTargets.Count} 个、不可达 {DeadNodes.Count} 个"));

        // pipeline_override 可以就地新建节点，所以「覆盖目标不在 pipeline 里」不算错误
        if (overrideMissing > 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "覆盖目标",
                $"{overrideMissing} 个 option 覆盖目标不在 pipeline 里（框架会在运行时就地创建，属正常用法）"));
        }

        // 命名规范（P3.1）：只统计不修改
        if (NamingIssues.Count > 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "命名待整理",
                $"{NamingIssues.Count} 个节点命名不规范（编辑器自动生成 / _copy 拖拽副本 / test 前缀），"
                + $"占全部节点的 {NamingIssues.Count * 100.0 / _nodes.Count:F1}%："
                + string.Join("、", NamingIssues.Take(12))
                + (NamingIssues.Count > 12 ? " …" : string.Empty)));
        }

        // 2) 整份文件都是死节点 → 可作为「整体剔除」的候选
        foreach ((string file, IReadOnlyList<string> nodes) in DeadNodesByFile)
        {
            bool wholeFile = _nodes.Values.Count(n => string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase)) == nodes.Count;
            if (!wholeFile)
            {
                continue;
            }

            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "整文件可剔除",
                $"该文件的 {nodes.Count} 个节点全是死节点（既无引用也非任务入口）：{string.Join("、", nodes.Take(10))}"
                + (nodes.Count > 10 ? " …" : string.Empty),
                file));
        }
    }

    private void ReportCustomHooks()
    {
        if (_customActions.Count > 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "agent 依赖",
                $"{_customActions.Count} 个自定义动作需要 agent 实现：{string.Join("、", _customActions.OrderBy(a => a, StringComparer.Ordinal))}"));
        }

        if (_customRecognitions.Count > 0)
        {
            _diagnostics.Add(new PipelineDiagnostic(
                PipelineSeverity.Info, "agent 依赖",
                $"{_customRecognitions.Count} 个自定义识别需要 agent 实现：{string.Join("、", _customRecognitions.OrderBy(a => a, StringComparer.Ordinal))}"));
        }
    }

    private void ReportCleanupCandidates(IReadOnlyCollection<string> cleanupCandidates)
    {
        foreach (string candidate in cleanupCandidates)
        {
            List<string> hits = [.. _files.Where(f =>
                string.Equals(f, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(f), candidate, StringComparison.OrdinalIgnoreCase))];

            foreach (string hit in hits)
            {
                _diagnostics.Add(new PipelineDiagnostic(
                    PipelineSeverity.Info, "待清理",
                    $"该文件被标记为调试/测试残留，可纳入 P3.1 整理清单", hit));
            }
        }
    }

    private static string NormalizeAssetPath(string path) => path.Replace('\\', '/').TrimStart('/');
}
