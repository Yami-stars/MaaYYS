using MaaYys.Engine.Config;

namespace MaaYys.Engine.Resources;

/// <summary>
/// 一个渠道资源集解析后的加载链：<c>resource[].path[]</c> 转成绝对路径。
/// 顺序即加载顺序，后加载的 bundle 覆盖先加载的同名 pipeline 节点（base ← 渠道层）。
/// </summary>
public sealed record ResourceChain(
    string Name,
    string DisplayName,
    IReadOnlyList<string> BundlePaths,
    IReadOnlyList<string> MissingPaths)
{
    /// <summary>链里有可加载的包。</summary>
    public bool IsUsable => BundlePaths.Count > 0;

    /// <summary>path 里声明的目录全部存在。</summary>
    public bool IsComplete => MissingPaths.Count == 0;

    /// <summary>可加载且目录齐全。</summary>
    public bool IsReady => IsUsable && IsComplete;

    public string Summary => IsComplete
        ? $"{DisplayName}（{BundlePaths.Count} 个包）"
        : $"{DisplayName}（{BundlePaths.Count} 个可用，缺少 {MissingPaths.Count} 个目录）";
}

/// <summary>
/// 按 PI V2 语义把 <c>interface.json</c> 的 <c>resource[].path</c> 解析成绝对路径链。
/// 相对路径以 interface.json 所在目录为基准（与官方 MaaPiCli 一致）。
/// </summary>
public static class ResourceChainResolver
{
    public static ResourceChain Resolve(InterfaceConfig config, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);

        ResourceConfig resource = config.FindResource(resourceName)
            ?? throw new InvalidOperationException(
                $"interface.json 中不存在资源集「{resourceName}」（可选：{string.Join(" / ", config.Resources.Select(r => r.Name))}）");

        return Resolve(config, resource);
    }

    public static ResourceChain Resolve(InterfaceConfig config, ResourceConfig resource)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(resource);

        var paths = new List<string>(resource.Path.Count);
        var missing = new List<string>();
        foreach (string relative in resource.Path)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            string absolute = Path.GetFullPath(Path.Combine(config.BaseDirectory, relative));
            if (Directory.Exists(absolute))
            {
                paths.Add(absolute);
            }
            else
            {
                missing.Add(absolute);
            }
        }

        return new ResourceChain(resource.Name, resource.DisplayName, paths, missing);
    }

    /// <summary>解析全部资源集（UI 下拉用）。</summary>
    public static IReadOnlyList<ResourceChain> ResolveAll(InterfaceConfig config)
        => config.Resources.Select(r => Resolve(config, r)).ToList();
}
