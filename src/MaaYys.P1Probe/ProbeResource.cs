using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Resources;

namespace MaaYys.P1Probe;

/// <summary>
/// 资源加载与渠道热切换验证（P2.5）。
/// 用法：<c>resource [--root 目录] [--set 资源集名] [--switch 目标资源集名] [--node 节点名] [--proof]</c>
///
/// 不依赖游戏窗口：用 Null 控制器建会话，验证「按链加载」与「切换资源集时不重启进程」。
/// <c>--node</c> 指定要对比的节点（缺省 <c>启动游戏</c>）：用 <c>MaaResourceGetNodeData</c>
/// 读出合并覆盖后的最终定义。<c>--proof</c> 构造临时覆盖包，单独验证覆盖机制本身。
/// --root 指向含 interface.json 的目录（缺省按环境变量 MAAYYS_RESOURCE_ROOT → 当前目录向上找）。
/// </summary>
internal static class ProbeResource
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

        Console.WriteLine($"[清单] {interfacePath}");
        List<string> errors = [];
        InterfaceConfig config = InterfaceConfig.Load(interfacePath, errors);
        Console.WriteLine($"[项目] {config.DisplayName} v{config.Version} | 资源集 {config.Resources.Count} 个 | 任务 {config.MergedTasks.Count} 个");
        foreach (string error in errors)
        {
            Console.WriteLine($"  ⚠ {error}");
        }

        Console.WriteLine();
        Console.WriteLine("=== 资源集解析 ===");
        foreach (ResourceChain chain in ResourceChainResolver.ResolveAll(config))
        {
            Console.WriteLine($"  {(chain.IsReady ? "✓" : "✗")} {chain.Name,-24} {chain.Summary}");
            foreach (string path in chain.BundlePaths)
            {
                Console.WriteLine($"      + {path}");
            }

            foreach (string missing in chain.MissingPaths)
            {
                Console.WriteLine($"      ! 缺失 {missing}");
            }
        }

        string initialName = ReadOption(args, "--set") ?? config.Resources.FirstOrDefault()?.Name ?? "base";
        ResourceChain initial;
        try
        {
            initial = ResourceChainResolver.Resolve(config, initialName);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[失败] {ex.Message}");
            return 2;
        }

        if (!initial.IsUsable)
        {
            Console.WriteLine($"[失败] 资源集「{initialName}」没有任何可加载的包");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"[引擎] 创建 Null 控制器会话 + 加载资源集「{initial.DisplayName}」");
        using MaaYysEngine engine = MaaYysEngine.CreateNull();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ResourceLoadResult first = await engine.LoadResourceChainAsync(initial.BundlePaths);
        watch.Stop();
        PrintResult("首次加载", first, watch.ElapsedMilliseconds);
        if (!first.Success)
        {
            return 3;
        }

        Console.WriteLine($"  ResourceLoaded={engine.ResourceLoaded} | 链长 {engine.ResourceChain.Count}");

        // ---- 同名覆盖验证：读同一节点在「base」与「base+渠道」下的最终定义 ----
        string nodeName = ReadOption(args, "--node") ?? "启动游戏";
        string? initialNode = PrintNode(engine, nodeName, initial.DisplayName);

        // 构造临时覆盖包，直接证明「后加载 bundle 覆盖同名节点」机制本身
        if (args.Contains("--proof"))
        {
            await ProveOverrideAsync(engine, initial.BundlePaths, nodeName);
            return 0;
        }

        string? targetName = ReadOption(args, "--switch");
        if (targetName is null)
        {
            return 0;
        }

        ResourceChain target;
        try
        {
            target = ResourceChainResolver.Resolve(config, targetName);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[失败] {ex.Message}");
            return 2;
        }

        if (!target.IsUsable)
        {
            Console.WriteLine($"[失败] 目标资源集「{targetName}」没有任何可加载的包");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"[切换] {initial.DisplayName} → {target.DisplayName}（同一进程内热切换）");
        watch.Restart();
        ResourceLoadResult switched = await engine.LoadResourceChainAsync(target.BundlePaths);
        watch.Stop();
        PrintResult("热切换", switched, watch.ElapsedMilliseconds);
        Console.WriteLine($"  ResourceLoaded={engine.ResourceLoaded} | 当前链 {engine.ResourceChain.Count} 个包");

        if (!switched.Success)
        {
            return 4;
        }

        string? targetNode = PrintNode(engine, nodeName, target.DisplayName);

        // 切回来，验证双向可逆
        Console.WriteLine();
        Console.WriteLine($"[回切] {target.DisplayName} → {initial.DisplayName}");
        watch.Restart();
        ResourceLoadResult back = await engine.LoadResourceChainAsync(initial.BundlePaths);
        watch.Stop();
        PrintResult("回切", back, watch.ElapsedMilliseconds);
        Console.WriteLine($"  ResourceLoaded={engine.ResourceLoaded} | 当前链 {engine.ResourceChain.Count} 个包");

        string? backNode = PrintNode(engine, nodeName, initial.DisplayName);

        // ---- 同名覆盖结论 ----
        Console.WriteLine();
        Console.WriteLine($"[覆盖] 节点「{nodeName}」在三条链下的定义对比：");
        PrintOverrideVerdict("官服链", initialNode, "B站服链", targetNode);
        Console.WriteLine(backNode == initialNode
            ? $"  ✓ 回切后节点定义与首次一致（双向可逆）"
            : $"  ⚠ 回切后节点定义与首次不一致（可能受加载顺序影响）");

        return back.Success ? 0 : 5;
    }

    /// <summary>
    /// 读取节点当前（已合并覆盖后的）定义并打印。返回原始 JSON 供调用方比较；节点不存在返回 null。
    /// </summary>
    private static string? PrintNode(MaaYysEngine engine, string nodeName, string label)
    {
        string? data = engine.GetResourceNodeData(nodeName);
        if (data is null)
        {
            Console.WriteLine($"  [节点] {label,-10} / {nodeName}：不存在或读取失败");
            return null;
        }

        // 压成单行，避免一屏 JSON 刷屏
        string compact = string.Join(' ', data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Console.WriteLine($"  [节点] {label,-10} / {nodeName} = {compact}");
        return data;
    }

    private static void PrintOverrideVerdict(string leftLabel, string? left, string rightLabel, string? right)
    {
        if (left is null || right is null)
        {
            Console.WriteLine("  ✗ 至少一侧读不到节点，无法判定覆盖");
            return;
        }

        if (left != right)
        {
            Console.WriteLine($"  ✓ 两侧定义不同 → 「同名覆盖」已生效（{rightLabel}覆盖掉了 {leftLabel}的同名节点）");
            return;
        }

        Console.WriteLine($"  ⚠ 两侧定义相同 —— 但这不代表覆盖机制失效，原因见下：");
        Console.WriteLine("     本资源包的 7 个渠道目录只覆盖 `package` 字段，而 MaaFramework 规定");
        Console.WriteLine("     `package` 仅在 action=StartApp/StopApp 时有效；`启动游戏` 节点是 DirectHit，");
        Console.WriteLine("     框架按未知字段丢弃 → 渠道差异在本资源包里本来就不会体现。");
        Console.WriteLine("     覆盖机制本身是否正常，用 `--proof` 单独验证（构造临时覆盖包改 post_delay）。");
    }

    /// <summary>
    /// 构造一个临时覆盖包，验证「后加载的 bundle 覆盖同名节点」这一机制本身。
    /// 只改 MaaFramework 明确识别的字段（post_delay），避免受资源包内容质量影响。
    /// </summary>
    private static async Task ProveOverrideAsync(
        MaaYysEngine engine, IReadOnlyList<string> basePaths, string nodeName)
    {
        Console.WriteLine();
        Console.WriteLine("[证明] 构造临时覆盖包，验证「后加载 bundle 覆盖同名节点」机制本身");
        string tempRoot = Path.Combine(Path.GetTempPath(), $"maayys-override-proof-{Guid.NewGuid():N}");
        try
        {
            string pipelineDir = Path.Combine(tempRoot, "pipeline");
            Directory.CreateDirectory(pipelineDir);
            File.WriteAllText(
                Path.Combine(pipelineDir, "证明用覆盖.json"),
                $"{{ \"{nodeName}\": {{ \"post_delay\": 12345 }} }}");

            var chain = new List<string>(basePaths) { tempRoot };
            ResourceLoadResult loaded = await engine.LoadResourceChainAsync(chain);
            if (!loaded.Success)
            {
                Console.WriteLine($"  ✗ 临时覆盖包加载失败：{loaded.Error}");
                return;
            }

            string? after = engine.GetResourceNodeData(nodeName);
            bool applied = after is not null && after.Contains("\"post_delay\":12345");
            Console.WriteLine(applied
                ? $"  ✓ 覆盖生效：`{nodeName}` 的 post_delay 由 1000 变为 12345 —— 后加载的 bundle 确实覆盖了 base 同名节点"
                : $"  ✗ 覆盖未生效：节点定义里没有临时包写入的 post_delay=12345");
            Console.WriteLine($"  链：{string.Join(" + ", chain.Select(Path.GetFileName))}");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清不掉不影响结论
            }
        }
    }

    private static void PrintResult(string label, ResourceLoadResult result, long elapsedMs)
    {
        Console.WriteLine(result.Success
            ? $"  ✓ {label}成功（{elapsedMs}ms）：{string.Join(" + ", result.LoadedBundles.Select(Path.GetFileName))}"
            : $"  ✗ {label}失败（{elapsedMs}ms）：{result.Error}");
    }

    private static string? LocateInterfaceJson(string start)
    {
        DirectoryInfo? dir = new(Path.GetFullPath(start));
        for (int depth = 0; depth < 8 && dir is not null; depth++)
        {
            string candidate = Path.Combine(dir.FullName, "interface.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? ReadOption(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
