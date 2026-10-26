using MaaYys.Engine.Config;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// 配置组持久化测试（P2.6）：命名组 + Current 指针 + ConfigVersion + 迁移 + 导入导出。
/// </summary>
public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ConfigurationStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "maayys-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "maayys.json");
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

    private ConfigurationStore NewStore() => new(_path);

    private ConfigurationStore LoadedStore(IReadOnlyList<InstanceDefinition>? migration = null)
    {
        ConfigurationStore store = NewStore();
        store.Load(migration);
        return store;
    }

    // ---- 基础加载 ----

    [Fact]
    public void Load_WhenFileMissing_ShouldCreateDefaultGroupAndWriteFile()
    {
        ConfigurationStore store = LoadedStore();

        Assert.Single(store.Configurations);
        Assert.Equal(ConfigurationStore.DefaultName, store.Current);
        Assert.NotNull(store.CurrentConfiguration);
        Assert.Empty(store.LoadErrors);
        // 首次加载即落盘，避免「配置组只存在内存里」
        Assert.True(File.Exists(_path));

        string json = File.ReadAllText(_path);
        Assert.Contains("\"config_version\"", json);
        Assert.Contains("\"current\"", json);
        Assert.Contains("\"configurations\"", json);
    }

    [Fact]
    public void Save_AndReload_ShouldPreserveGroupsTasksAndCurrent()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("日常");
        TaskConfiguration daily = store.CurrentConfiguration!;
        daily.Tasks.Add(new TaskSelection
        {
            Name = "自动御魂",
            Enabled = true,
            Options = { ["层数"] = "10", ["加成"] = "开" },
        });
        daily.Tasks.Add(new TaskSelection { Name = "结界突破", Enabled = false });
        store.Touch(daily.Name);

        // 模拟「重启程序」
        ConfigurationStore reloaded = LoadedStore();

        Assert.Equal(2, reloaded.Configurations.Count);
        Assert.Equal("日常", reloaded.Current);

        TaskConfiguration restored = reloaded.Find("日常")!;
        Assert.Equal(2, restored.Tasks.Count);
        Assert.Equal(1, restored.EnabledCount);

        TaskSelection soul = restored.FindTask("自动御魂")!;
        Assert.True(soul.Enabled);
        Assert.Equal("10", soul.GetOption("层数"));
        Assert.Equal("开", soul.GetOption("加成"));

        Assert.False(restored.FindTask("结界突破")!.Enabled);
    }

    [Fact]
    public void Current_ShouldFallBackToFirst_WhenPointerMissing()
    {
        var store = new ConfigurationStore(_path);
        File.WriteAllText(_path, """
            {
              "config_version": 1,
              "current": "不存在的组",
              "configurations": {
                "甲": { "name": "甲", "tasks": [] },
                "乙": { "name": "乙", "tasks": [] }
              }
            }
            """);

        store.Load();

        Assert.Equal("甲", store.Current);
        Assert.Equal(["甲", "乙"], store.Configurations.Select(c => c.Name));
    }

    // ---- 增删改 ----

    [Fact]
    public void Add_ShouldAutoDeduplicateName_AndSwithCurrent()
    {
        ConfigurationStore store = LoadedStore();

        store.Add("日常");
        TaskConfiguration second = store.Add("日常");

        Assert.Equal("日常 2", second.Name);
        Assert.Equal("日常 2", store.Current);
        Assert.Equal(3, store.Configurations.Count);
    }

    [Fact]
    public void Duplicate_ShouldDeepCopyTasks_AndInsertAfterSource()
    {
        ConfigurationStore store = LoadedStore();
        TaskConfiguration source = store.CurrentConfiguration!;
        source.Tasks.Add(new TaskSelection { Name = "自动御魂", Options = { ["层数"] = "8" } });
        store.Touch(source.Name);

        TaskConfiguration? copy = store.Duplicate(source.Name);

        Assert.NotNull(copy);
        Assert.Equal("默认配置 副本", copy!.Name);
        Assert.Equal(source.Name, store.Configurations[0].Name);
        Assert.Equal(copy.Name, store.Configurations[1].Name);

        // 深拷贝：改副本不影响原组
        copy.Tasks[0].SetOption("层数", "11");
        Assert.Equal("8", source.Tasks[0].GetOption("层数"));
    }

    [Fact]
    public void Remove_ShouldMoveCurrentToNeighbour()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("乙");
        store.Add("丙");

        Assert.True(store.Remove("丙", out string? error));
        Assert.Null(error);
        Assert.Equal("乙", store.Current);

        // 允许删空
        Assert.True(store.Remove("乙", out _));
        Assert.True(store.Remove(ConfigurationStore.DefaultName, out _));
        Assert.Empty(store.Configurations);
        Assert.Null(store.Current);
        Assert.Null(store.CurrentConfiguration);
    }

    [Fact]
    public void Remove_WhenMissing_ShouldReportError()
    {
        ConfigurationStore store = LoadedStore();

        Assert.False(store.Remove("没有这个组", out string? error));
        Assert.Equal("配置组不存在", error);
    }

    [Fact]
    public void Rename_ShouldRejectDuplicateAndEmpty_AndMoveCurrent()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("乙");
        store.SetCurrent(ConfigurationStore.DefaultName);

        Assert.False(store.Rename(ConfigurationStore.DefaultName, "乙", out string? dupError));
        Assert.Contains("同名", dupError);

        Assert.False(store.Rename(ConfigurationStore.DefaultName, "   ", out string? emptyError));
        Assert.Equal("配置组名不能为空", emptyError);

        Assert.True(store.Rename(ConfigurationStore.DefaultName, "主配置", out _));
        Assert.Equal("主配置", store.Current);
        Assert.NotNull(store.Find("主配置"));
        Assert.Null(store.Find(ConfigurationStore.DefaultName));
    }

    [Fact]
    public void SetCurrent_WhenMissing_ShouldBeNoOp()
    {
        ConfigurationStore store = LoadedStore();
        string before = store.Current!;

        Assert.False(store.SetCurrent("不存在"));
        Assert.Equal(before, store.Current);
    }

    // ---- 容错 ----

    [Fact]
    public void Load_WhenJsonBroken_ShouldBackupAndRebuild()
    {
        File.WriteAllText(_path, "{ 这不是合法 JSON ");

        ConfigurationStore store = LoadedStore();

        Assert.Single(store.Configurations);
        Assert.NotEmpty(store.LoadErrors);
        Assert.Contains(store.LoadErrors, e => e.Contains("解析失败"));

        // 原文件被改名备份，坏数据不丢
        string[] backups = Directory.GetFiles(_root, "maayys.json.bad-*");
        Assert.Single(backups);
        Assert.Contains("这不是合法 JSON", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void Load_WhenVersionHigher_ShouldWarnButKeepData()
    {
        File.WriteAllText(_path, """
            {
              "config_version": 99,
              "current": "未来配置",
              "configurations": {
                "未来配置": { "name": "未来配置", "tasks": [{ "name": "自动御魂", "enabled": true }] }
              }
            }
            """);

        ConfigurationStore store = LoadedStore();

        Assert.Contains(store.LoadErrors, e => e.Contains("高于程序支持"));
        Assert.Equal("未来配置", store.Current);
        Assert.Single(store.Find("未来配置")!.Tasks);
    }

    [Fact]
    public void Load_ShouldDropEmptyNamesAndDeduplicateTasks()
    {
        File.WriteAllText(_path, """
            {
              "config_version": 1,
              "current": "甲",
              "configurations": {
                "": { "name": "", "tasks": [] },
                "甲": {
                  "name": "名字和键不一致",
                  "tasks": [
                    { "name": "自动御魂" },
                    { "name": "自动御魂" },
                    { "name": "  " }
                  ]
                }
              }
            }
            """);

        ConfigurationStore store = LoadedStore();

        Assert.Single(store.Configurations);
        TaskConfiguration group = store.Configurations[0];
        Assert.Equal("甲", group.Name);
        Assert.Single(group.Tasks);
        Assert.Contains(store.LoadErrors, e => e.Contains("重复"));
        Assert.Contains(store.LoadErrors, e => e.Contains("没有名字"));
    }

    // ---- 迁移 ----

    [Fact]
    public void Migrate_ShouldLiftInstanceTaskEntries_AndWriteBackName()
    {
        var instances = new List<InstanceDefinition>
        {
            new() { Id = "a", Name = "官服-大号", TaskEntries = ["打开游戏", "自动御魂", "自动御魂"] },
            new() { Id = "b", Name = "B站-小号", TaskEntries = ["打开游戏"] },
            new() { Id = "c", Name = "空配置号", TaskEntries = [] },
        };

        ConfigurationStore store = NewStore();
        bool touched = store.Load(instances);

        Assert.True(touched);
        Assert.Equal(2, store.Configurations.Count);      // 空任务实例不产生空组

        TaskConfiguration first = store.Find("官服-大号")!;
        Assert.Equal(["打开游戏", "自动御魂"], first.Tasks.Select(t => t.Name));   // 去重
        Assert.All(first.Tasks, t => Assert.True(t.Enabled));

        Assert.Equal("官服-大号", instances[0].ConfigurationName);
        Assert.Equal("B站-小号", instances[1].ConfigurationName);
        Assert.Equal(string.Empty, instances[2].ConfigurationName);

        // 迁移结果已落盘
        ConfigurationStore reloaded = LoadedStore();
        Assert.Equal(2, reloaded.Configurations.Count);
    }

    [Fact]
    public void Migrate_ShouldBeIdempotent()
    {
        var instances = new List<InstanceDefinition>
        {
            new() { Id = "a", Name = "官服-大号", TaskEntries = ["打开游戏"] },
        };

        ConfigurationStore first = NewStore();
        first.Load(instances);

        // 第二次加载：实例已带 ConfigurationName，不应再生成新组
        ConfigurationStore second = NewStore();
        bool touched = second.Load(instances);

        Assert.False(touched);
        Assert.Single(second.Configurations);
        Assert.Equal("官服-大号", instances[0].ConfigurationName);
    }

    [Fact]
    public void Migrate_ShouldNotOverwriteExistingConfigurations()
    {
        File.WriteAllText(_path, """
            { "config_version": 1, "current": "手工配置",
              "configurations": { "手工配置": { "name": "手工配置", "tasks": [] } } }
            """);

        var instances = new List<InstanceDefinition>
        {
            new() { Id = "a", Name = "官服-大号", TaskEntries = ["打开游戏"] },
        };

        ConfigurationStore store = NewStore();
        bool touched = store.Load(instances);

        Assert.False(touched);
        Assert.Single(store.Configurations);
        Assert.Null(store.Find("官服-大号"));
    }

    // ---- 导入导出 ----

    [Fact]
    public void ExportImport_ShouldRoundTripSingleGroup()
    {
        ConfigurationStore store = LoadedStore();
        TaskConfiguration group = store.CurrentConfiguration!;
        group.Tasks.Add(new TaskSelection
        {
            Name = "自动御魂",
            Options = { ["层数"] = "10" },
        });
        store.Touch(group.Name);

        string file = Path.Combine(_root, "export-single.json");
        Assert.True(store.Export(file, group.Name));

        var target = new ConfigurationStore(Path.Combine(_root, "other.json"));
        target.Load();
        ImportResult result = target.Import(file);

        Assert.True(result.Success);
        TaskConfiguration imported = target.Find(result.Imported[0])!;
        Assert.Equal("自动御魂", imported.Tasks[0].Name);
        Assert.Equal("10", imported.Tasks[0].GetOption("层数"));
    }

    [Fact]
    public void ExportImport_ShouldRoundTripWholeDocument()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("乙");
        string file = Path.Combine(_root, "export-all.json");
        Assert.True(store.Export(file));

        var target = new ConfigurationStore(Path.Combine(_root, "other.json"));
        target.Load();
        ImportResult result = target.Import(file);

        Assert.True(result.Success);
        Assert.Equal(2, result.Imported.Count);
        Assert.Equal(3, target.Configurations.Count);   // 目标自身的默认组 + 导入的两组
    }

    [Fact]
    public void Import_WhenNameCollides_ShouldAutoRename()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("日常");
        string file = Path.Combine(_root, "collide.json");
        Assert.True(store.Export(file, "日常"));

        ImportResult result = store.Import(file);

        Assert.True(result.Success);
        Assert.Equal("日常 2", result.Imported[0]);
    }

    [Fact]
    public void Import_MaaGuiNewFormat_ShouldReadTaskQueue()
    {
        string file = Path.Combine(_root, "gui.new.json");
        File.WriteAllText(file, """
            {
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "StartUpTask", "Name": "开始唤醒", "IsEnable": false },
                    { "$type": "FightTask", "Name": "刷理智", "IsEnable": true }
                  ],
                  "TaskSelectedIndex": 0
                }
              },
              "ConfigVersion": 1,
              "Current": "Default"
            }
            """);

        ConfigurationStore store = LoadedStore();
        ImportResult result = store.Import(file);

        Assert.True(result.Success);
        Assert.Equal("Default", result.Imported[0]);
        Assert.Contains(result.Errors, e => e.Contains("MAA gui.new.json"));

        TaskConfiguration group = store.Find("Default")!;
        Assert.Equal(2, group.Tasks.Count);
        Assert.False(group.FindTask("开始唤醒")!.Enabled);
        Assert.True(group.FindTask("刷理智")!.Enabled);
    }

    [Fact]
    public void Import_UnrecognizedFormat_ShouldFail()
    {
        string file = Path.Combine(_root, "weird.json");
        File.WriteAllText(file, """{ "hello": "world" }""");

        ConfigurationStore store = LoadedStore();
        ImportResult result = store.Import(file);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("无法识别", result.Error);
    }

    [Fact]
    public void Import_MissingFile_ShouldFail()
    {
        ConfigurationStore store = LoadedStore();
        ImportResult result = store.Import(Path.Combine(_root, "nope.json"));

        Assert.False(result.Success);
        Assert.Contains("文件不存在", result.Error);
    }

    [Fact]
    public void ToJson_ShouldEmitConfigurationsAsObject()
    {
        ConfigurationStore store = LoadedStore();
        store.Add("乙");

        string json = store.ToJson();

        // configurations 是对象而非数组（与 MAA / 人类阅读习惯一致）
        Assert.Contains("\"configurations\": {", json);
        Assert.Contains("\"乙\": {", json);
        Assert.DoesNotContain("\"configurations\": [", json);
    }
}
