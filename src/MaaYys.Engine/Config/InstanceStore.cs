using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaaYys.Engine.Config;

/// <summary>
/// 实例清单持久化（<c>config/instances.json</c>）。
/// 设计要点：
/// - 便携优先：默认存放于程序目录，拷走整个目录即完成迁移；
/// - 损坏不丢数据：解析失败先把原文件改名备份（<c>*.bad-时间戳</c>）再重建默认实例；
/// - 原子写：先写 <c>.tmp</c> 再替换，避免掉电/崩溃留下半个 JSON；
/// - 每次变更立即落盘（文件仅几 KB），不给 UI 留「忘记保存」的坑。
/// </summary>
public sealed class InstanceStore
{
    /// <summary>清单文件名。</summary>
    public const string FileName = "instances.json";

    /// <summary>当前清单格式版本（P2.6 迁移入口，读写时校验）。</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly object _gate = new();
    private readonly List<InstanceDefinition> _instances = [];

    public InstanceStore(string? path = null)
    {
        Path = path ?? DefaultPath;
    }

    /// <summary>默认清单路径：程序目录下 <c>config/instances.json</c>（P1.6 评审：便携优先）。</summary>
    public static string DefaultPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config", FileName);

    /// <summary>清单文件绝对路径。</summary>
    public string Path { get; }

    /// <summary>全部实例（按清单顺序，标签条顺序即此顺序）。</summary>
    public IReadOnlyList<InstanceDefinition> Instances
    {
        get
        {
            lock (_gate)
            {
                return _instances.ToList();
            }
        }
    }

    /// <summary>当前激活实例 Id（Current 指针）。</summary>
    public string? CurrentId { get; private set; }

    /// <summary>当前激活实例；清单为空时为 null。</summary>
    public InstanceDefinition? Current
    {
        get
        {
            lock (_gate)
            {
                return _instances.FirstOrDefault(i => i.Id == CurrentId) ?? _instances.FirstOrDefault();
            }
        }
    }

    /// <summary>加载期问题（文件缺失/损坏/版本不符），供 UI 提示；不影响可用性。</summary>
    public List<string> LoadErrors { get; } = [];

    /// <summary>清单内容变化（增删改/切换 Current），UI 据此刷新标签条。</summary>
    public event EventHandler? Changed;

    // ---- 加载 / 保存 ----

