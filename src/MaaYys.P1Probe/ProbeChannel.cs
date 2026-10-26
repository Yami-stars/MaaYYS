using System.Diagnostics;
using System.Text.Json;
using MaaYys.Engine;
using MaaYys.Engine.Config;
using MaaYys.Engine.Native;
using MaaYys.Engine.Resources;

namespace MaaYys.P1Probe;

/// <summary>
/// 渠道服分层核对（P3.5）。
/// 用法：
/// <code>
/// channel [--root 目录] [--set 资源集] [--verify]
/// </code>
/// 默认做<b>静态解析</b>：把每个资源集的渠道层拆出来（覆盖字段 / 专属节点 / 包名 / 规范性问题）。
/// <c>--verify</c> 会逐个资源集<b>真实加载</b>，用 <c>MaaResourceGetNodeData</c> 核对框架侧的最终定义，
/// 确认「渠道层改的东西确实生效了」，并顺带验证 <c>StartApp</c> 在 Null 控制器上不会崩。
/// </summary>
internal static class ProbeChannel
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
        Console.WriteLine($"[项目] {config.DisplayName} v{config.Version} | 资源集 {config.Resources.Count} 个");
        foreach (string error in errors)
        {
            Console.WriteLine($"  ⚠ {error}");
        }

        string? only = ReadOption(args, "--set");
        List<ResourceChain> chains = ResourceChainResolver.ResolveAll(config)
            .Where(c => only is null
                        || string.Equals(c.Name, only, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(c.DisplayName, only, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (chains.Count == 0)
        {
            Console.WriteLine($"[失败] 没有匹配的资源集（可选：{string.Join(" / ", config.Resources.Select(r => r.Name))}）");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine("=== 渠道层（静态解析：这一层相对 base 改了什么）===");
        int issueCount = 0;
        var layered = new List<(ResourceChain Chain, ChannelLayer? Layer)>();
        foreach (ResourceChain chain in chains)
        {
            ChannelLayer? layer = ChannelLayerResolver.Resolve(chain);
            layered.Add((chain, layer));
            ChannelPackages packages = ChannelLayerResolver.ResolvePackages(chain);

            string chainText = string.Join(" + ", chain.BundlePaths.Select(p => Path.GetFileName(
                p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))));
            Console.WriteLine($"  {(chain.IsReady ? "✓" : "✗")} {chain.DisplayName,-10} [{chainText}]");
            Console.WriteLine($"      渠道层：{layer?.Summary ?? "无（仅 base）"}");
            Console.WriteLine($"      包名：{packages}");

            if (layer is null)
            {
                continue;
            }

            foreach (ChannelNodeOverride item in layer.Overrides)
            {
                string mark = item.IsNewNode ? "+" : "~";
                Console.WriteLine($"      {mark} {item.Name}（{string.Join(", ", item.Fields)}）");
            }

            foreach (string issue in layer.Issues)
            {
                issueCount++;
                Console.WriteLine($"      ! {issue}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(issueCount == 0
            ? "[静态] ✓ 没有发现分层规范性问题"
            : $"[静态] ⚠ {issueCount} 条分层规范性问题（见上）");

        if (!args.Contains("--verify"))
        {
            return issueCount == 0 ? 0 : 3;
        }

        Console.WriteLine();
        Console.WriteLine("=== 框架侧核对（真实加载 + GetNodeData）===");
        using MaaYysEngine engine = MaaYysEngine.CreateNull();
        int failures = 0;

        foreach ((ResourceChain chain, ChannelLayer? layer) in layered)
        {
            if (!chain.IsUsable)
            {
                Console.WriteLine($"  ✗ {chain.DisplayName} 没有可加载的包");
                failures++;
                continue;
            }

            var watch = Stopwatch.StartNew();
            ResourceLoadResult loaded = await engine.LoadResourceChainAsync(chain.BundlePaths);
            watch.Stop();

            if (!loaded.Success)
            {
                Console.WriteLine($"  ✗ {chain.DisplayName,-10} 加载失败（{watch.ElapsedMilliseconds} ms）");
                failures++;
                continue;
            }

            List<string> notes = Verify(engine, layer);
            failures += notes.Count(n => n.StartsWith("✗", StringComparison.Ordinal));
            Console.WriteLine($"  ✓ {chain.DisplayName,-10} {watch.ElapsedMilliseconds,5} ms  "
                              + string.Join(" | ", notes));
        }

        // 顺带证明 StartApp 绑定不会崩：Null 控制器没有实现，应该返回 Invalid / Failed 而不是炸掉
        Console.WriteLine();
        MaaStatus start = engine.StartApp();
        MaaStatus stop = engine.StopApp();
        bool graceful = start is MaaStatus.Invalid or MaaStatus.Failed
                        && stop is MaaStatus.Invalid or MaaStatus.Failed;
        Console.WriteLine(graceful
            ? $"[StartApp] ✓ Null 控制器无实现，优雅返回 {start}/{stop}（Adb 下才会真正执行 am start/force-stop）"
            : $"[StartApp] ⚠ Null 控制器返回 {start}/{stop}（预期 Invalid/Failed，请核对绑定签名）");

        return failures == 0 && graceful ? 0 : 4;
    }

    /// <summary>核对框架侧合并结果，返回展示用的短句；以 ✗ 开头表示失败。</summary>
    private static List<string> Verify(MaaYysEngine engine, ChannelLayer? layer)
    {
        List<string> notes = [];

        string? startJson = engine.GetResourceNodeData(ChannelLayerResolver.StartNodeName);
        string? skipJson = engine.GetResourceNodeData(ChannelLayerResolver.SkipNodeName);
        string? stopJson = engine.GetResourceNodeData(ChannelLayerResolver.StopNodeName);

        if (startJson is null || skipJson is null || stopJson is null)
        {
            notes.Add("✗ 关键节点缺失");
            return notes;
        }

        // 1. 「启动游戏」的 next 必须还是 base 的 [启动游戏-跳过动画]，渠道层不能把它整条换掉
        string? firstNext = FirstNext(startJson);
        notes.Add(string.Equals(firstNext, ChannelLayerResolver.SkipNodeName, StringComparison.Ordinal)
            ? $"启动游戏→{firstNext}"
            : $"✗ 启动游戏→{firstNext}（期望 {ChannelLayerResolver.SkipNodeName}）");

        // 2. 「关闭阴阳师」必须是 StopApp，且包名是渠道自己的（这是 package 唯一能生效的地方）
        string? stopAction = ActionType(stopJson);
        string? stopPackage = ActionPackage(stopJson);
        notes.Add(stopAction == "StopApp"
            ? $"关闭={stopPackage}"
            : $"✗ 关闭 action={stopAction}");

        // 3. 渠道专属节点必须真的进了框架（华为的更新提示、应用宝的 QQ 登录）
        if (layer is { ExclusiveNodes.Count: > 0 })
        {
            List<string> missing = [.. layer.ExclusiveNodes.Where(n => engine.GetResourceNodeData(n) is null)];
            notes.Add(missing.Count == 0
                ? $"专属 {layer.ExclusiveNodes.Count} 个已生效"
                : $"✗ 专属节点未生效：{string.Join(",", missing)}");
        }

        // 4. 渠道层若改了「启动游戏-跳过动画」的 next，核对链里能带上专属节点
        if (layer is not null)
        {
            string skipText = skipJson;
            foreach (string exclusive in layer.ExclusiveNodes)
            {
                if (!skipText.Contains(exclusive, StringComparison.Ordinal))
                {
                    notes.Add($"· 专属节点「{exclusive}」不在跳过动画链上（靠别的路径进入也算正常）");
                }
            }
        }

        return notes;
    }

    private static string? FirstNext(string nodeJson)
    {
        using JsonDocument document = JsonDocument.Parse(nodeJson);
        if (!document.RootElement.TryGetProperty("next", out JsonElement next)
            || next.ValueKind != JsonValueKind.Array
            || next.GetArrayLength() == 0)
        {
            return null;
        }

        return next[0].TryGetProperty("name", out JsonElement name) ? name.GetString() : null;
    }

    private static string? ActionType(string nodeJson)
    {
        using JsonDocument document = JsonDocument.Parse(nodeJson);
        return document.RootElement.TryGetProperty("action", out JsonElement action)
               && action.TryGetProperty("type", out JsonElement type)
            ? type.GetString()
            : null;
    }

    private static string? ActionPackage(string nodeJson)
    {
        using JsonDocument document = JsonDocument.Parse(nodeJson);
        return document.RootElement.TryGetProperty("action", out JsonElement action)
               && action.TryGetProperty("param", out JsonElement param)
               && param.TryGetProperty("package", out JsonElement package)
            ? package.GetString()
            : null;
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
