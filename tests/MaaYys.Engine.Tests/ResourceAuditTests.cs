using MaaYys.Engine.Cleanup;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 资源审计 / 整理 / 图片缓存的测试。
/// <para>
/// 锁住两条主线：
/// <list type="bullet">
/// <item><b>哪些不能删</b>：隐藏任务、活动轮换包、<c>debug.json</c>、OCR 模型、越界路径；</item>
/// <item><b>删得对不对</b>：直接删除（无回收区）、保留缩进与行尾、dry-run 不动文件。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ResourceAuditTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDirectory;

    public ResourceAuditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-audit-" + Guid.NewGuid().ToString("N"));
        _configDirectory = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(_root, "pack", "pipeline"));
        Directory.CreateDirectory(_configDirectory);
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

    // ---- 造数据 ----

    private void WriteInterface(string imports = "[\"tasks/main.json\"]", string version = "v1.0.0")
    {
        Write("interface.json", $$"""
        {
          "interface_version": 2,
          "name": "audit-test",
          "version": "{{version}}",
          "controller": [],
          "resource": [
            { "name": "测试服", "path": ["./pack"] }
          ],
          "import": {{imports}}
        }
        """);
    }

    private void WriteTasks(string fileName, string json) => Write(Path.Combine("tasks", fileName), json);

    private void WritePipeline(string relativePath, string json)
        => Write(Path.Combine("pack", "pipeline", relativePath), json);

    private void WriteAsset(string relativePath)
    {
        string path = Path.Combine(_root, "pack", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>标准场景：一条活链路 + 一个死节点 + 一个隐藏任务 + 活动包 + debug 兜底文件。</summary>
    private void WriteStandardTree()
    {
        WriteInterface();
        WriteTasks("main.json", """{ "task": [ { "name": "主线", "entry": "A" } ] }""");
        WriteTasks("hidden.json", """
        {
          "task": [ { "name": "隐藏玩法", "entry": "H", "option": ["opt"] } ],
          "option": {
            "opt": {
              "type": "select",
              "cases": [ { "name": "开", "pipeline_override": { "H2": { "enabled": true } } } ]
            }
          }
        }
        """);

        WritePipeline("main.json", """
        {
          "A": { "recognition": "DirectHit", "next": ["B"] },
          "B": { "recognition": "TemplateMatch", "template": ["a.png"] },
          "X": { "recognition": "DirectHit", "next": [] },
          "H": { "recognition": "DirectHit", "next": ["H2"] },
          "H2": { "recognition": "DirectHit" }
        }
        """);

        WritePipeline("活动/每月活动.json", """
        {
          "Y1": { "recognition": "DirectHit", "next": ["Y2"] },
          "Y2": { "recognition": "DirectHit" }
        }
        """);

        WritePipeline("debug.json", """{ "D1": { "recognition": "DirectHit" } }""");

        WriteAsset("image/a.png");
        WriteAsset("image/orphan.png");
        WriteAsset("model/ocr/det.onnx");
        WriteAsset("model/detect/unused.onnx");

        Write(Path.Combine("config", "maayys.json.bak"), "{}");
    }

    private ResourceAuditResult Scan(bool includeAssets = true)
        => ResourceAudit.Scan(new AuditRequest
        {
            InterfacePath = Path.Combine(_root, "interface.json"),
            ResourceSetName = "测试服",
            IncludeAssets = includeAssets,
            ConfigDirectory = _configDirectory,
        });

    // ---- 审计 ----

    [Fact]
    public void Scan_FindsHiddenTaskWithoutImportingIt()
    {
        WriteStandardTree();

        ResourceAuditResult result = Scan();

        HiddenTaskInfo hidden = Assert.Single(result.HiddenTasks);
        Assert.Equal("隐藏玩法", hidden.Label);
        Assert.Equal("H", hidden.Entry);
        Assert.Equal("tasks/hidden.json", hidden.File);
        Assert.Contains("tasks/hidden.json", hidden.EnableHint);
    }

    [Fact]
    public void Scan_KeepsHiddenTaskNodesAlive()
    {
        WriteStandardTree();

        ResourceAuditResult result = Scan();

        Assert.Equal(2, result.ProtectedByHiddenTasks);
        Assert.Equal(4, result.DeadNodes);      // X + Y1/Y2 + D1（H/H2 因隐藏任务算活）
        Assert.Equal(8, result.TotalNodes);

        CleanupItem? deadGroup = result.Items.FirstOrDefault(i => i.RelativePath == "pipeline/main.json");
        Assert.NotNull(deadGroup);
        Assert.Equal(CleanupCategory.DeadNodes, deadGroup!.Category);
        Assert.Contains("X", deadGroup.Nodes);
        Assert.DoesNotContain("H", deadGroup.Nodes);
        Assert.Equal(CleanupRisk.Safe, deadGroup.Risk);
    }

    [Fact]
    public void Scan_ProtectsActivityRotationAndDebugFile()
    {
        WriteStandardTree();

        ResourceAuditResult result = Scan();

        CleanupItem activity = result.Items.Single(i => i.RelativePath == "pipeline/活动/每月活动.json");
        Assert.Equal(CleanupRisk.Protected, activity.Risk);
        Assert.False(activity.CanClean);
        Assert.Contains("轮换", activity.Reason);

        CleanupItem debug = result.Items.Single(i => i.RelativePath == "pipeline/debug.json");
        Assert.Equal(CleanupRisk.Protected, debug.Risk);
    }

    [Fact]
    public void Scan_ReportsUnusedAssetsButProtectsOcrModels()
    {
        WriteStandardTree();

        ResourceAuditResult result = Scan();

        Assert.Equal(2, result.TemplateTotal);
        Assert.Equal(1, result.TemplateUnused);
        Assert.Contains(result.Items, i => i.RelativePath == "image/orphan.png" && i.Risk == CleanupRisk.Review);

        Assert.Equal(2, result.ModelTotal);

        // 模型一律受保护：ocr/ 是框架隐式依赖，detect/ 下即便无引用也因「不可再生」保留
        Assert.Equal(0, result.ModelUnused);
        Assert.Contains(result.Items, i => i.RelativePath == "model/ocr/det.onnx" && i.Risk == CleanupRisk.Protected);
        Assert.Contains(result.Items, i => i.RelativePath == "model/detect/unused.onnx" && i.Risk == CleanupRisk.Protected);
    }

    [Fact]
    public void Scan_FindsConfigBackups()
    {
        WriteStandardTree();

        ResourceAuditResult result = Scan(includeAssets: false);

        CleanupItem backup = Assert.Single(result.Items, i => i.Category == CleanupCategory.ConfigBackup);
        Assert.Equal("maayys.json.bak", backup.RelativePath);
        Assert.Equal(CleanupRisk.Safe, backup.Risk);
    }

    [Fact]
    public void Scan_DoesNotTouchResourceFiles()
    {
        WriteStandardTree();
        string before = File.ReadAllText(Path.Combine(_root, "pack", "pipeline", "main.json"));

        Scan();

        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "pack", "pipeline", "main.json")));
    }

    // ---- 整理（直接删除，无回收区） ----

    [Fact]
    public void Prune_RemovesDeadNodesInPlace()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan(includeAssets: false);

        string mainPath = Path.Combine(_root, "pack", "pipeline", "main.json");
        PruneOutcome outcome = ResourcePruner.Prune(result);

        Assert.True(outcome.Success);
        Assert.True(outcome.RemovedNodes > 0);

        string after = File.ReadAllText(mainPath);
        Assert.DoesNotContain("\"X\"", after);
        Assert.Contains("\"A\"", after);
        Assert.Contains("\"B\"", after);
        Assert.Contains("\"H\"", after);
        Assert.Contains("\"H2\"", after);
    }

    [Fact]
    public void Prune_DeletesWholeDeadFileAndLeavesNoBackup()
    {
        WriteStandardTree();
        WritePipeline("孤儿.json", """{ "Z1": { "recognition": "DirectHit" } }""");
        ResourceAuditResult result = Scan(includeAssets: false);

        CleanupItem whole = result.Items.Single(i => i.RelativePath == "pipeline/孤儿.json");
        Assert.Equal(CleanupCategory.DeadFile, whole.Category);

        ResourcePruner.Prune(result);

        Assert.False(File.Exists(Path.Combine(_root, "pack", "pipeline", "孤儿.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "config", "cleanup")));
    }

    [Fact]
    public void Prune_DeletesUnusedImagesButKeepsAllModels()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan();

        ResourcePruner.Prune(result);

        Assert.False(File.Exists(Path.Combine(_root, "pack", "image", "orphan.png")));
        Assert.True(File.Exists(Path.Combine(_root, "pack", "image", "a.png")));

        // 模型不可再生：ocr/ 是框架隐式依赖，detect/ 下无引用的旧版本也要留着（换皮肤/换版本还要用）
        Assert.True(File.Exists(Path.Combine(_root, "pack", "model", "detect", "unused.onnx")));
        Assert.True(File.Exists(Path.Combine(_root, "pack", "model", "ocr", "det.onnx")));
    }

    [Fact]
    public void Prune_CanLeaveUnusedAssetsAlone()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan();

        ResourcePruner.Prune(result, new PruneOptions { IncludeUnusedAssets = false });

        Assert.True(File.Exists(Path.Combine(_root, "pack", "image", "orphan.png")));
        Assert.True(File.Exists(Path.Combine(_root, "pack", "pipeline", "活动", "每月活动.json")));
    }

    [Fact]
    public void Prune_DeletesConfigBackups()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan(includeAssets: false);

        ResourcePruner.Prune(result);

        Assert.False(File.Exists(Path.Combine(_configDirectory, "maayys.json.bak")));
    }

    [Fact]
    public void Prune_DryRunChangesNothing()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan();
        string mainPath = Path.Combine(_root, "pack", "pipeline", "main.json");
        string before = File.ReadAllText(mainPath);

        PruneOutcome outcome = ResourcePruner.Prune(result, new PruneOptions { DryRun = true });

        Assert.True(outcome.RemovedNodes > 0);
        Assert.Equal(before, File.ReadAllText(mainPath));
        Assert.True(File.Exists(Path.Combine(_root, "pack", "image", "orphan.png")));
        Assert.True(File.Exists(Path.Combine(_configDirectory, "maayys.json.bak")));
    }

    [Fact]
    public void Prune_NeverTouchesForbiddenOrOutOfBoundsPaths()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan(includeAssets: false);

        // 手工构造越界项：把 interface.json 当成死文件（真实判定永远不会这么给，但底层要拦得住）
        CleanupItem forged = new()
        {
            Id = "forged",
            Category = CleanupCategory.DeadFile,
            Risk = CleanupRisk.Safe,
            RelativePath = "../../interface.json",
            Reason = "测试越界",
        };

        List<CleanupItem> items = [.. result.Items.Where(i => i.CanClean), forged];
        ResourcePruner.Prune(WithItems(result, items));

        Assert.True(File.Exists(Path.Combine(_root, "interface.json")));
    }

    [Fact]
    public void Prune_IgnoresProtectedItems()
    {
        WriteStandardTree();
        ResourceAuditResult result = Scan(includeAssets: false);

        PruneOutcome outcome = ResourcePruner.Prune(WithItems(result, result.Protected));

        Assert.True(outcome.Success);
        Assert.False(outcome.Changed);
        Assert.True(File.Exists(Path.Combine(_root, "pack", "pipeline", "活动", "每月活动.json")));
        Assert.True(File.Exists(Path.Combine(_root, "pack", "pipeline", "debug.json")));
    }

    [Fact]
    public void Prune_PreservesOriginalIndentAndNewLine()
    {
        WriteStandardTree();

        string path = Path.Combine(_root, "pack", "pipeline", "crlf.json");
        string original = "{\r\n    \"P1\": {\r\n        \"recognition\": \"DirectHit\",\r\n        \"next\": [\r\n            \"P2\"\r\n        ]\r\n    },\r\n    \"P2\": {\r\n        \"recognition\": \"DirectHit\",\r\n        \"next\": [\r\n            \"P1\"\r\n        ]\r\n    },\r\n    \"Dead\": {\r\n        \"recognition\": \"DirectHit\"\r\n    }\r\n}";
        File.WriteAllText(path, original);

        // 让 P1 变成任务入口，P2 才有意义；Dead 保持不可达
        WriteInterface("[\"tasks/main.json\"]");
        WriteTasks("main.json", """{ "task": [ { "name": "主线", "entry": "P1" } ] }""");

        ResourceAuditResult result = Scan(includeAssets: false);
        CleanupItem item = result.Items.Single(i => i.RelativePath == "pipeline/crlf.json");
        Assert.Contains("Dead", item.Nodes);

        ResourcePruner.Prune(result);

        string after = File.ReadAllText(path);
        Assert.DoesNotContain("Dead", after);
        Assert.Contains("\r\n", after);
        Assert.Contains("\r\n    \"P1\"", after);   // 4 空格缩进保留
    }

    // ---- 多资源集取交集 ----

    [Fact]
    public void Merge_KeepsOnlyItemsCleanableEverywhere()
    {
        ResourceAuditResult a = AuditOf("官服",
            Item(CleanupCategory.DeadNodes, "pipeline/a.json", ["X", "Y"]),
            Item(CleanupCategory.DeadFile, "pipeline/only-a.json", []));

        ResourceAuditResult b = AuditOf("b服",
            Item(CleanupCategory.DeadNodes, "pipeline/a.json", ["X", "Z"]));

        ResourceAuditResult merged = ResourceMaintenance.Merge([a, b]);

        CleanupItem single = Assert.Single(merged.Items);
        Assert.Equal("pipeline/a.json", single.RelativePath);
        Assert.Equal(["X"], single.Nodes);            // 只有两边都走不到的节点才删
        Assert.DoesNotContain(merged.Items, i => i.RelativePath == "pipeline/only-a.json");
    }

    [Fact]
    public void Merge_ProtectsWhenAnySetProtects()
    {
        ResourceAuditResult a = AuditOf("官服", Item(CleanupCategory.DeadFile, "pipeline/x.json", []));
        ResourceAuditResult b = AuditOf("b服", new CleanupItem
        {
            Id = "file:pipeline/x.json",
            Category = CleanupCategory.DeadFile,
            Risk = CleanupRisk.Protected,
            RelativePath = "pipeline/x.json",
            Reason = "活动轮换包",
        });

        Assert.Empty(ResourceMaintenance.Merge([a, b]).Items);
    }

    [Fact]
    public void Merge_UnionOfBundlePaths()
    {
        ResourceAuditResult merged = ResourceMaintenance.Merge([AuditOf("官服"), AuditOf("b服")]);

        Assert.Equal(["base", "official"], merged.BundlePaths);
        Assert.Contains("官服", merged.ResourceSetName);
        Assert.Contains("b服", merged.ResourceSetName);
    }

    // ---- 版本更新触发 ----

    [Fact]
    public void Maintenance_SkipsWhenNothingChanged()
    {
        WriteStandardTree();
        string statePath = Path.Combine(_configDirectory, "resource-state.json");

        MaintenanceOutcome first = ResourceMaintenance.RunIfNeeded(Path.Combine(_root, "interface.json"), statePath);
        Assert.True(first.Ran);
        Assert.True(File.Exists(statePath));

        string mainAfterFirst = File.ReadAllText(Path.Combine(_root, "pack", "pipeline", "main.json"));

        MaintenanceOutcome second = ResourceMaintenance.RunIfNeeded(Path.Combine(_root, "interface.json"), statePath);
        Assert.False(second.Ran);
        Assert.Contains("未更新", second.Reason);
        Assert.Equal(mainAfterFirst, File.ReadAllText(Path.Combine(_root, "pack", "pipeline", "main.json")));
    }

    [Fact]
    public void Maintenance_RunsAgainAfterVersionBump()
    {
        WriteStandardTree();
        string statePath = Path.Combine(_configDirectory, "resource-state.json");
        string interfacePath = Path.Combine(_root, "interface.json");

        ResourceMaintenance.RunIfNeeded(interfacePath, statePath);

        // 新版资源：版本号变了，并带回一个新的走不到的节点
        WriteInterface(version: "v1.1.0");
        WritePipeline("main.json", """
        {
          "A": { "recognition": "DirectHit", "next": ["B"] },
          "B": { "recognition": "TemplateMatch", "template": ["a.png"] },
          "X2": { "recognition": "DirectHit" }
        }
        """);

        MaintenanceOutcome outcome = ResourceMaintenance.RunIfNeeded(interfacePath, statePath);

        Assert.True(outcome.Ran);
        Assert.Equal("v1.1.0", outcome.Version);
        Assert.DoesNotContain("X2", File.ReadAllText(Path.Combine(_root, "pack", "pipeline", "main.json")));
    }

    [Fact]
    public void Maintenance_StatesVersionInFile()
    {
        WriteStandardTree();
        string statePath = Path.Combine(_configDirectory, "resource-state.json");

        ResourceMaintenance.RunIfNeeded(Path.Combine(_root, "interface.json"), statePath);

        string json = File.ReadAllText(statePath);
        Assert.Contains("v1.0.0", json);
        Assert.Contains("fingerprint", json);
    }

    // ---- 图片缓存 ----

    [Fact]
    public void ImageCache_CountsImagesOnly()
    {
        string app = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(app, "debug"));
        File.WriteAllBytes(Path.Combine(app, "debug", "shot-1.bmp"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(app, "debug", "shot-2.png"), new byte[1024]);
        File.WriteAllText(Path.Combine(app, "debug", "run.log"), "keep me");
        Directory.CreateDirectory(Path.Combine(app, "debug", "nested"));
        File.WriteAllBytes(Path.Combine(app, "debug", "nested", "deep.jpg"), new byte[512]);

        ImageCacheSnapshot snapshot = ImageCache.Scan(app);

        Assert.Equal(3, snapshot.FileCount);
        Assert.Equal(3584, snapshot.Bytes);
        Assert.Contains("debug", snapshot.Directories);
    }

    [Fact]
    public void ImageCache_ClearDeletesImagesAndKeepsLogs()
    {
        string app = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(app, "debug"));
        File.WriteAllBytes(Path.Combine(app, "debug", "shot-1.bmp"), new byte[2048]);
        File.WriteAllText(Path.Combine(app, "debug", "run.log"), "keep me");

        ImageCacheClearOutcome outcome = ImageCache.Clear(app);

        Assert.Equal(1, outcome.Deleted);
        Assert.Equal(0, outcome.Failed);
        Assert.False(File.Exists(Path.Combine(app, "debug", "shot-1.bmp")));
        Assert.True(File.Exists(Path.Combine(app, "debug", "run.log")));
        Assert.Equal(0, ImageCache.Scan(app).FileCount);
    }

    [Fact]
    public void ImageCache_ClearOnEmptyIsNotAnError()
    {
        string app = Path.Combine(_root, "app-empty");
        Directory.CreateDirectory(app);

        ImageCacheClearOutcome outcome = ImageCache.Clear(app);

        Assert.Equal(0, outcome.Deleted);
        Assert.Contains("已经是空的", outcome.Summary);
    }

    // ---- 辅助 ----

    private static ResourceAuditResult AuditOf(string set, params CleanupItem[] items) => new()
    {
        InterfacePath = "interface.json",
        ResourceRoot = ".",
        ResourceSetName = set,
        BundlePaths = set switch
        {
            "官服" => ["base", "official"],
            _ => ["base"],
        },
        Items = items,
    };

    private static CleanupItem Item(CleanupCategory category, string path, string[] nodes) => new()
    {
        Id = $"{category}:{path}",
        Category = category,
        Risk = CleanupRisk.Safe,
        RelativePath = path,
        Nodes = nodes,
        Reason = "测试",
    };

    private static ResourceAuditResult WithItems(ResourceAuditResult source, IReadOnlyList<CleanupItem> items) => new()
    {
        InterfacePath = source.InterfacePath,
        ResourceRoot = source.ResourceRoot,
        ResourceSetName = source.ResourceSetName,
        BundlePaths = source.BundlePaths,
        ConfigDirectory = source.ConfigDirectory,
        Items = items,
        HiddenTasks = source.HiddenTasks,
        Problems = source.Problems,
        TotalNodes = source.TotalNodes,
        LiveNodes = source.LiveNodes,
        DeadNodes = source.DeadNodes,
    };
}