    /// <summary>
    /// 读取清单。文件不存在 → 建默认实例；解析失败 → 备份后重建。
    /// 任何情况下返回后 <see cref="Instances"/> 至少有一项。
    /// </summary>
    public void Load()
    {
        lock (_gate)
        {
            _instances.Clear();
            LoadErrors.Clear();
            CurrentId = null;

            if (!File.Exists(Path))
            {
                _instances.Add(CreateDefault());
                CurrentId = _instances[0].Id;
                SaveCore();
                return;
            }

            Document? document = null;
            try
            {
                document = JsonSerializer.Deserialize<Document>(File.ReadAllText(Path), SerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                string backup = BackupBrokenFile();
                LoadErrors.Add($"实例清单解析失败（{ex.Message}），已备份为 {System.IO.Path.GetFileName(backup)} 并重建默认实例");
            }

            if (document?.Instances is { Count: > 0 } loaded)
            {
                if (document.SchemaVersion > SchemaVersion)
                {
                    LoadErrors.Add($"实例清单版本 {document.SchemaVersion} 高于程序支持的 {SchemaVersion}，已按兼容方式读取");
                }

                foreach (InstanceDefinition instance in loaded)
                {
                    // 容错：Id 缺失/重复的条目重新分配，避免后续按 Id 索引时互相覆盖
                    if (string.IsNullOrWhiteSpace(instance.Id) || _instances.Any(i => i.Id == instance.Id))
                    {
                        instance.Id = InstanceDefinition.NewId();
                    }

                    if (string.IsNullOrWhiteSpace(instance.Name))
                    {
                        instance.Name = $"实例 {_instances.Count + 1}";
                    }

                    instance.TaskEntries ??= [];
                    _instances.Add(instance);
                }

                CurrentId = document.CurrentId is { } id && _instances.Any(i => i.Id == id)
                    ? id
                    : _instances[0].Id;
                return;
            }

            _instances.Add(CreateDefault());
            CurrentId = _instances[0].Id;
            SaveCore();
        }
    }

    /// <summary>写盘（原子替换 + 上一版备份）。</summary>
    public void Save()
    {
        lock (_gate)
        {
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 变更操作 ----

    /// <summary>新建实例（自动取不重复的名字），返回新实例。</summary>
    public InstanceDefinition Add(string? name = null)
    {
        InstanceDefinition created;
        lock (_gate)
        {
            created = new InstanceDefinition { Name = UniqueName(name ?? NextName()) };
            _instances.Add(created);
            CurrentId = created.Id;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return created;
    }

    /// <summary>复制实例（深拷贝 + 新 Id + 新名字），返回副本。</summary>
    public InstanceDefinition? Duplicate(string id)
    {
        InstanceDefinition? copy = null;
        lock (_gate)
        {
            InstanceDefinition? source = _instances.FirstOrDefault(i => i.Id == id);
            if (source is null)
            {
                return null;
            }

            copy = source.Clone(UniqueName($"{source.Name} 副本"));
            _instances.Insert(_instances.IndexOf(source) + 1, copy);
            CurrentId = copy.Id;
            SaveCore();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return copy;
    }

    /// <summary>
    /// 删除实例。保留至少一个——删空后 UI 与标签条都无从下手。
    /// 删除的是当前实例时，当前指针落到相邻项。
    /// </summary>
    public bool Remove(string id, out string? error)
    {
        error = null;
        bool removed = false;

        lock (_gate)
        {
            if (_instances.Count <= 1)
            {
                error = "至少保留一个实例";
                return false;
            }

            int index = _instances.FindIndex(i => i.Id == id);
            if (index < 0)
            {
                error = "实例不存在";
                return false;
            }

            _instances.RemoveAt(index);
            if (CurrentId == id)
            {
                CurrentId = _instances[Math.Min(index, _instances.Count - 1)].Id;
            }

            SaveCore();
            removed = true;
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>改名（重名检查）。</summary>
    public bool Rename(string id, string newName, out string? error)
    {
        error = null;
        bool renamed = false;

        lock (_gate)
        {
            InstanceDefinition? instance = _instances.FirstOrDefault(i => i.Id == id);
            if (instance is null)
            {
                error = "实例不存在";
                return false;
            }

            string trimmed = newName.Trim();
            if (trimmed.Length == 0)
            {
                error = "实例名不能为空";
                return false;
            }

            if (_instances.Any(i => i.Id != id && string.Equals(i.Name, trimmed, StringComparison.Ordinal)))
            {
                error = $"已存在同名实例「{trimmed}」";
                return false;
            }

            instance.Name = trimmed;
            instance.UpdatedAt = DateTimeOffset.Now;
            SaveCore();
            renamed = true;
        }

        if (renamed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return renamed;
    }

    /// <summary>把编辑结果写回清单（不存在则忽略）。</summary>
    public bool Update(InstanceDefinition updated, out string? error)
    {
        error = null;
        bool applied = false;

        lock (_gate)
        {
            InstanceDefinition? existing = _instances.FirstOrDefault(i => i.Id == updated.Id);
            if (existing is null)
            {
                error = "实例不存在";
                return false;
            }

            if (_instances.Any(i => i.Id != updated.Id && string.Equals(i.Name, updated.Name, StringComparison.Ordinal)))
            {
                error = $"已存在同名实例「{updated.Name}」";
                return false;
            }

            existing.CopyEditableFrom(updated);
            existing.Win32WindowHandle = updated.Win32WindowHandle;
            SaveCore();
            applied = true;
        }

        if (applied)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return applied;
    }

    /// <summary>切换当前实例。</summary>
    public bool SetCurrent(string id)
    {
        bool changed = false;
        lock (_gate)
        {
            if (CurrentId != id && _instances.Any(i => i.Id == id))
            {
                CurrentId = id;
                SaveCore();
                changed = true;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }

    /// <summary>按 Id 查找。</summary>
    public InstanceDefinition? Find(string id)
    {
        lock (_gate)
        {
            return _instances.FirstOrDefault(i => i.Id == id);
        }
    }

    /// <summary>生成不与现有实例重名的名字。</summary>
    public string UniqueName(string baseName)
    {
        lock (_gate)
        {
            string name = baseName.Trim();
            if (name.Length == 0)
            {
                name = "实例";
            }

            if (_instances.All(i => !string.Equals(i.Name, name, StringComparison.Ordinal)))
            {
                return name;
            }

            for (int n = 2; ; n++)
            {
                string candidate = $"{name} {n}";
                if (_instances.All(i => !string.Equals(i.Name, candidate, StringComparison.Ordinal)))
                {
                    return candidate;
                }
            }
        }
    }

    // ---- 内部 ----

    private string NextName()
    {
        for (int n = 1; ; n++)
        {
            string candidate = $"实例 {n}";
            if (_instances.All(i => !string.Equals(i.Name, candidate, StringComparison.Ordinal)))
            {
                return candidate;
            }
        }
    }

    private InstanceDefinition CreateDefault() => new()
    {
        Name = "实例 1",
        DeviceKind = InstanceDeviceKind.Win32,
        Win32TitleRegex = "阴阳师",
        ScreencapMethod = "FramePool",
        InputMethod = "SendMessage",
        PreviewFps = 5,
    };

    /// <summary>原子写：tmp → Move(overwrite)。上一版保留为 .bak 便于手工回滚。</summary>
    private void SaveCore()
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        var document = new Document
        {
            SchemaVersion = SchemaVersion,
            CurrentId = CurrentId,
            Instances = _instances,
        };

        string json = JsonSerializer.Serialize(document, SerializerOptions);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));

        if (File.Exists(Path))
        {
            try
            {
                File.Copy(Path, Path + ".bak", overwrite: true);
            }
            catch (IOException)
            {
                // 备份失败不阻断主流程（只影响手工回滚能力）
            }
        }

        File.Move(temp, Path, overwrite: true);
    }

    private string BackupBrokenFile()
    {
        string target = $"{Path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(Path, target, overwrite: true);
        }
        catch (IOException)
        {
            return Path;
        }

        return target;
    }

    /// <summary>清单文件 DTO（保持与 instances.json 顶层结构一致）。</summary>
    private sealed class Document
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = InstanceStore.SchemaVersion;

        [JsonPropertyName("current_id")]
        public string? CurrentId { get; set; }

        [JsonPropertyName("instances")]
        public List<InstanceDefinition> Instances { get; set; } = [];
    }
}
