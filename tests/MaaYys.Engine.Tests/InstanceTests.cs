using MaaYys.Engine.Config;
using Xunit;

namespace MaaYys.Engine.Tests;

/// <summary>
/// P2.4 实例配置层测试：模型校验、清单持久化（含损坏恢复）、设备独占登记、可选项降级。
/// 全部走临时目录，不触碰程序目录的真实清单。
/// </summary>
public sealed class InstanceTests : IDisposable
{
    private readonly string _directory;

    public InstanceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "MaaYysTests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_directory);
    }

    private string StorePath => Path.Combine(_directory, "config", "instances.json");

    private InstanceStore NewStore()
    {
        var store = new InstanceStore(StorePath);
        store.Load();
        return store;
    }

    // ---- 模型校验 ----

    [Fact]
    public void Validate_DefaultWin32Instance_ShouldPass()
    {
        var definition = new InstanceDefinition { Name = "实例 1" };

        Assert.Empty(definition.Validate());
    }

    [Theory]
    [InlineData("", "实例名不能为空")]
    [InlineData("   ", "实例名不能为空")]
    public void Validate_EmptyName_ShouldFail(string name, string expectedFragment)
    {
        var definition = new InstanceDefinition { Name = name };

        IReadOnlyList<string> errors = definition.Validate();

        Assert.Contains(errors, e => e.Contains(expectedFragment));
    }

    [Fact]
    public void Validate_TooLongName_ShouldFail()
    {
        var definition = new InstanceDefinition { Name = new string('实', InstanceDefinition.MaxNameLength + 1) };

        Assert.Contains(definition.Validate(), e => e.Contains("不能超过"));
    }

    [Fact]
    public void Validate_InvalidRegex_ShouldFail()
    {
        var definition = new InstanceDefinition { Name = "坏正则", Win32TitleRegex = "阴阳师(（" };

        Assert.Contains(definition.Validate(), e => e.Contains("标题正则非法"));
    }

    [Fact]
    public void Validate_AdbWithoutPort_ShouldFail()
    {
        var definition = new InstanceDefinition
        {
            Name = "模拟器",
            DeviceKind = InstanceDeviceKind.Adb,
            AdbAddress = "127.0.0.1",
        };

        Assert.Contains(definition.Validate(), e => e.Contains("host:port"));
    }

    [Fact]
    public void Validate_UnknownScreencapMethod_ShouldFail()
    {
        var definition = new InstanceDefinition { Name = "未知方法", ScreencapMethod = "Magic" };

        Assert.Contains(definition.Validate(), e => e.Contains("截图方法非法"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public void Validate_FpsOutOfRange_ShouldFail(int fps)
    {
        var definition = new InstanceDefinition { Name = "帧率越界", PreviewFps = fps };

        Assert.Contains(definition.Validate(), e => e.Contains("预览帧率"));
    }

    [Fact]
    public void Validate_AdbWithValidConfig_ShouldPass()
    {
        var definition = new InstanceDefinition
        {
            Name = "模拟器",
            DeviceKind = InstanceDeviceKind.Adb,
            AdbAddress = "127.0.0.1:16384",
            AdbConfig = """{"sn": "emulator-5554"}""",
        };

        Assert.Empty(definition.Validate());
    }

    // ---- 清单持久化 ----

    [Fact]
    public void Load_WithoutFile_ShouldCreateDefaultInstance()
    {
        InstanceStore store = NewStore();

        Assert.Single(store.Instances);
        Assert.Equal("实例 1", store.Instances[0].Name);
        Assert.Equal(store.Instances[0].Id, store.CurrentId);
        Assert.True(File.Exists(StorePath));
    }

    [Fact]
    public void Add_ThreeInstances_ShouldPersistAndSwitchCurrent()
    {
        InstanceStore store = NewStore();
        store.Add();
        store.Add();
        store.Add();

        Assert.Equal(4, store.Instances.Count);

        // 重新加载：名字、顺序、Current 指针都要一致
        InstanceStore reloaded = NewStore();
        Assert.Equal(4, reloaded.Instances.Count);
        Assert.Equal(store.CurrentId, reloaded.CurrentId);
        Assert.Equal(["实例 1", "实例 2", "实例 3", "实例 4"], reloaded.Instances.Select(i => i.Name));
    }

    [Fact]
    public void Rename_DuplicateName_ShouldFail()
    {
        InstanceStore store = NewStore();
        InstanceDefinition second = store.Add();

        bool renamed = store.Rename(second.Id, "实例 1", out string? error);

        Assert.False(renamed);
        Assert.Contains("同名", error);
        Assert.Equal("实例 2", store.Find(second.Id)!.Name);
    }

    [Fact]
    public void Rename_ValidName_ShouldPersist()
    {
        InstanceStore store = NewStore();
        InstanceDefinition first = store.Instances[0];

        Assert.True(store.Rename(first.Id, "官服-大号", out _));

        InstanceStore reloaded = NewStore();
        Assert.Equal("官服-大号", reloaded.Instances[0].Name);
    }

    [Fact]
    public void Remove_LastInstance_ShouldBeRejected()
    {
        InstanceStore store = NewStore();

        bool removed = store.Remove(store.Instances[0].Id, out string? error);

        Assert.False(removed);
        Assert.Contains("至少保留", error);
        Assert.Single(store.Instances);
    }

    [Fact]
    public void Remove_CurrentInstance_ShouldMovePointerToNeighbour()
    {
        InstanceStore store = NewStore();
        store.Add();
        InstanceDefinition third = store.Add();
        store.SetCurrent(third.Id);

        Assert.True(store.Remove(third.Id, out _));

        Assert.Equal(2, store.Instances.Count);
        Assert.NotNull(store.Current);
        Assert.NotEqual(third.Id, store.Current!.Id);
    }

    [Fact]
    public void Duplicate_ShouldCopyFieldsWithNewId()
    {
        InstanceStore store = NewStore();
        InstanceDefinition source = store.Instances[0];
        source.TaskEntries = ["StartUp", "Daily"];
        store.Update(source, out _);

        InstanceDefinition? copy = store.Duplicate(source.Id);

        Assert.NotNull(copy);
        Assert.NotEqual(source.Id, copy!.Id);
        Assert.Equal(["StartUp", "Daily"], copy.TaskEntries);
        Assert.Equal(2, store.Instances.Count);

        // 深拷贝：改副本不影响原实例
        copy.TaskEntries.Add("Extra");
        Assert.Equal(2, store.Find(source.Id)!.TaskEntries.Count);
    }

    [Fact]
    public void Update_FullFields_RoundTripsThroughDisk()
    {
        InstanceStore store = NewStore();
        InstanceDefinition instance = store.Instances[0];
        instance.DeviceKind = InstanceDeviceKind.Adb;
        instance.AdbAddress = "127.0.0.1:16384";
        instance.ScreencapMethod = "FramePool";
        instance.InputMethod = "Seize";
        instance.PreviewFps = 15;
        instance.ResourceName = "bilibili";
        instance.Note = "B 服小号，夜间挂机";
        instance.TaskEntries = ["御魂", "觉醒"];

        Assert.True(store.Update(instance, out _));

        InstanceStore reloaded = NewStore();
        InstanceDefinition restored = reloaded.Instances[0];
        Assert.Equal(InstanceDeviceKind.Adb, restored.DeviceKind);
        Assert.Equal("127.0.0.1:16384", restored.AdbAddress);
        Assert.Equal("Seize", restored.InputMethod);
        Assert.Equal(15, restored.PreviewFps);
        Assert.Equal("bilibili", restored.ResourceName);
        Assert.Equal("B 服小号，夜间挂机", restored.Note);
        Assert.Equal(["御魂", "觉醒"], restored.TaskEntries);
    }

    [Fact]
    public void Load_CorruptedFile_ShouldBackupAndRebuild()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, "{ this is not json");

        InstanceStore store = NewStore();

        Assert.Single(store.Instances);
        Assert.NotEmpty(store.LoadErrors);
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(StorePath)!, "*.bad-*"), f => f.Contains("instances.json"));
    }

    [Fact]
    public void Save_ShouldBeAtomic_NoTempFileLeftBehind()
    {
        InstanceStore store = NewStore();
        store.Add();
        store.Add();

        Assert.True(File.Exists(StorePath));
        Assert.False(File.Exists(StorePath + ".tmp"));

        // 第二次保存后应留下上一版备份，便于手工回滚
        store.Save();
        Assert.True(File.Exists(StorePath + ".bak"));
    }

    [Fact]
    public void Load_DuplicateIds_ShouldReassign()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """
            {
              "schema_version": 1,
              "current_id": "dup",
              "instances": [
                { "id": "dup", "name": "A" },
                { "id": "dup", "name": "B" }
              ]
            }
            """);

        InstanceStore store = NewStore();

        Assert.Equal(2, store.Instances.Count);
        Assert.Equal(2, store.Instances.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void Load_HigherSchemaVersion_ShouldWarnButKeepData()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, """
            { "schema_version": 99, "current_id": "x", "instances": [ { "id": "x", "name": "未来实例" } ] }
            """);

        InstanceStore store = NewStore();

        Assert.Equal("未来实例", store.Instances[0].Name);
        Assert.Contains(store.LoadErrors, e => e.Contains("版本"));
    }

    // ---- 设备独占 ----

    [Fact]
    public void DeviceLease_SecondOwner_ShouldBeRejected()
    {
        string key = $"win32:0x{Guid.NewGuid().ToString("N")[..8]}";
        string ownerA = Guid.NewGuid().ToString("N");
        string ownerB = Guid.NewGuid().ToString("N");

        try
        {
            Assert.True(DeviceLease.TryAcquire(key, ownerA, out _));
            Assert.False(DeviceLease.TryAcquire(key, ownerB, out string? holder));
            Assert.Equal(ownerA, holder);

            // 同一实例重复登记是幂等的
            Assert.True(DeviceLease.TryAcquire(key, ownerA, out _));

            DeviceLease.Release(key, ownerA);
            Assert.True(DeviceLease.TryAcquire(key, ownerB, out _));
        }
        finally
        {
            DeviceLease.ReleaseAll(ownerA);
            DeviceLease.ReleaseAll(ownerB);
        }
    }

    [Fact]
    public void DeviceLease_ReleaseAll_ShouldFreeEveryDevice()
    {
        string owner = Guid.NewGuid().ToString("N");
        string key1 = $"adb:{owner}-1";
        string key2 = $"adb:{owner}-2";

        DeviceLease.TryAcquire(key1, owner, out _);
        DeviceLease.TryAcquire(key2, owner, out _);
        Assert.Equal(2, DeviceLease.Snapshot().Count(p => p.Value == owner));

        DeviceLease.ReleaseAll(owner);

        Assert.Null(DeviceLease.HolderOf(key1));
        Assert.Null(DeviceLease.HolderOf(key2));
    }

    // ---- 可选项降级 ----

    [Fact]
    public void Catalog_WithoutInterfaceJson_ShouldFallBack()
    {
        InstanceCatalog catalog = InstanceCatalog.Load(Path.Combine(_directory, "missing.json"));

        Assert.False(catalog.Loaded);
        Assert.Equal(["base"], catalog.ResourceNames);
        Assert.Empty(catalog.Tasks);
        Assert.NotEmpty(catalog.Errors);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}
