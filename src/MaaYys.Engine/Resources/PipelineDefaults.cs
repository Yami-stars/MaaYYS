using System.Text.Json;

namespace MaaYys.Engine.Resources;

/// <summary>
/// 资源包根目录下 <c>default_pipeline.json</c> 的 <c>Default</c> 段。
/// <para>
/// 这不是可有可无的配置：MaaYYs 的 <c>Default</c> 里写了
/// <c>"on_error": ["[JumpBack]悬赏封印_协助战斗1"]</c>，等于给<b>每一个</b>节点都挂了一条
/// 兜底错误处理边。不读它，就会把 <c>debug.json</c> 里那条错误处理链误判成死节点。
/// </para>
/// </summary>
public sealed class PipelineDefaults
{
    private static readonly string[] RefFields = ["next", "on_error"];

    public static PipelineDefaults None { get; } = new();

    /// <summary>默认 <c>next</c>（节点自己没写 next 时生效）。</summary>
    public IReadOnlyList<string> Next { get; private init; } = [];

    /// <summary>默认 <c>on_error</c>（节点自己没写 on_error 时生效）。</summary>
    public IReadOnlyList<string> OnError { get; private init; } = [];

    /// <summary>默认 <c>next</c> 里引用的锚点名。</summary>
    public IReadOnlyList<string> NextAnchors { get; private init; } = [];

    /// <summary>默认 <c>on_error</c> 里引用的锚点名。</summary>
    public IReadOnlyList<string> OnErrorAnchors { get; private init; } = [];

    public bool DeclaresNext { get; private init; }

    public bool DeclaresOnError { get; private init; }

    /// <summary>来源文件（用于诊断定位），可能为 null。</summary>
    public string? Source { get; private init; }

    public bool IsEmpty => !DeclaresNext && !DeclaresOnError;

    public string Summary
    {
        get
        {
            if (IsEmpty)
            {
                return "无默认值";
            }

            List<string> parts = [];
            if (DeclaresNext)
            {
                parts.Add($"next=[{string.Join(", ", Next)}]");
            }

            if (DeclaresOnError)
            {
                parts.Add($"on_error=[{string.Join(", ", OnError)}]");
            }

            return string.Join("；", parts);
        }
    }

    /// <summary>
    /// 读取一个 bundle 根目录的 <c>default_pipeline.json</c>；文件不存在返回 <see cref="None"/>。
    /// 只解析 <c>Default</c> 段——算法/动作段的默认参数不影响图结构，但若里面写了
    /// <c>next</c>/<c>on_error</c> 会以 <paramref name="warnings"/> 报出（本工具不模拟那种继承）。
    /// </summary>
    public static PipelineDefaults Load(string defaultPipelineFile, List<string>? warnings = null)
    {
        if (!File.Exists(defaultPipelineFile))
        {
            return None;
        }

        JsonDocument document;
        try
        {
            using FileStream stream = File.OpenRead(defaultPipelineFile);
            document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings?.Add($"default_pipeline.json 解析失败：{ex.Message}");
            return None;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Default", out JsonElement defaults)
                || defaults.ValueKind != JsonValueKind.Object)
            {
                return None;
            }

            // 算法/动作段里出现引用字段时提示：本工具只模拟 Default 段
            foreach (JsonProperty section in document.RootElement.EnumerateObject())
            {
                if (section.NameEquals("Default") || section.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (string field in RefFields)
                {
                    if (section.Value.TryGetProperty(field, out _))
                    {
                        warnings?.Add(
                            $"default_pipeline.json 的 `{section.Name}` 段里写了 `{field}`，"
                            + "本工具只模拟 Default 段，可达性分析可能偏保守");
                    }
                }
            }

            return FromElement(defaults, defaultPipelineFile);
        }
    }

    /// <summary>从 <c>Default</c> 对象构造。</summary>
    internal static PipelineDefaults FromElement(JsonElement defaults, string? source)
    {
        List<string> nextAnchors = [];
        List<string> onErrorAnchors = [];
        List<PipelineDiagnostic> ignored = [];

        List<string> next = ReadList(defaults, "next", nextAnchors, ignored, source);
        List<string> onError = ReadList(defaults, "on_error", onErrorAnchors, ignored, source);

        return new PipelineDefaults
        {
            DeclaresNext = defaults.TryGetProperty("next", out _),
            DeclaresOnError = defaults.TryGetProperty("on_error", out _),
            Next = next,
            OnError = onError,
            NextAnchors = nextAnchors,
            OnErrorAnchors = onErrorAnchors,
            Source = source,
        };
    }

    private static List<string> ReadList(
        JsonElement defaults, string field, List<string> anchors, List<PipelineDiagnostic> diagnostics, string? source)
    {
        if (!defaults.TryGetProperty(field, out JsonElement value))
        {
            return [];
        }

        var refs = new List<string>();
        ParseRefs(value, refs, anchors, field, source, diagnostics);
        return refs;
    }

    /// <summary>
    /// 与 <see cref="PipelineInventory"/> 共用同一套前缀语义（<c>[JumpBack]</c> / <c>[Anchor]</c> / NodeAttr）。
    /// </summary>
    internal static void ParseRefs(
        JsonElement value, List<string> refs, List<string> anchors, string field,
        string? source, List<PipelineDiagnostic> diagnostics)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
            {
                string raw = value.GetString() ?? string.Empty;
                if (raw.StartsWith("[JumpBack]", StringComparison.Ordinal))
                {
                    raw = raw["[JumpBack]".Length..];
                }

                if (raw.StartsWith("[Anchor]", StringComparison.Ordinal))
                {
                    string anchor = raw["[Anchor]".Length..];
                    if (anchor.Length > 0)
                    {
                        anchors.Add(anchor);
                    }

                    return;
                }

                if (raw.Length > 0)
                {
                    refs.Add(raw);
                }

                break;
            }

            case JsonValueKind.Object:
                if (value.TryGetProperty("name", out JsonElement name)
                    && name.ValueKind == JsonValueKind.String)
                {
                    ParseRefs(name, refs, anchors, field, source, diagnostics);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement element in value.EnumerateArray())
                {
                    ParseRefs(element, refs, anchors, field, source, diagnostics);
                }

                break;
        }
    }

    /// <summary>后加载 bundle 的同名字段覆盖先加载的（与框架的多 bundle 合并一致）。</summary>
    public PipelineDefaults Then(PipelineDefaults later)
    {
        if (later.IsEmpty)
        {
            return this;
        }

        return new PipelineDefaults
        {
            DeclaresNext = later.DeclaresNext || DeclaresNext,
            DeclaresOnError = later.DeclaresOnError || DeclaresOnError,
            Next = later.DeclaresNext ? later.Next : Next,
            OnError = later.DeclaresOnError ? later.OnError : OnError,
            NextAnchors = later.DeclaresNext ? later.NextAnchors : NextAnchors,
            OnErrorAnchors = later.DeclaresOnError ? later.OnErrorAnchors : OnErrorAnchors,
            Source = later.DeclaresNext || later.DeclaresOnError ? later.Source : Source,
        };
    }
}
