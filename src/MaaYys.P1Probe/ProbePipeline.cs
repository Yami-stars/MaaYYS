using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Resources;

namespace MaaYys.P1Probe;

/// <summary>
/// pipeline 静态自检（P3.1 验收）。
/// 用法：<c>pipeline [--root 目录] [--set 资源集名] [--layer] [--verify] [--quiet]</c>
///
/// <c>--layer</c> 对资源链上的**每一个** bundle 分别扫描（base 与渠道层单独看）；
/// 缺省只扫<b>合并后</b>的全部 pipeline（把链上每个包的 pipeline 目录都算进来，后加载的覆盖同名节点）。
/// <c>--verify</c> 额外用 MaaFramework 真实加载一次，逐个节点调 <c>MaaResourceGetNodeData</c>
/// 确认框架确实认到了这些节点（这是「JSON 全部可解析」的框架侧证据）。
/// </summary>
internal static class ProbePipeline
{
    public static async Task<int> RunAsync(string[] args)
    {
        string root = ReadOption(args, "--root")
                      ?? Environment.GetEnvironmentVariable("MAAYYS_RESOURCE_ROOT")
                      ?? Directory.GetCurrentDirectory();

        string? interfacePath = LocateInterfaceJson(root);
        if (interfacePath is null)
        {
            Console.WriteLine($"[失败] 在 {root} 及其上级目录中找不到 interface.json");
            return 1;
        }

        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(interfacePath, errors);
        Console.WriteLine($"[清单] {interfacePath}");

        string? setName = ReadOption(args, "--set");
        ResourceChain? chain = setName is null
            ? ResourceChainResolver.ResolveAll(config).FirstOrDefault(c => c.IsUsable)
            : ResourceChainResolver.Resolve(config, setName);

        if (chain is null)
        {
            Console.WriteLine($"[失败] 找不到可用资源集{(setName is null ? string.Empty : $"「{setName}」")}");
            return 2;
        }

        Console.WriteLine($"[资源集] {chain.Name} → {chain.BundlePaths.Count} 个包");
        foreach (string bundle in chain.BundlePaths)
        {
            Console.WriteLine($"    + {bundle}");
        }

        Console.WriteLine();
        bool perLayer = args.Contains("--layer");
        PipelineInventory merged;

        // default_pipeline.json：按链顺序合并（后加载 bundle 的同名字段覆盖先加载的）
        List<string> defaultWarnings = [];
        PipelineDefaults defaults = PipelineDefaults.None;
        foreach (string bundle in chain.BundlePaths)
        {
            defaults = defaults.Then(PipelineDefaults.Load(
                Path.Combine(bundle, "default_pipeline.json"), defaultWarnings));
        }

        Console.WriteLine($"[默认属性] {(defaults.IsEmpty ? "无" : defaults.Summary)}");
        foreach (string warning in defaultWarnings)
        {
            Console.WriteLine($"  ⚠ {warning}");
        }

        // pipeline 之外的引用来源：任务入口 + option 的 pipeline_override
        ResourceReferenceScope scope = ResourceReferenceScope.FromConfig(config);
        Console.WriteLine($"[引用范围] 任务入口 {scope.TaskEntries.Count} 个 | option 覆盖目标 {scope.OverrideTargets.Count} 个 | 覆盖内引用 {scope.OverrideReferences.Count} 个");

        if (perLayer)
        {
            foreach (string bundle in chain.BundlePaths)
            {
                PipelineInventory one = PipelineInventory.Scan(
                    Path.Combine(bundle, "pipeline"),
                    Path.Combine(bundle, "image"),
                    null,
                    scope,
                    PipelineDefaults.Load(Path.Combine(bundle, "default_pipeline.json")));

                PrintInventory(one, $"{Path.GetFileName(bundle)}（独立）");
                Console.WriteLine();
            }
        }

        // 合并视图：临时目录里按链顺序拼接（后加载 bundle 的同名节点覆盖前面的），用于看「最终生效」的定义
        using MergedPipelineStage stage = PipelineBundleMerger.Stage(chain.BundlePaths);
        merged = PipelineInventory.Scan(
            stage.PipelineDirectory,
            Path.Combine(chain.BundlePaths[0], "image"),
            null,
            scope,
            defaults);

        PrintInventory(merged, "合并后（链上全部 pipeline，后者覆盖同名节点）");
        PrintDeadNodeReport(merged);

        if (!args.Contains("--verify"))
        {
            return merged.IsHealthy ? 0 : 3;
        }

        // ---- 框架侧核对：真实加载，逐节点读回定义 ----
        Console.WriteLine();
        Console.WriteLine("=== 框架侧核对（MaaResourceGetNodeData）===");
        using MaaYysEngine engine = MaaYysEngine.CreateNull();
        ResourceLoadResult loaded = await engine.LoadResourceChainAsync(chain.BundlePaths);
        Console.WriteLine($"  加载：{(loaded.Success ? "成功" : "失败 · " + loaded.Error)}（{loaded.LoadedBundles.Count} 个包）");
        if (!loaded.Success)
        {
            return 5;
        }

        int seen = 0;
        int missing = 0;
        foreach (string node in merged.Nodes.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            string? definition = engine.GetResourceNodeData(node);
            if (definition is null)
            {
                missing++;
                Console.WriteLine($"  ✗ 框架读不到的节点：{node}");
            }
            else
            {
                seen++;
            }
        }

        Console.WriteLine($"  框架可见节点 {seen} 个 / 静态扫描 {merged.Nodes.Count} 个，缺失 {missing} 个");
        bool ok = missing == 0 && seen > 0;
        Console.WriteLine(ok
            ? "  ✓ 静态扫描与框架解析完全一致"
            : "  ✗ 存在静态扫描看得到、框架却解析不到的节点");
        return ok ? 0 : 6;
    }

