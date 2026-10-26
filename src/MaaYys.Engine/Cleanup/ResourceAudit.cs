using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaaYys.Engine.Config;
using MaaYys.Engine.Resources;

namespace MaaYys.Engine.Cleanup;

/// <summary>
/// 资源审计：把「以后版本更新可能留下来的冗余」找出来，并且<b>分清哪些能删、哪些不能删</b>。
/// <para>
/// 判定分三层（这是本模块最重要的设计，别简化成「不可达就删」）：
/// </para>
/// <list type="number">
///   <item><b>受保护 - 隐藏任务</b>：<c>tasks/</c> 里存在但没被 <c>interface.json</c> import 的任务，
///         其入口与后续节点全部算「活的」。下个版本活动一开就能用，删了就得从上游重新找回来。</item>
///   <item><b>受保护 - 约定保留</b>：<c>活动/</c> 下的每月轮换包（一期只接一个）、
///         <c>debug.json</c>（承载 <c>default_pipeline.json</c> 的全局兜底链）。</item>
///   <item><b>可清理</b>：剩下真正走不到的节点。名字像 <c>N-1766…</c>/<c>_copy1</c>/<c>test*</c>
///         的判为「可清理」，其余判为「需确认」。</item>
/// </list>
/// <para>
/// 审计本身<b>只读</b>：所有 pipeline 合并都在临时目录里做，不会碰原始文件。
/// 真正的删除由 <see cref="ResourcePruner"/> 执行（直接删，不进回收区），
/// 触发时机由 <see cref="ResourceMaintenance"/> 控制（资源版本更新时）。
/// </para>
/// </summary>
public static class ResourceAudit
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static ResourceAuditResult Scan(AuditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        Stopwatch stopwatch = Stopwatch.StartNew();
        List<string> problems = [];

        InterfaceConfig config;
        try
        {
            config = InterfaceConfig.Load(request.InterfacePath, problems);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return new ResourceAuditResult
            {
                InterfacePath = request.InterfacePath,
                ResourceRoot = Path.GetDirectoryName(Path.GetFullPath(request.InterfacePath)) ?? string.Empty,
                ResourceSetName = request.ResourceSetName ?? string.Empty,
                Problems = [$"interface.json 读取失败：{ex.Message}"],
                Duration = stopwatch.Elapsed,
            };
        }

        ResourceChain? chain = request.ResourceSetName is null
            ? ResourceChainResolver.ResolveAll(config).FirstOrDefault(c => c.IsUsable)
            : TryResolve(config, request.ResourceSetName, problems);

        if (chain is null)
        {
            return new ResourceAuditResult
            {
                InterfacePath = request.InterfacePath,
                ResourceRoot = config.BaseDirectory,
                ResourceSetName = request.ResourceSetName ?? string.Empty,
                Problems = [.. problems, "找不到可用的资源集（interface.json 的 resource[].path 全部不存在）"],
                Duration = stopwatch.Elapsed,
            };
        }

        // ---- 1. 隐藏任务：tasks/ 里有、import 里没有 ----
        List<HiddenTaskInfo> hiddenTasks = DiscoverHiddenTasks(config, out ResourceReferenceScope hiddenScope);

        // ---- 2. 引用范围：可见任务 ∪ 隐藏任务（隐藏任务的节点因此不会被判死） ----
        ResourceReferenceScope visibleScope = ResourceReferenceScope.FromConfig(config);
        ResourceReferenceScope fullScope = Union(visibleScope, hiddenScope);

        // ---- 3. 合并视图 + 两遍可达性 ----
        PipelineDefaults defaults = PipelineDefaults.None;
        foreach (string bundle in chain.BundlePaths)
        {
            defaults = defaults.Then(PipelineDefaults.Load(Path.Combine(bundle, "default_pipeline.json")));
        }

        string imageDirectory = Path.Combine(chain.BundlePaths[0], "image");

        PipelineInventory full;
        PipelineInventory visibleOnly;
        using (MergedPipelineStage stage = PipelineBundleMerger.Stage(chain.BundlePaths))
        {
            full = PipelineInventory.Scan(stage.PipelineDirectory, imageDirectory, null, fullScope, defaults);
            visibleOnly = hiddenTasks.Count == 0
                ? full
                : PipelineInventory.Scan(stage.PipelineDirectory, imageDirectory, null, visibleScope, defaults);

            // ---- 4. 死节点分组 ----
            HashSet<string> protectedByHidden = hiddenTasks.Count == 0
                ? []
                : [.. visibleOnly.DeadNodes.Where(n => !full.DeadNodes.Contains(n))];

            List<CleanupItem> items = BuildNodeItems(full, protectedByHidden);
            int protectedNodes = items.Where(i => !i.CanClean).Sum(i => i.Nodes.Count);

            // ---- 5. 未引用资源（模板图 / 模型） ----
            int templateTotal = 0;
            int templateUnused = 0;
            int modelTotal = 0;
            int modelUnused = 0;
            if (request.IncludeAssets)
            {
                string pipelineText = ReadPipelineText(chain.BundlePaths);
                HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
                foreach (string template in full.Templates)
                {
                    referenced.Add(Normalize(template));
                }

                foreach (string model in full.Models)
                {
                    referenced.Add(Normalize(model));
                }

                (templateTotal, templateUnused) = CollectUnusedAssets(
                    chain.BundlePaths, "image", CleanupCategory.UnusedTemplate, referenced, pipelineText,
                    "模板图", [".png", ".jpg", ".jpeg", ".bmp"], _ => null, items);

                // 模型与模板图的根本区别：**模型不可再生**。模板图能重截，模型得重新训练/找回原文件。
                // 所以 model/ 下的 .onnx 一律受保护，哪怕当前没有节点引用（庭院皮肤 v5、旧版 special-icon
                // 都是这个情况，以后换回来还要用）。ocr/ 另外还有「框架按固定路径隐式加载」这层理由。
                (modelTotal, modelUnused) = CollectUnusedAssets(
                    chain.BundlePaths, "model", CleanupCategory.UnusedModel, referenced, pipelineText,
                    "模型", [".onnx"], ModelProtectionReason, items);
            }

            // ---- 6. 配置备份 ----
            string configDirectory = request.ConfigDirectory
                                     ?? Path.Combine(AppContext.BaseDirectory, "config");
            CollectConfigBackups(configDirectory, items);

            AddProblems(full, problems);

            return new ResourceAuditResult
            {
                InterfacePath = request.InterfacePath,
                ResourceRoot = config.BaseDirectory,
                ResourceSetName = chain.Name,
                BundlePaths = chain.BundlePaths,
                Items = [.. items
                    .OrderBy(i => i.Category)
                    .ThenBy(i => i.Risk)
                    .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)],
                HiddenTasks = hiddenTasks,
                ConfigDirectory = configDirectory,
                Problems = [.. problems.Distinct(StringComparer.Ordinal)],
                TotalNodes = full.Nodes.Count,
                LiveNodes = full.LiveNodes.Count,
                DeadNodes = full.DeadNodes.Count,
                ProtectedByHiddenTasks = protectedByHidden.Count,
                ProtectedByConvention = protectedNodes - protectedByHidden.Count,
                TemplateTotal = templateTotal,
                TemplateUnused = templateUnused,
                ModelTotal = modelTotal,
                ModelUnused = modelUnused,
                Duration = stopwatch.Elapsed,
            };
        }
    }

    private static ResourceChain? TryResolve(InterfaceConfig config, string name, List<string> problems)
    {
        try
        {
            return ResourceChainResolver.Resolve(config, name);
        }
        catch (InvalidOperationException ex)
        {
            problems.Add(ex.Message);
            return null;
        }
    }

    private static ResourceReferenceScope Union(ResourceReferenceScope a, ResourceReferenceScope b)
        => new(
            [.. a.TaskEntries.Concat(b.TaskEntries).Distinct(StringComparer.Ordinal)],
            [.. a.OverrideTargets.Concat(b.OverrideTargets).Distinct(StringComparer.Ordinal)],
            [.. a.OverrideReferences.Concat(b.OverrideReferences).Distinct(StringComparer.Ordinal)]);

    // ---- 死节点 → 清理项 ----

    private static List<CleanupItem> BuildNodeItems(PipelineInventory inventory, HashSet<string> protectedByHidden)
    {
        List<CleanupItem> items = [];

        foreach ((string file, IReadOnlyList<string> deadNodes) in inventory.DeadNodesByFile)
        {
            int totalInFile = inventory.Nodes.Values.Count(n => string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase));
            bool wholeFile = totalInFile == deadNodes.Count;
            CleanupCategory category = wholeFile ? CleanupCategory.DeadFile : CleanupCategory.DeadNodes;

            (CleanupRisk risk, string reason) = ClassifyDeadGroup(file, deadNodes, protectedByHidden);

            items.Add(new CleanupItem
            {
                Id = $"{(wholeFile ? "file" : "nodes")}:{file}",
                Category = category,
                Risk = risk,
                RelativePath = "pipeline/" + file,
                Nodes = deadNodes,
                SizeBytes = wholeFile ? SafeFileSize(inventory, file) : 0,
                Reason = reason,
            });
        }

        return items;
    }

    /// <summary>
    /// 给一组死节点定风险。三条保护规则按优先级：
    /// 隐藏任务 → 活动轮换/debug 兜底 → 名字像残留。
    /// </summary>
    private static (CleanupRisk Risk, string Reason) ClassifyDeadGroup(
        string file, IReadOnlyList<string> nodes, HashSet<string> protectedByHidden)
    {
        int hiddenCount = nodes.Count(protectedByHidden.Contains);
        if (hiddenCount == nodes.Count)
        {
            return (CleanupRisk.Protected,
                "属于未接线的任务（tasks/ 里有定义、interface.json 未 import）。下个版本活动一开即可启用，保留");
        }

        if (hiddenCount > 0)
        {
            return (CleanupRisk.Review,
                $"其中 {hiddenCount} 个节点属于未接线的任务，需逐个确认后再清理");
        }

        if (IsConventionProtected(file))
        {
            return (CleanupRisk.Protected,
                file.EndsWith("debug.json", StringComparison.OrdinalIgnoreCase)
                    ? "debug.json 承载 default_pipeline.json 的全局兜底链，整份保留"
                    : "活动轮换包：每月一期只接一个，其余留着下期直接切换，保留");
        }

        // 走到这里的节点都已经「从任何任务入口/覆盖目标都到不了」——
        // 删它不可能断掉活链路（P3.1 已核对 0 悬空引用、框架侧 1511 节点一致）。
        // 所以「可清理」；真正的兜底是 ResourcePruner 只认 CanClean 项 + 强制拦禁止名单。
        bool tempNamed = nodes.All(PipelineInventory.IsPoorlyNamed);
        return tempNamed
            ? (CleanupRisk.Safe, $"{nodes.Count} 个节点全部不可达，且命名是编辑器残留（N-时间戳 / _copy / test*）")
            : (CleanupRisk.Safe, $"{nodes.Count} 个节点从任何任务入口都走不到（可达性遍历 + 锚点解析）");
    }

    /// <summary>「约定保留」：活动目录下的轮换包、全局兜底链文件。</summary>
    private static bool IsConventionProtected(string relativeFile)
    {
        if (relativeFile.EndsWith("debug.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] segments = relativeFile.Split('/', '\\');
        return segments.Any(s => string.Equals(s, "活动", StringComparison.Ordinal));
    }

    private static long SafeFileSize(PipelineInventory inventory, string relativeFile)
    {
        try
        {
            string full = Path.Combine(inventory.PipelineDirectory, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? new FileInfo(full).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // ---- 未引用资源 ----

    /// <summary>
    /// 收集未被引用的模板图/模型。
    /// <para>
    /// 判定用「双保险」：既不在任何节点的 <c>template</c>/<c>model</c> 里出现，
    /// 文件名也不在任何 pipeline JSON 的<b>原文</b>里出现。后者专门用来兜住
    /// <c>pipeline_override</c> 里写死的资源路径（那些不会进入静态节点表）。
    /// </para>
    /// </summary>
    private static (int Total, int Unused) CollectUnusedAssets(
        IReadOnlyList<string> bundlePaths,
        string assetFolder,
        CleanupCategory category,
        HashSet<string> referenced,
        string pipelineText,
        string label,
        IReadOnlyList<string> extensions,
        Func<string, string?> protectionReason,
        List<CleanupItem> items)
    {
        int total = 0;
        int unused = 0;

        foreach (string bundle in bundlePaths)
        {
            string directory = Path.Combine(bundle, assetFolder);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!extensions.Any(e => relative.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                total++;

                // 受保护的资源一律只做说明、不提供清理（返回 null 表示不保护）
                if (protectionReason(relative) is { } reason)
                {
                    items.Add(new CleanupItem
                    {
                        Id = $"asset:{assetFolder}:{Path.GetFileName(bundle)}:{relative}",
                        Category = category,
                        Risk = CleanupRisk.Protected,
                        RelativePath = $"{assetFolder}/{relative}",
                        SizeBytes = SafeFileSize(file),
                        Reason = reason,
                    });
                    continue;
                }

                if (referenced.Contains(relative)
                    || pipelineText.Contains(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                unused++;
                items.Add(new CleanupItem
                {
                    Id = $"asset:{assetFolder}:{Path.GetFileName(bundle)}:{relative}",
                    Category = category,
                    Risk = CleanupRisk.Review,
                    RelativePath = $"{assetFolder}/{relative}",
                    SizeBytes = SafeFileSize(file),
                    Reason = $"没有任何节点引用这张{label}（含隐藏任务与 override 原文检索），确认后可清理",
                });
            }
        }

        return (total, unused);
    }

    /// <summary>
    /// 模型的保护理由（返回 null 表示不保护）。
    /// <c>model/ocr/*</c>：框架内置 OCR 按固定路径加载，pipeline 里根本不会出现引用，删了 OCR 全废。
    /// 其余 <c>.onnx</c>：<b>模型不可再生</b>——模板图能重截，模型只能重新训练或找回原文件，
    /// 所以哪怕当前没有节点引用也要保留（庭院皮肤 v5、旧版 special-icon 就是这种情况）。
    /// </summary>
    private static string ModelProtectionReason(string relative)
        => relative.StartsWith("ocr/", StringComparison.OrdinalIgnoreCase)
            ? "框架内置 OCR 直接按固定路径加载（pipeline 里不会出现引用），必须保留"
            : "模型不可再生（不像模板图能重截），即便当前无引用也保留，供以后换版本/换皮肤使用";

    private static long SafeFileSize(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string ReadPipelineText(IReadOnlyList<string> bundlePaths)
    {
        System.Text.StringBuilder builder = new();
        foreach (string bundle in bundlePaths)
        {
            string directory = Path.Combine(bundle, "pipeline");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    builder.AppendLine(File.ReadAllText(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 读不到就跳过（该文件本身会在别处报解析问题）
                }
            }
        }

        return builder.ToString();
    }

    // ---- 配置备份 ----

    private static void CollectConfigBackups(string configDirectory, List<CleanupItem> items)
    {
        if (!Directory.Exists(configDirectory))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(configDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            bool isBackup = name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                            || name.Contains(".bad-", StringComparison.OrdinalIgnoreCase);
            if (!isBackup)
            {
                continue;
            }

            long size = 0;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 忽略
            }

            items.Add(new CleanupItem
            {
                Id = $"backup:{name}",
                Category = CleanupCategory.ConfigBackup,
                Risk = CleanupRisk.Safe,
                RelativePath = name,
                SizeBytes = size,
                Reason = "配置每次保存/损坏时自动生成的上一版备份，属运行期产物",
            });
        }
    }

    private static void AddProblems(PipelineInventory inventory, List<string> problems)
    {
        foreach (string failure in inventory.ParseFailures)
        {
            problems.Add($"pipeline 文件解析失败：{failure}");
        }

        foreach (PipelineDiagnostic diagnostic in inventory.Errors)
        {
            problems.Add(diagnostic.ToString());
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    // ---- 隐藏任务发现 ----

    private static List<HiddenTaskInfo> DiscoverHiddenTasks(InterfaceConfig config, out ResourceReferenceScope scope)
    {
        List<HiddenTaskInfo> result = [];
        HashSet<string> entries = new(StringComparer.Ordinal);
        HashSet<string> targets = new(StringComparer.Ordinal);
        HashSet<string> references = new(StringComparer.Ordinal);

        string tasksDirectory = Path.Combine(config.BaseDirectory, "tasks");
        if (!Directory.Exists(tasksDirectory))
        {
            scope = ResourceReferenceScope.Empty;
            return result;
        }

        HashSet<string> imported = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in config.Import ?? [])
        {
            imported.Add(Normalize(raw.TrimStart('.', '/')));
        }

        string[] files = Directory.GetFiles(tasksDirectory, "*.json", SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string relative = "tasks/" + Path.GetFileName(file);
            if (imported.Contains(relative))
            {
                continue;
            }

            TaskFileDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<TaskFileDto>(File.ReadAllText(file), ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (dto?.Tasks is null)
            {
                continue;
            }

            foreach (TaskConfig task in dto.Tasks)
            {
                result.Add(new HiddenTaskInfo
                {
                    Name = task.Name,
                    Label = task.DisplayName,
                    Entry = task.Entry,
                    File = relative,
                    OptionCount = task.Option?.Count ?? 0,
                });

                if (!string.IsNullOrWhiteSpace(task.Entry))
                {
                    entries.Add(task.Entry);
                }

                if (task.PipelineOverride is { } taskOverride)
                {
                    ResourceReferenceScope.CollectOverride(taskOverride, targets, references);
                }
            }

            if (dto.Options is null)
            {
                continue;
            }

            foreach (OptionConfig option in dto.Options.Values)
            {
                if (option.PipelineOverride is { } optionOverride)
                {
                    ResourceReferenceScope.CollectOverride(optionOverride, targets, references);
                }

                foreach (CaseConfig optionCase in option.Cases ?? [])
                {
                    if (optionCase.PipelineOverride is { } caseOverride)
                    {
                        ResourceReferenceScope.CollectOverride(caseOverride, targets, references);
                    }
                }
            }
        }

        scope = new ResourceReferenceScope([.. entries], [.. targets], [.. references]);
        return result;
    }

    /// <summary><c>tasks/*.json</c> 的最小 DTO（只关心 entry 与 override）。</summary>
    private sealed class TaskFileDto
    {
        [JsonPropertyName("task")]
        public List<TaskConfig>? Tasks { get; set; }

        [JsonPropertyName("option")]
        public Dictionary<string, OptionConfig>? Options { get; set; }
    }
}
