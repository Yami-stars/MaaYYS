using System.Text.Json;
using MaaYys.Engine.Cleanup;

namespace MaaYys.P1Probe;

/// <summary>
/// 资源审计 / 整理 / 图片缓存（P3.1 遗留项）。
/// 用法：
/// <code>
/// audit [--root 目录] [--set 资源集] [--json out.json] [--no-assets]     只扫描（只读）
/// audit [--root 目录] --prune                                           直接删除过期资源（多资源集取交集）
/// cache [--root 程序目录] [--clear]                                      查看 / 清空图片缓存
/// </code>
/// 默认<b>只扫描不删除</b>；<c>--prune</c> 才会删，且与 GUI 启动时走的「资源更新整理」是同一条路径
/// （<see cref="ResourceMaintenance"/>），不进回收区。
/// </summary>
internal static class ProbeAudit
{
    public static int Run(string[] args)
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

        string? setName = ReadOption(args, "--set");
        string? configDirectory = ReadOption(args, "--config");

        Console.WriteLine($"[清单] {interfacePath}");
        ResourceAuditResult result = ResourceAudit.Scan(new AuditRequest
        {
            InterfacePath = interfacePath,
            ResourceSetName = setName,
            IncludeAssets = !args.Contains("--no-assets"),
            ConfigDirectory = configDirectory,
        });

        Console.WriteLine($"[资源集] {result.ResourceSetName}（{result.BundlePaths.Count} 个包）");
        Console.WriteLine($"[节点] 共 {result.TotalNodes} 个：可达 {result.LiveNodes} / 不可达 {result.DeadNodes}");
        Console.WriteLine(
            $"[保护] 隐藏任务节点 {result.ProtectedByHiddenTasks} 个；活动轮换/兜底链节点 {result.ProtectedByConvention} 个");
        if (result.TemplateTotal > 0 || result.ModelTotal > 0)
        {
            Console.WriteLine(
                $"[资源] 模板图 {result.TemplateTotal} 张（未引用 {result.TemplateUnused}）；模型 {result.ModelTotal} 个（未引用 {result.ModelUnused}）");
        }

        Console.WriteLine($"[摘要] {result.Summary}");
        Console.WriteLine($"[耗时] {result.Duration.TotalMilliseconds:F0} ms");

        if (result.HiddenTasks.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== 未接线的任务（隐藏，受保护）{result.HiddenTasks.Count} 个 ===");
            foreach (HiddenTaskInfo task in result.HiddenTasks)
            {
                Console.WriteLine($"  · {task.Label} | 入口 {task.Entry} | 选项 {task.OptionCount} 个 | {task.File}");
                Console.WriteLine($"      {task.EnableHint}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== 可整理项 ===");
        foreach (CleanupItem item in result.Cleanable)
        {
            Console.WriteLine($"  [{item.RiskLabel}] [{item.CategoryLabel}] {item.RelativePath}（{item.SizeText}）");
            Console.WriteLine($"      {item.Reason}");
            if (item.NodeSummary.Length > 0)
            {
                Console.WriteLine($"      {item.NodeSummary}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== 受保护（永不清理）{result.Protected.Count} 项 ===");
        foreach (CleanupItem item in result.Protected.Take(40))
        {
            Console.WriteLine($"  [{item.CategoryLabel}] {item.RelativePath}—— {item.Reason}");
        }

        if (result.Protected.Count > 40)
        {
            Console.WriteLine($"  …还有 {result.Protected.Count - 40} 项");
        }

        if (result.Problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== 问题 {result.Problems.Count} 条 ===");
            foreach (string problem in result.Problems.Take(20))
            {
                Console.WriteLine($"  - {problem}");
            }
        }

        string? jsonPath = ReadOption(args, "--json");
        if (jsonPath is not null)
        {
            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            Console.WriteLine($"[导出] 扫描结果已写入 {jsonPath}");
        }

        if (!args.Contains("--prune"))
        {
            return 0;
        }

        // --prune 走产品同一条路径：所有资源集取交集，只删「每套都走不到」的东西
        Console.WriteLine();
        Console.WriteLine("=== 整理（多资源集取交集，直接删除，不进回收区）===");
        string? statePath = ReadOption(args, "--state");
        MaintenanceOutcome outcome = ResourceMaintenance.RunIfNeeded(
            interfacePath, statePath, force: true);

        Console.WriteLine($"  资源版本 {outcome.Version}｜资源集 {string.Join(" / ", outcome.ResourceSets)}");
        Console.WriteLine($"  ✓ {outcome.Summary}");

        foreach (string line in outcome.Prune?.Removed ?? [])
        {
            Console.WriteLine($"    - {line}");
        }

        foreach (string error in outcome.Prune?.Errors ?? [])
        {
            Console.WriteLine($"    ! {error}");
        }

        // 整理后立刻复查：可整理项应当归零，引用不能出现新的悬空
        ResourceAuditResult after = ResourceAudit.Scan(new AuditRequest
        {
            InterfacePath = interfacePath,
            ResourceSetName = outcome.ResourceSets.Count == 1 ? outcome.ResourceSets[0] : setName,
            IncludeAssets = true,
            ConfigDirectory = configDirectory,
        });

        Console.WriteLine();
        Console.WriteLine(
            $"  整理后：节点 {after.TotalNodes}（不可达 {after.DeadNodes}）· 仍可整理 {after.Cleanable.Count} 项 · 问题 {after.Problems.Count} 条");

        return outcome.Prune is { } pruned && pruned.Errors.Count > 0 ? 2 : 0;
    }

    /// <summary>图片缓存：<c>cache [--root 程序目录] [--clear]</c>。</summary>
    public static int Cache(string[] args)
    {
        string root = ReadOption(args, "--root") ?? AppContext.BaseDirectory;
        bool clear = args.Contains("--clear");

        if (!clear)
        {
            ImageCacheSnapshot snapshot = ImageCache.Scan(root);
            Console.WriteLine($"[目录] {snapshot.Root}");
            Console.WriteLine($"[占用] {snapshot.Summary}");
            if (snapshot.Newest is { } newest)
            {
                Console.WriteLine($"[最新] {newest:yyyy-MM-dd HH:mm:ss}");
            }

            return 0;
        }

        ImageCacheClearOutcome outcome = ImageCache.Clear(root);
        Console.WriteLine($"{(outcome.Deleted > 0 ? "✓" : "·")} {outcome.Summary}");
        foreach (string error in outcome.Errors)
        {
            Console.WriteLine($"  ! {error}");
        }

        return outcome.Failed == 0 ? 0 : 2;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
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
}