    /// <summary>死节点清单：哪些文件可以整体剔除、哪些只能删个别节点。</summary>
    private static void PrintDeadNodeReport(PipelineInventory inventory)
    {
        IReadOnlyCollection<string> dead = inventory.DeadNodes;
        Console.WriteLine();
        Console.WriteLine($"=== 死节点清单（既无引用、也非任务入口）共 {dead.Count} 个 ===");

        if (dead.Count == 0)
        {
            Console.WriteLine("  ✓ 没有死节点");
            return;
        }

        foreach ((string file, IReadOnlyList<string> nodes) in inventory.DeadNodesByFile)
        {
            int total = inventory.Nodes.Values.Count(n => string.Equals(n.File, file, StringComparison.OrdinalIgnoreCase));
            string verdict = nodes.Count == total ? "整份可删" : $"{nodes.Count}/{total} 个可删";
            Console.WriteLine($"  [{verdict}] {file}");
            Console.WriteLine($"      {string.Join("、", nodes.Take(12))}{(nodes.Count > 12 ? $" …等 {nodes.Count} 个" : string.Empty)}");
        }
    }

    private static void PrintInventory(PipelineInventory inventory, string title)
    {
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine($"  目录：{inventory.PipelineDirectory}");
        Console.WriteLine($"  文件：解析成功 {inventory.ParsedFileCount} 个，失败 {inventory.ParseFailures.Count} 个");
        Console.WriteLine($"  节点：{inventory.Nodes.Count} 个");
        Console.WriteLine($"  模板图引用：{inventory.Templates.Count} 张 | 模型引用：{inventory.Models.Count} 个");
        Console.WriteLine($"  自定义动作：{inventory.CustomActions.Count} 个 | 自定义识别：{inventory.CustomRecognitions.Count} 个");
        Console.WriteLine($"  命名不规范：{inventory.NamingIssues.Count} 个 | 不可达节点：{inventory.DeadNodes.Count} 个");

        var groups = new (string Label, IEnumerable<PipelineDiagnostic> Items)[]
        {
            ("错误", inventory.Diagnostics.Where(d => d.Severity == PipelineSeverity.Error)),
            ("警告", inventory.Diagnostics.Where(d => d.Severity == PipelineSeverity.Warning)),
            ("信息", inventory.Diagnostics.Where(d => d.Severity == PipelineSeverity.Info)),
        };

        foreach ((string label, IEnumerable<PipelineDiagnostic> items) in groups)
        {
            List<PipelineDiagnostic> list = [.. items];
            if (list.Count == 0)
            {
                continue;
            }

            Console.WriteLine($"  --- {label} {list.Count} 条 ---");
            foreach (PipelineDiagnostic diagnostic in list.Take(40))
            {
                Console.WriteLine($"    {diagnostic}");
            }

            if (list.Count > 40)
            {
                Console.WriteLine($"    …还有 {list.Count - 40} 条{label}");
            }
        }

        Console.WriteLine(inventory.IsHealthy ? "  ✓ 无错误" : $"  ✗ 有 {inventory.Errors.Count()} 个错误");
    }

    private static string? LocateInterfaceJson(string root)
    {
        string? current = Path.GetFullPath(root);
        for (int i = 0; i < 6 && current is not null; i++)
        {
            string candidate = Path.Combine(current, "interface.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string nested = Path.Combine(current, "resource", "interface.json");
            if (File.Exists(nested))
            {
                return nested;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
