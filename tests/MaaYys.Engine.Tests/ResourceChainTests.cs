using MaaYys.Engine.Config;
using MaaYys.Engine.Resources;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>资源链解析测试（P2.5）：path[] 顺序、相对路径基准、缺失目录识别。</summary>
public sealed class ResourceChainTests : IDisposable
{
    private readonly string _root;

    public ResourceChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private InterfaceConfig LoadConfig(string json)
    {
        string path = Path.Combine(_root, "interface.json");
        File.WriteAllText(path, json);
        List<string> errors = [];
        return InterfaceConfig.Load(path, errors);
    }

    [Fact]
    public void Resolve_ShouldKeepPathOrder_AndFlagMissingDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_root, "resource_pack", "base"));
        // official 目录故意不创建，用于验证缺失识别
        InterfaceConfig config = LoadConfig("""
        {
          "interface_version": 2,
          "name": "test",
          "resource": [
            { "name": "官服", "label": "官服", "path": ["./resource_pack/base", "./resource_pack/official"] }
          ]
        }
        """);

        ResourceChain chain = ResourceChainResolver.Resolve(config, "官服");

        Assert.Equal("官服", chain.Name);
        Assert.Single(chain.BundlePaths);
        Assert.EndsWith(Path.Combine("resource_pack", "base"), chain.BundlePaths[0], StringComparison.Ordinal);
        Assert.Single(chain.MissingPaths);
        Assert.True(chain.IsUsable);
        Assert.False(chain.IsComplete);
        Assert.True(Path.IsPathRooted(chain.BundlePaths[0]));
    }

    [Fact]
    public void Resolve_WithAllDirectoriesPresent_ShouldBeReady()
    {
        Directory.CreateDirectory(Path.Combine(_root, "resource_pack", "base"));
        Directory.CreateDirectory(Path.Combine(_root, "resource_pack", "bilibili"));
        InterfaceConfig config = LoadConfig("""
        {
          "interface_version": 2,
          "name": "test",
          "resource": [
            { "name": "bilibili服", "path": ["./resource_pack/base", "./resource_pack/bilibili"] }
          ]
        }
        """);

        ResourceChain chain = ResourceChainResolver.Resolve(config, "bilibili服");

        Assert.Equal(2, chain.BundlePaths.Count);
        Assert.Empty(chain.MissingPaths);
        Assert.True(chain.IsReady);
        // 顺序即加载顺序：base 先、渠道层后（后加载覆盖同名节点）
        Assert.EndsWith("base", chain.BundlePaths[0], StringComparison.Ordinal);
        Assert.EndsWith("bilibili", chain.BundlePaths[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnknownResource_ShouldThrowWithAvailableNames()
    {
        InterfaceConfig config = LoadConfig("""
        {
          "interface_version": 2,
          "name": "test",
          "resource": [ { "name": "官服", "path": ["./resource_pack/base"] } ]
        }
        """);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => ResourceChainResolver.Resolve(config, "不存在的服"));
        Assert.Contains("官服", ex.Message);
    }

    [Fact]
    public void ResolveAll_ShouldPreserveInterfaceDeclarationOrder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "resource_pack", "base"));
        InterfaceConfig config = LoadConfig("""
        {
          "interface_version": 2,
          "name": "test",
          "resource": [
            { "name": "官服", "path": ["./resource_pack/base"] },
            { "name": "bilibili服", "path": ["./resource_pack/base"] },
            { "name": "华为渠道服", "path": ["./resource_pack/base"] }
          ]
        }
        """);

        IReadOnlyList<ResourceChain> chains = ResourceChainResolver.ResolveAll(config);

        Assert.Equal(3, chains.Count);
        Assert.Equal(["官服", "bilibili服", "华为渠道服"], chains.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Resolve_WithEmptyPath_ShouldBeUnusable()
    {
        InterfaceConfig config = LoadConfig("""
        {
          "interface_version": 2,
          "name": "test",
          "resource": [ { "name": "空资源集", "path": [] } ]
        }
        """);

        ResourceChain chain = ResourceChainResolver.Resolve(config, "空资源集");

        Assert.False(chain.IsUsable);
        Assert.False(chain.IsReady);
    }
}
