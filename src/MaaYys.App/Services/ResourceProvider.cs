using System.IO;
using MaaYys.Engine.Config;
using MaaYys.Engine.Resources;

namespace MaaYys.App.Services;

/// <summary>
/// 资源集提供者（P2.5）：interface.json 只解析一次，各实例按名字取加载链。
///
/// 找不到 interface.json 时**降级而不报错**：实例照样能连接、能预览，只是没有 pipeline 资源
/// （P3.6 产出资源后自动生效）。
/// </summary>
public sealed class ResourceProvider
{
    private readonly Dictionary<string, ResourceChain> _byName = new(StringComparer.Ordinal);

    private ResourceProvider(string? interfacePath) => InterfacePath = interfacePath;

    /// <summary>interface.json 绝对路径（未找到为 null）。</summary>
    public string? InterfacePath { get; }

    /// <summary>解析后的清单（未加载为 null）。</summary>
    public InterfaceConfig? Config { get; private set; }

    /// <summary>全部资源集加载链（按 interface.json 声明顺序）。</summary>
    public IReadOnlyList<ResourceChain> Chains { get; private set; } = [];

    /// <summary>加载问题（缺失/校验错误），供 UI 提示。</summary>
    public List<string> Errors { get; } = [];

    public bool Loaded => Config is not null;

    /// <summary>
    /// interface.json <c>controller[].win32</c> 段给出的默认值（P3.6）。
    /// 实例自己的字段优先，没填才用这里的；清单里没有 Win32 控制器时为 null。
    /// </summary>
    public Win32Defaults? Win32Defaults { get; private set; }

    public static ResourceProvider Load(string? interfacePath)
    {
        var provider = new ResourceProvider(interfacePath);
        if (string.IsNullOrWhiteSpace(interfacePath) || !File.Exists(interfacePath))
        {
            provider.Errors.Add("未找到 interface.json：资源集不可用，实例仅能预览、无法执行任务");
            return provider;
        }

        try
        {
            List<string> errors = [];
            InterfaceConfig config = InterfaceConfig.Load(interfacePath, errors);
            provider.Config = config;
            provider.Errors.AddRange(errors);
            provider.Chains = ResourceChainResolver.ResolveAll(config);
            provider.Win32Defaults = Win32Defaults.From(config);
            foreach (ResourceChain chain in provider.Chains)
            {
                provider._byName[chain.Name] = chain;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            provider.Errors.Add($"interface.json 读取失败：{ex.Message}");
        }

        return provider;
    }

    /// <summary>按名字取加载链；不存在返回 null。</summary>
    public ResourceChain? Resolve(string? name)
        => name is not null && _byName.TryGetValue(name, out ResourceChain? chain) ? chain : null;
}
