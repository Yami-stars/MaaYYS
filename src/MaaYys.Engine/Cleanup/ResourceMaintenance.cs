using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaaYys.Engine.Cleanup;

/// <summary>上一次整理留下了什么（<c>config/resource-state.json</c>）。</summary>
public sealed class ResourceState
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>资源文件指纹（文件数 + 总字节）。版本号没变但资源被换过时靠它识别。</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; } = string.Empty;

    [JsonPropertyName("pruned_at")]
    public DateTimeOffset PrunedAt { get; set; } = DateTimeOffset.Now;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;
}

/// <summary>一次「资源更新后整理」的结果。</summary>
public sealed record MaintenanceOutcome(
    bool Ran,
    string Reason,
    string Version,
    PruneOutcome? Prune,
    IReadOnlyList<string> ResourceSets)
{
    public string Summary => Ran
        ? Prune?.Summary ?? "资源已在最新版本，未做整理"
        : Reason;
}

/// <summary>
/// 资源维护：<b>只在资源版本更新时</b>清掉上一版留下、这一版再也走不到的东西
/// （不可达节点、未引用模板图/模型、<c>*.bak</c> 等）。
/// <para>
/// 为什么挂在这里而不是让用户点按钮：这些东西是「版本迭代的副产物」，
/// 用户不该知道、也不该决定要不要删——新版资源包一到位，它们就一定是垃圾。
/// 让用户手动点，等于把一个必然正确的动作变成一道可能选错的选择题。
/// </para>
/// <para>
/// 判定门槛故意设得很高：<b>必须在所有资源集里都不可达</b>才算数。
/// 某个节点在官服走不到、在 bilibili 服却有人用，它就绝不能删。
/// </para>
/// </summary>
public static class ResourceMaintenance
{
    private const string StateFileName = "resource-state.json";

    /// <summary>状态文件默认位置：程序目录 <c>config/</c>。</summary>
    public static string DefaultStatePath => Path.Combine(AppContext.BaseDirectory, "config", StateFileName);

    /// <summary>日志默认位置（GUI 无控制台，删了什么必须留痕）。</summary>
    public static string DefaultLogPath => Path.Combine(AppContext.BaseDirectory, "debug", "resource-prune.log");

