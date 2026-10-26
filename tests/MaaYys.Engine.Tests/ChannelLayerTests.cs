using MaaYys.Engine.Resources;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 渠道服分层（P3.5）的测试。
/// <para>
/// 锁住四条规则：
/// <list type="bullet">
/// <item><b>分层识别</b>：链上第一个 bundle 是 base，后面的才是渠道层；只有 base 时没有渠道层。</item>
/// <item><b>差异识别</b>：渠道层的节点哪些是覆盖、哪些是 base 没有的专属节点。</item>
/// <item><b>包名解析</b>：渠道层覆盖 base；纯 base 时回落到 base 自己的「关闭阴阳师」。</item>
/// <item><b>踩坑检查</b>：渠道层改「启动游戏」的 next 会跳过开场动画；关闭包名带 activity 会关不掉游戏。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ChannelLayerTests : IDisposable
{
    private readonly string _root;

    public ChannelLayerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "base", "pipeline"));
        Directory.CreateDirectory(Path.Combine(_root, "huawei", "pipeline"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    private string BasePath => Path.Combine(_root, "base");

    private string ChannelPath => Path.Combine(_root, "huawei");

    private ResourceChain Chain(bool withChannel = true)
    {
        List<string> paths = [BasePath];
        if (withChannel)
        {
            paths.Add(ChannelPath);
        }

        return new ResourceChain("华为服", "华为服", paths, []);
    }

    private void WriteBase()
    {
        Write("base/pipeline/启动游戏.json", """
        {
          "启动游戏": { "recognition": "DirectHit", "next": ["启动游戏-跳过动画"] },
          "启动游戏-跳过动画": { "action": "Custom", "custom_action": "RandomTouch", "next": ["启动游戏2"] },
          "启动游戏2": { "recognition": "OCR", "expected": "设置" },
          "关闭阴阳师": { "action": "StopApp", "package": "com.netease.onmyoji.wyzymnqsd_cps", "recognition": "DirectHit" }
        }
        """);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // ---- 分层识别 ----

    [Fact]
    public void 只有Base时没有渠道层()
    {
        WriteBase();

        Assert.Null(ChannelLayerResolver.Resolve(Chain(withChannel: false)));
    }

    [Fact]
    public void 渠道层能拆出覆盖与专属节点()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "启动游戏": { "package": "com.netease.onmyoji.huawei" },
          "关闭阴阳师": { "package": "com.netease.onmyoji.huawei" },
          "启动游戏-跳过动画": { "next": ["启动游戏2", "[JumpBack]关闭华为更新提示"] },
          "关闭华为更新提示": { "action": "Click", "recognition": "OCR", "expected": "以后再说" }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Equal("huawei", layer.Name);
        Assert.Equal(4, layer.Overrides.Count);
        Assert.Equal(["关闭华为更新提示"], layer.ExclusiveNodes);
        Assert.Contains(layer.Overrides, o => o.Name == "启动游戏-跳过动画" && !o.IsNewNode);
        Assert.Contains(layer.Overrides, o => o.Name == "关闭华为更新提示" && o.IsNewNode);
    }

    // ---- 包名解析 ----

    [Fact]
    public void 包名取渠道层的值()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "启动游戏": { "package": "com.netease.onmyoji.huawei/act" },
          "关闭阴阳师": { "package": "com.netease.onmyoji.huawei" }
        }
        """);

        ChannelPackages packages = ChannelLayerResolver.ResolvePackages(Chain());

        Assert.Equal("com.netease.onmyoji.huawei/act", packages.Start);
        Assert.Equal("com.netease.onmyoji.huawei", packages.Stop);
    }

    [Fact]
    public void 纯Base时回落到Base自己的关闭包名()
    {
        WriteBase();

        ChannelPackages packages = ChannelLayerResolver.ResolvePackages(Chain(withChannel: false));

        Assert.Null(packages.Start);                                     // base 不写启动包名（桌面端不需要）
        Assert.Equal("com.netease.onmyoji.wyzymnqsd_cps", packages.Stop); // 关闭包名由 base 提供
    }

    // ---- 踩坑检查 ----

    [Fact]
    public void 渠道层改启动游戏的Next会跳过开场动画()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "启动游戏": { "package": "com.netease.onmyoji.huawei", "next": ["启动游戏2"] }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Contains(layer.Issues, i => i.Contains("启动游戏-跳过动画", StringComparison.Ordinal));
        Assert.True(layer.HasIssues);
    }

    [Fact]
    public void 关闭包名带Activity会被判为关不掉游戏()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "关闭阴阳师": { "package": "com.netease.onmyoji.huawei/com.netease.onmyoji.tag0" }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Contains(layer.Issues, i => i.Contains("am force-stop", StringComparison.Ordinal));
    }

    [Fact]
    public void 启动包名带Activity不算问题()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "启动游戏": { "package": "com.netease.onmyoji.huawei/com.netease.onmyoji.tag0" }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.False(layer.HasIssues);
        Assert.Equal("com.netease.onmyoji.huawei/com.netease.onmyoji.tag0", layer.StartPackage);
    }

    [Fact]
    public void Package显式重复Base不算冗余()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "关闭阴阳师": { "package": "com.netease.onmyoji.wyzymnqsd_cps" }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.DoesNotContain(layer.Issues, i => i.Contains("冗余", StringComparison.Ordinal));
    }

    [Fact]
    public void 非Package字段与Base相同会被判冗余()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", """
        {
          "关闭阴阳师": { "action": "StopApp" }
        }
        """);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Contains(layer.Issues, i => i.Contains("冗余", StringComparison.Ordinal));
    }

    [Fact]
    public void 渠道层文件坏掉会被记录而不是抛异常()
    {
        WriteBase();
        Write("huawei/pipeline/启动游戏.json", "{ 这不是 json ");

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Contains(layer.Issues, i => i.Contains("无法解析", StringComparison.Ordinal));
        Assert.Empty(layer.Overrides);
    }

    [Fact]
    public void 渠道层没有Pipeline目录会被记录()
    {
        WriteBase();
        Directory.Delete(Path.Combine(ChannelPath, "pipeline"), recursive: true);

        ChannelLayer layer = ChannelLayerResolver.Resolve(Chain())!;

        Assert.Contains(layer.Issues, i => i.Contains("pipeline", StringComparison.Ordinal));
    }

    // ---- 常量契约 ----

    [Fact]
    public void 关键节点名常量与资源包一致()
    {
        Assert.Equal("启动游戏", ChannelLayerResolver.StartNodeName);
        Assert.Equal("关闭阴阳师", ChannelLayerResolver.StopNodeName);
        Assert.Equal("启动游戏-跳过动画", ChannelLayerResolver.SkipNodeName);
    }
}
