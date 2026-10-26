using MaaYys.Engine.Resources;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// pipeline 静态自检测试（P3.1）。这套逻辑踩过的坑都在这里锁住：
/// v1/v2 两种写法、<c>[JumpBack]</c>/<c>[Anchor]</c> 前缀、NodeAttr 对象形式、
/// <c>default_pipeline.json</c> 的全局兜底边、以及「有入度但不可达」的假活节点。
/// </summary>
public sealed class PipelineInventoryTests : IDisposable
{
    private readonly string _root;

    public PipelineInventoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-pipe-" + Guid.NewGuid().ToString("N"));
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
            // 临时目录清不掉不影响测试结论
        }
    }

    private void WritePipeline(string relativePath, string json)
    {
        string path = Path.Combine(_root, "pipeline", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private PipelineInventory Scan(
        ResourceReferenceScope? scope = null, PipelineDefaults? defaults = null)
        => PipelineInventory.Scan(Path.Combine(_root, "pipeline"), null, null, scope, defaults);

    [Fact]
    public void Scan_ReadsNodesFromNestedFolders()
    {
        WritePipeline("a.json", """{ "A": { "recognition": "DirectHit" } }""");
        WritePipeline("sub/b.json", """{ "B": { "recognition": "DirectHit" } }""");

        PipelineInventory inventory = Scan();

        Assert.Equal(2, inventory.ParsedFileCount);
        Assert.Equal(2, inventory.Nodes.Count);
        Assert.Empty(inventory.ParseFailures);
        Assert.True(inventory.IsHealthy);
    }

    [Fact]
    public void Scan_ReportsBrokenJson()
    {
        WritePipeline("bad.json", """{ "A": { "next": [ } """);

        PipelineInventory inventory = Scan();

        Assert.Single(inventory.ParseFailures);
        Assert.Contains(inventory.Errors, d => d.Kind == "解析失败");
    }

    [Fact]
    public void Scan_ReportsDanglingNextReference()
    {
        WritePipeline("a.json", """{ "A": { "next": ["不存在"] } }""");

        PipelineInventory inventory = Scan();

        PipelineDiagnostic error = Assert.Single(inventory.Errors);
        Assert.Equal("引用悬空", error.Kind);
        Assert.Contains("不存在", error.Message);
        Assert.Equal("A", error.Node);
    }

    [Fact]
    public void Scan_StripsJumpBackPrefix()
    {
        WritePipeline("a.json", """
            {
              "A": { "next": ["[JumpBack]B"] },
              "B": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.True(inventory.IsHealthy);
        Assert.Equal(["B"], inventory.Nodes["A"].Next);
    }

    [Fact]
    public void Scan_TreatsAnchorReferenceSeparatelyFromNodeReference()
    {
        // [Anchor]X 不是节点名：不该被当作悬空节点，而应作为「未声明的锚点」警告
        WritePipeline("a.json", """{ "A": { "next": ["[Anchor]X"] } }""");

        PipelineInventory inventory = Scan();

        Assert.True(inventory.IsHealthy);
        Assert.Empty(inventory.Nodes["A"].Next);
        Assert.Contains(inventory.Warnings, d => d.Kind == "锚点悬空");
    }

    [Fact]
    public void Scan_ResolvesAnchorForReachability()
    {
        // A 用锚点跳转，B 声明锚点 X → B 必须算可达
        WritePipeline("a.json", """
            {
              "A": { "next": ["[Anchor]X"] },
              "B": { "anchor": "X", "next": ["C"] },
              "C": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan(new ResourceReferenceScope(["A"], [], []));

        Assert.Contains("B", inventory.LiveNodes);
        Assert.Contains("C", inventory.LiveNodes);
        Assert.Empty(inventory.DeadNodes);
    }

    [Fact]
    public void Scan_SupportsNodeAttrObjectForm()
    {
        WritePipeline("a.json", """
            {
              "A": { "next": [ { "name": "B", "jump_back": true } ] },
              "B": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.True(inventory.IsHealthy);
        Assert.Equal(["B"], inventory.Nodes["A"].Next);
    }

    [Fact]
    public void Scan_NormalizesPipelineV2TypeAndParam()
    {
        // v2 把类型放 recognition.type、参数放 recognition.param
        WritePipeline("a.json", """
            {
              "A": {
                "recognition": { "type": "TemplateMatch", "param": { "template": ["x/y.png"] } },
                "action": { "type": "Click", "param": { "target": [1, 2, 3, 4] } }
              }
            }
            """);

        PipelineInventory inventory = Scan();

        PipelineNodeInfo node = inventory.Nodes["A"];
        Assert.Equal("TemplateMatch", node.Recognition);
        Assert.Equal("Click", node.Action);
        Assert.Equal(["x/y.png"], node.Templates);
        Assert.True(inventory.IsHealthy);
    }

    [Fact]
    public void Scan_RejectsUnknownRecognitionAndAction()
    {
        WritePipeline("a.json", """
            {
              "A": { "recognition": "SuperMatch", "action": "Teleport" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.Contains(inventory.Errors, d => d.Kind == "未知识别算法");
        Assert.Contains(inventory.Errors, d => d.Kind == "未知动作");
    }

    [Fact]
    public void Scan_RequiresTemplateForTemplateMatch()
    {
        // 真实资源包里 寮3042 就是这个毛病：TemplateMatch 却没写 template
        WritePipeline("a.json", """{ "A": { "recognition": "TemplateMatch" } }""");

        PipelineInventory inventory = Scan();

        Assert.Contains(inventory.Errors, d => d.Kind == "缺参数" && d.Node == "A");
    }

    [Fact]
    public void Scan_RequiresCustomActionName()
    {
        WritePipeline("a.json", """{ "A": { "action": "Custom" } }""");

        PipelineInventory inventory = Scan();

        Assert.Contains(inventory.Errors, d => d.Kind == "缺参数");
        Assert.Empty(inventory.CustomActions);
    }

    [Fact]
    public void Scan_CollectsCustomHooksForAgent()
    {
        WritePipeline("a.json", """
            {
              "A": { "action": "Custom", "custom_action": "SwitchSoul" },
              "B": { "recognition": "Custom", "custom_recognition": "TaskCounterRecognition" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.Equal(["SwitchSoul"], inventory.CustomActions);
        Assert.Equal(["TaskCounterRecognition"], inventory.CustomRecognitions);
    }

    [Fact]
    public void Scan_WarnsOnDeprecatedFields()
    {
        WritePipeline("a.json", """
            {
              "A": { "is_sub": true, "recognition": "DirectHit" },
              "B": { "interrupt": ["A"], "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.Equal(2, inventory.Warnings.Count(d => d.Kind == "废弃字段"));
    }

    [Fact]
    public void Scan_WarnsOnDuplicateNodeInSameBundle()
    {
        WritePipeline("a.json", """{ "A": { "recognition": "DirectHit" } }""");
        WritePipeline("b.json", """{ "A": { "recognition": "OCR" } }""");

        PipelineInventory inventory = Scan();

        Assert.Contains(inventory.Warnings, d => d.Kind == "节点重名");
    }

    [Fact]
    public void Scan_ReportsMissingTaskEntry()
    {
        // tasks 声明的入口在 pipeline 里不存在 → 该任务永远跑不起来
        WritePipeline("a.json", """{ "A": { "recognition": "DirectHit" } }""");

        PipelineInventory inventory = Scan(new ResourceReferenceScope(["A", "幽灵入口"], [], []));

        Assert.Contains(inventory.Errors, d => d.Kind == "任务入口缺失");
        Assert.Equal(["幽灵入口"], inventory.MissingTaskEntries);
    }

    [Fact]
    public void DeadNodes_IncludeNodesOnlyReferencedByDeadNodes()
    {
        // 真实案例：test.json 的 N-1 只被「测试-停止任务」引用，而后者自己就是死的
        WritePipeline("a.json", """
            {
              "入口": { "next": ["活"] },
              "活": { "recognition": "DirectHit" },
              "测试-停止任务": { "next": ["N-1"] },
              "N-1": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []));

        Assert.Contains("活", inventory.LiveNodes);
        Assert.Contains("测试-停止任务", inventory.DeadNodes);
        Assert.Contains("N-1", inventory.DeadNodes);
    }

    [Fact]
    public void DeadNodes_ExcludeOptionOverrideTargets()
    {
        // 只被 option 覆盖的节点是活的，不能算死节点
        WritePipeline("a.json", """
            {
              "入口": { "recognition": "DirectHit" },
              "活动分支": { "recognition": "DirectHit" }
            }
            """);

        ResourceReferenceScope scope = new(["入口"], ["活动分支"], []);
        PipelineInventory inventory = Scan(scope);

        Assert.Contains("活动分支", inventory.LiveNodes);
        Assert.Empty(inventory.DeadNodes);
    }

    [Fact]
    public void Defaults_OnErrorKeepsFallbackChainAlive()
    {
        // 真实案例：default_pipeline.json 给每个节点挂 on_error → debug.json 的错误处理链
        WritePipeline("a.json", """
            {
              "入口": { "recognition": "DirectHit" },
              "兜底处理": { "next": ["兜底二"] },
              "兜底二": { "recognition": "DirectHit" }
            }
            """);

        var defaults = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["[JumpBack]兜底处理"] }""").RootElement,
            "default_pipeline.json");
        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []), defaults);

        Assert.Contains("兜底处理", inventory.LiveNodes);
        Assert.Contains("兜底二", inventory.LiveNodes);
        Assert.Empty(inventory.DeadNodes);
    }

    [Fact]
    public void Defaults_DanglingReferenceIsReported()
    {
        WritePipeline("a.json", """{ "入口": { "recognition": "DirectHit" } }""");

        var defaults = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["不存在的兜底"] }""").RootElement,
            "default_pipeline.json");
        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []), defaults);

        Assert.Contains(inventory.Errors, d => d.Kind == "引用悬空" && d.Message.Contains("default_pipeline.json"));
    }

    [Fact]
    public void Defaults_NodeOwnOnErrorWins()
    {
        // 节点自己写了 on_error 就不继承默认兜底边；这里两个可达节点都显式写了，
        // 所以只靠默认值才能到达的「默认兜底」应当不可达。
        WritePipeline("a.json", """
            {
              "入口": { "next": ["收尾"], "on_error": ["收尾"] },
              "收尾": { "next": ["入口"], "on_error": ["入口"] },
              "默认兜底": { "recognition": "DirectHit" }
            }
            """);

        var defaults = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["[JumpBack]默认兜底"] }""").RootElement,
            "default_pipeline.json");
        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []), defaults);

        Assert.Contains("默认兜底", inventory.DeadNodes);
    }

    [Fact]
    public void Defaults_AnyNodeWithoutOnErrorInheritsFallback()
    {
        // 反面：只要链上有一个节点没写 on_error，默认兜底边就把它拉活
        WritePipeline("a.json", """
            {
              "入口": { "next": ["收尾"], "on_error": ["收尾"] },
              "收尾": { "recognition": "DirectHit" },
              "默认兜底": { "recognition": "DirectHit" }
            }
            """);

        var defaults = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["[JumpBack]默认兜底"] }""").RootElement,
            "default_pipeline.json");
        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []), defaults);

        Assert.Contains("默认兜底", inventory.LiveNodes);
    }

    [Fact]
    public void Defaults_ThenPrefersLaterBundle()
    {
        var earlier = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["早"] }""").RootElement, "early");
        var later = PipelineDefaults.FromElement(
            System.Text.Json.JsonDocument.Parse("""{ "on_error": ["晚"] }""").RootElement, "late");

        PipelineDefaults merged = earlier.Then(later);

        Assert.Equal(["晚"], merged.OnError);
    }

    [Fact]
    public void DeadNodesByFile_GroupsByFileAndFlagsWholeFile()
    {
        WritePipeline("垃圾.json", """
            {
              "X": { "recognition": "DirectHit" },
              "Y": { "recognition": "DirectHit" }
            }
            """);
        WritePipeline("有用.json", """
            {
              "入口": { "next": ["Z"] },
              "Z": { "recognition": "DirectHit" },
              "孤立": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan(new ResourceReferenceScope(["入口"], [], []));

        Assert.Equal(2, inventory.DeadNodesByFile["垃圾.json"].Count);
        Assert.Single(inventory.DeadNodesByFile["有用.json"]);
    }

    [Fact]
    public void NamingIssues_FlagAutoGeneratedAndCopyNodes()
    {
        WritePipeline("a.json", """
            {
              "N-1766075283687": { "recognition": "DirectHit" },
              "寮3039_copy1_copy1": { "recognition": "DirectHit" },
              "testOCR": { "recognition": "DirectHit" },
              "正常节点": { "recognition": "DirectHit" },
              "N-非数字": { "recognition": "DirectHit" },
              "999": { "recognition": "DirectHit" }
            }
            """);

        PipelineInventory inventory = Scan();

        Assert.Equal(
            ["N-1766075283687", "testOCR", "寮3039_copy1_copy1"],
            inventory.NamingIssues.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Scan_ChecksTemplateFilesExist()
    {
        WritePipeline("a.json", """
            {
              "A": { "recognition": "TemplateMatch", "template": ["有.png"] },
              "B": { "recognition": "TemplateMatch", "template": ["没有.png"] }
            }
            """);
        string imageDir = Path.Combine(_root, "image");
        Directory.CreateDirectory(imageDir);
        File.WriteAllBytes(Path.Combine(imageDir, "有.png"), [0x89, 0x50]);

        PipelineInventory inventory = PipelineInventory.Scan(
            Path.Combine(_root, "pipeline"), imageDir);

        Assert.Contains(inventory.Errors, d => d.Kind == "模板图缺失" && d.Message.EndsWith("image/没有.png", StringComparison.Ordinal));
        Assert.DoesNotContain(inventory.Errors, d => d.Kind == "模板图缺失" && d.Message.EndsWith("image/有.png", StringComparison.Ordinal));
    }
}