    /// <summary>
    /// 后台跑一次（启动时调用）：发现资源版本变化就整理，失败只写日志、绝不弹窗打断启动。
    /// </summary>
    public static void RunInBackground(string? interfacePath, string? statePath = null, string? logPath = null)
    {
        if (string.IsNullOrWhiteSpace(interfacePath) || !File.Exists(interfacePath))
        {
            return;
        }

        string path = interfacePath;
        _ = Task.Run(() =>
        {
            try
            {
                MaintenanceOutcome outcome = RunIfNeeded(path, statePath);
                if (outcome.Ran)
                {
                    Log(logPath, $"资源更新 {outcome.Version}（{string.Join("/", outcome.ResourceSets)}）：{outcome.Summary}");
                    foreach (string line in (outcome.Prune?.Removed ?? []).Take(50))
                    {
                        Log(logPath, $"  - {line}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log(logPath, $"整理失败：{ex}");
            }
        });
    }

    /// <summary>版本/指纹没变就直接返回，变了才扫描 + 整理 + 写状态。</summary>
    public static MaintenanceOutcome RunIfNeeded(
        string interfacePath,
        string? statePath = null,
        bool force = false,
        ResourceState? stateOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfacePath);

        string version = ReadVersion(interfacePath);
        string root = Path.GetDirectoryName(Path.GetFullPath(interfacePath))!;
        string fingerprint = ComputeFingerprint(root);

        // 状态文件跟着 interface.json 走（发布后两者同目录，开发期也不会写到别处去）
        string path = statePath
                      ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(interfacePath))!, "config", StateFileName);
        ResourceState? state = stateOverride ?? LoadState(path);

        if (!force && state is not null
            && string.Equals(state.Version, version, StringComparison.Ordinal)
            && string.Equals(state.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            return new MaintenanceOutcome(false, $"资源未更新（{version}），无需整理", version, null, []);
        }

        // 删掉死节点会连带让「只被死节点引用的模板图」变成未引用，所以必须反复扫到不动点。
        // 正常情况下 2 轮收敛（第 1 轮删节点，第 2 轮删随之变孤儿的美术资源）。
        const int MaxPasses = 4;
        List<PruneOutcome> passes = [];
        IReadOnlyList<string> setNames = [];

        for (int i = 0; i < MaxPasses; i++)
        {
            IReadOnlyList<ResourceAuditResult> audits = AuditAllSets(interfacePath);
            if (audits.Count == 0)
            {
                return new MaintenanceOutcome(false, "没有可用的资源集，跳过整理", version, null, []);
            }

            if (setNames.Count == 0)
            {
                setNames = [.. audits.Select(a => a.ResourceSetName)];
            }

            PruneOutcome pruned = ResourcePruner.Prune(Merge(audits));
            passes.Add(pruned);

            if (!pruned.Changed)
            {
                break;
            }
        }

        PruneOutcome combined = Combine(passes);

        // 指纹在整理之后再取，这样下次启动不会因为「文件变少了」又触发一轮
        SaveState(path, new ResourceState
        {
            Version = version,
            Fingerprint = ComputeFingerprint(root),
            PrunedAt = DateTimeOffset.Now,
            // 这一轮什么都没删时保留上一次的结论，界面上不会显示成「从没整理过」
            Summary = combined.Changed
                ? combined.Summary
                : state?.Summary ?? "资源已是当前版本，未发现需要清理的过期文件",
        });

        return new MaintenanceOutcome(true, combined.Summary, version, combined, setNames);
    }

    /// <summary>把多轮整理合并成一条结论（删除数、节点数、释放空间累加，错误与清单去重后拼接）。</summary>
    private static PruneOutcome Combine(IReadOnlyList<PruneOutcome> passes)
    {
        if (passes.Count == 0)
        {
            return new PruneOutcome(true, 0, 0, 0, 0, [], []);
        }

        return new PruneOutcome(
            passes.All(p => p.Success),
            passes.Sum(p => p.RemovedFiles),
            passes.Sum(p => p.RewrittenFiles),
            passes.Sum(p => p.RemovedNodes),
            passes.Sum(p => p.FreedBytes),
            [.. passes.SelectMany(p => p.Removed).Distinct(StringComparer.Ordinal)],
            [.. passes.SelectMany(p => p.Errors).Distinct(StringComparer.Ordinal)]);
    }

    // ---- 多资源集取交集 ----

    private static IReadOnlyList<ResourceAuditResult> AuditAllSets(string interfacePath)
    {
        InterfaceSets sets = ReadResourceSetNames(interfacePath);
        List<ResourceAuditResult> results = [];

        string? configDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(interfacePath))!, "config");
        if (!Directory.Exists(configDirectory))
        {
            configDirectory = null;
        }

        foreach (string name in sets.Names)
        {
            try
            {
                results.Add(ResourceAudit.Scan(new AuditRequest
                {
                    InterfacePath = interfacePath,
                    ResourceSetName = name,
                    IncludeAssets = true,
                    ConfigDirectory = configDirectory,
                }));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
            {
                // 单个资源集扫不动（包缺文件等）就跳过它，但保留其余集合的结论
            }
        }

        return results;
    }

