using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaaYys.Engine.Resources;

/// <summary>一个节点最终来自哪个 bundle 的哪个文件（用于「清理时该改哪份文件」的定位）。</summary>
public sealed record PipelineNodeOrigin(string BundleRoot, string RelativeFile, int BundleIndex)
{
    public override string ToString() => RelativeFile;
}

/// <summary>
/// 多 bundle 的 pipeline 合并结果。合并是在<b>临时目录</b>里落盘的一份「等价视图」，
/// 只用于静态分析（可达性、死节点），不会改动任何原始资源文件。
/// </summary>
public sealed class MergedPipelineStage : IDisposable
{
    private readonly string _root;

    internal MergedPipelineStage(
        string root,
        string pipelineDirectory,
        IReadOnlyList<string> bundleRoots,
        IReadOnlyDictionary<string, PipelineNodeOrigin> nodeOrigins,
        IReadOnlyList<string> parseFailures)
    {
        _root = root;
        PipelineDirectory = pipelineDirectory;
        BundleRoots = bundleRoots;
        NodeOrigins = nodeOrigins;
        ParseFailures = parseFailures;
    }

    /// <summary>临时目录下的 <c>pipeline/</c>。</summary>
    public string PipelineDirectory { get; }

    /// <summary>参与合并的 bundle 根目录（按加载顺序）。</summary>
    public IReadOnlyList<string> BundleRoots { get; }

    /// <summary>节点名 → 首次定义它的文件（后续 bundle 只覆盖字段，不改归属）。</summary>
    public IReadOnlyDictionary<string, PipelineNodeOrigin> NodeOrigins { get; }

    /// <summary>合并阶段解析失败的文件（交给 <see cref="PipelineInventory"/> 复扫时还会再报一次）。</summary>
    public IReadOnlyList<string> ParseFailures { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响功能（下次系统清理会带走）
        }
    }
}

/// <summary>
/// 把资源链上多个 bundle 的 pipeline 合并成一份视图。
/// <para>
/// <b>合并粒度是「节点」，不是「文件」</b>：框架按 bundle 顺序加载，同名<b>节点</b>由后加载者
/// 按<b>字段</b>覆盖先加载者；两个 bundle 里相对路径相同的 json 并不会整份替换
/// （base 的 <c>启动游戏.json</c> 有 15 个节点，渠道层同名文件只重定义 1 个，其余 14 个仍然生效）。
/// </para>
/// <para>
/// 这套语义是被 MaaResourceGetNodeData 实测确认过的（见 docs/P3.1-pipeline自检报告.md §3.1），
/// 所以合成视图可以放心当作「框架眼里的最终状态」用于死节点判定。
/// </para>
/// </summary>
public static class PipelineBundleMerger
{
    /// <summary>把资源链合并到系统临时目录，返回等价视图。调用方负责 <c>Dispose</c>。</summary>
    public static MergedPipelineStage Stage(IEnumerable<string> bundlePaths)
    {
        ArgumentNullException.ThrowIfNull(bundlePaths);
        List<string> bundles = [.. bundlePaths];

        string root = Path.Combine(Path.GetTempPath(), "maayys-pipeline-stage-" + Guid.NewGuid().ToString("N")[..8]);
        string pipelineDirectory = Path.Combine(root, "pipeline");
        Directory.CreateDirectory(pipelineDirectory);

        // 相对路径 → 该文件的合并后内容（节点按首次出现顺序排列）
        Dictionary<string, JsonObject> files = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PipelineNodeOrigin> origins = new(StringComparer.Ordinal);
        List<string> parseFailures = [];

        for (int bundleIndex = 0; bundleIndex < bundles.Count; bundleIndex++)
        {
            string bundleRoot = bundles[bundleIndex];
            string source = Path.Combine(bundleRoot, "pipeline");
            if (!Directory.Exists(source))
            {
                continue;
            }

            string[] jsonFiles = Directory.GetFiles(source, "*.json", SearchOption.AllDirectories);
            Array.Sort(jsonFiles, StringComparer.OrdinalIgnoreCase);

            foreach (string file in jsonFiles)
            {
                string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                JsonObject? incoming = ReadObject(file);
                if (incoming is null)
                {
                    parseFailures.Add(relative);
                    continue;
                }

                if (!files.TryGetValue(relative, out JsonObject? target))
                {
                    target = new JsonObject();
                    files[relative] = target;
                }

                foreach (KeyValuePair<string, JsonNode?> property in incoming)
                {
                    origins.TryAdd(property.Key, new PipelineNodeOrigin(bundleRoot, relative, bundleIndex));

                    if (target[property.Key] is JsonObject existingNode && property.Value is JsonObject incomingNode)
                    {
                        // 字段级合并：后加载 bundle 只覆盖它写了的字段，其余保留
                        foreach (KeyValuePair<string, JsonNode?> field in incomingNode)
                        {
                            existingNode[field.Key] = field.Value?.DeepClone();
                        }
                    }
                    else
                    {
                        target[property.Key] = property.Value?.DeepClone();
                    }
                }
            }
        }

        JsonSerializerOptions writeOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        foreach ((string relative, JsonObject content) in files)
        {
            string destination = Path.Combine(pipelineDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, content.ToJsonString(writeOptions));
        }

        return new MergedPipelineStage(root, pipelineDirectory, bundles, origins, parseFailures);
    }

    private static JsonObject? ReadObject(string file)
    {
        try
        {
            return JsonNode.Parse(
                File.ReadAllText(file),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 解析失败由 PipelineInventory 负责报错（它会给出文件与原因）
            return null;
        }
    }
}