    /// <summary>
    /// 合并多个资源集的审计结论：只有<b>每个资源集都判成可清理</b>的项、且节点在每套里都不可达，才留下来。
    /// 处理策略取最保守的那个（任一资源集说「保护」，就保护）。
    /// </summary>
    public static ResourceAuditResult Merge(IReadOnlyList<ResourceAuditResult> audits)
    {
        ArgumentNullException.ThrowIfNull(audits);
        if (audits.Count == 0)
        {
            throw new ArgumentException("至少需要一个审计结果", nameof(audits));
        }

        ResourceAuditResult first = audits[0];
        if (audits.Count == 1)
        {
            return first;
        }

        // key = 类别 + 相对路径。所有集合都有、且都可清理，才进入候选。
        List<CleanupItem> merged = [];

        foreach (CleanupItem candidate in first.Items.Where(i => i.CanClean))
        {
            bool everywhere = true;
            List<string> nodes = [.. candidate.Nodes];

            for (int i = 1; i < audits.Count && everywhere; i++)
            {
                CleanupItem? same = audits[i].Items.FirstOrDefault(
                    x => x.Category == candidate.Category && x.RelativePath == candidate.RelativePath);

                if (same is null || !same.CanClean)
                {
                    everywhere = false;
                    break;
                }

                nodes = [.. nodes.Intersect(same.Nodes, StringComparer.Ordinal)];
            }

            if (!everywhere)
            {
                continue;
            }

            // 节点级清理：交集为空说明每套走不到的具体节点不一样，这一项没法定向删，放弃
            if (candidate.Category == CleanupCategory.DeadNodes && nodes.Count == 0)
            {
                continue;
            }

            merged.Add(new CleanupItem
            {
                Id = candidate.Id,
                Category = candidate.Category,
                Risk = candidate.Risk,
                RelativePath = candidate.RelativePath,
                Nodes = nodes,
                SizeBytes = candidate.SizeBytes,
                Reason = candidate.Reason,
            });
        }

        return new ResourceAuditResult
        {
            InterfacePath = first.InterfacePath,
            ResourceRoot = first.ResourceRoot,
            ResourceSetName = string.Join(" + ", audits.Select(a => a.ResourceSetName)),
            BundlePaths = [.. audits.SelectMany(a => a.BundlePaths).Distinct(StringComparer.OrdinalIgnoreCase)],
            ConfigDirectory = first.ConfigDirectory,
            Items = merged,
            HiddenTasks = first.HiddenTasks,
            Problems = [.. audits.SelectMany(a => a.Problems)],
            TotalNodes = first.TotalNodes,
            LiveNodes = first.LiveNodes,
            DeadNodes = first.DeadNodes,
            ProtectedByHiddenTasks = first.ProtectedByHiddenTasks,
            ProtectedByConvention = first.ProtectedByConvention,
            TemplateTotal = first.TemplateTotal,
            TemplateUnused = first.TemplateUnused,
            ModelTotal = first.ModelTotal,
            ModelUnused = first.ModelUnused,
            Duration = TimeSpan.FromTicks(audits.Sum(a => a.Duration.Ticks)),
        };
    }

    // ---- 版本与指纹 ----

    /// <summary>读 <c>interface.json</c> 的 <c>version</c>；读不到就返回空串（空串会让下次启动再试一遍）。</summary>
    public static string ReadVersion(string interfacePath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(interfacePath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            return document.RootElement.TryGetProperty("version", out JsonElement value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed record InterfaceSets(IReadOnlyList<string> Names);

    private static InterfaceSets ReadResourceSetNames(string interfacePath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(interfacePath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (document.RootElement.TryGetProperty("resource", out JsonElement resources)
                && resources.ValueKind == JsonValueKind.Array)
            {
                List<string> names = [];
                foreach (JsonElement item in resources.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("name", out JsonElement name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        names.Add(name.GetString()!);
                    }
                }

                if (names.Count > 0)
                {
                    return new InterfaceSets(names);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 落到空集合
        }

        return new InterfaceSets([]);
    }

    /// <summary>资源指纹：<c>resource_pack/&lt;bundle&gt;/{pipeline,image,model}</c> 的文件数与总字节。</summary>
    public static string ComputeFingerprint(string resourceRoot)
    {
        string pack = Path.Combine(resourceRoot, "resource_pack");
        if (!Directory.Exists(pack))
        {
            pack = resourceRoot;
        }

        int files = 0;
        long bytes = 0;

        try
        {
            foreach (string bundle in Directory.EnumerateDirectories(pack))
            {
                foreach (string folder in new[] { "pipeline", "image", "model" })
                {
                    string directory = Path.Combine(bundle, folder);
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        files++;
                        try
                        {
                            bytes += new FileInfo(file).Length;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 单个文件读不到就不计体积
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable";
        }

        return $"{files}:{bytes}";
    }

    private static ResourceState? LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ResourceState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveState(string path, ResourceState state)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(state, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }),
                new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 状态写不进去最多下次再整理一遍，不影响主流程
        }
    }

    private static void Log(string? logPath, string message)
    {
        try
        {
            string path = logPath ?? DefaultLogPath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写失败不影响主流程
        }
    }
}
