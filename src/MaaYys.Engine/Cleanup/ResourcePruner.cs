using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaaYys.Engine.Cleanup;

/// <summary>
/// 资源整理结果。<see cref="Removed"/> 里的路径都是相对资源根的，直接写进日志/界面。
/// </summary>
public sealed record PruneOutcome(
    bool Success,
    int RemovedFiles,
    int RewrittenFiles,
    int RemovedNodes,
    long FreedBytes,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Errors)
{
    public bool Changed => RemovedFiles + RewrittenFiles > 0;

    public string Summary => !Success
        ? $"整理失败：{string.Join("；", Errors.Take(3))}"
        : Changed
            ? $"删除 {RemovedFiles} 个文件、重写 {RewrittenFiles} 个文件（清掉 {RemovedNodes} 个不可达节点），释放 {ResourceAuditResult.FormatBytes(FreedBytes)}"
            : "没有需要整理的过期资源";
}

/// <summary>整理选项。</summary>
public sealed record PruneOptions
{
    /// <summary>是否连「未引用的模板图/模型」一起删（默认删：资源更新后它们就是旧版本的残留）。</summary>
    public bool IncludeUnusedAssets { get; init; } = true;

    /// <summary>只算不删（dry-run），用于报告与测试。</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// 资源整理：把审计判成冗余的东西<b>直接删掉</b>，不进回收区。
/// <para>
/// 为什么不做回收区：这里的删除时机是「资源版本更新」，删掉的东西是<b>旧版本资源包留下的</b>，
/// 恢复它们没有意义（新版资源包自带自己需要的一切）。回收区只会让用户目录里长期躺着一份
/// 同样大小的副本，等于没清理。
/// </para>
/// <para>
/// 安全底线（哪怕上层判定出错也不会误伤）：
/// <list type="number">
/// <item><see cref="CleanupRisk.Protected"/> 的项永不处理；</item>
/// <item><c>interface.json</c> / <c>default_pipeline.json</c> / <c>debug.json</c> 永不删除；</item>
/// <item>只有落在资源链 bundle 目录<b>内部</b>的路径才会被动（拦住 <c>../../</c> 这类越界）。</item>
/// </list>
/// </para>
/// </summary>
public static class ResourcePruner
{
    private static readonly string[] ForbiddenFiles = ["interface.json", "default_pipeline.json", "debug.json"];

    public static PruneOutcome Prune(ResourceAuditResult audit, PruneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(audit);

        PruneOptions settings = options ?? new PruneOptions();

        List<CleanupItem> selected =
        [
            .. audit.Items.Where(i => i.CanClean
                                      && (i.Risk == CleanupRisk.Safe || settings.IncludeUnusedAssets))
        ];

        if (selected.Count == 0)
        {
            return new PruneOutcome(true, 0, 0, 0, 0, [], []);
        }

        List<string> removed = [];
        List<string> errors = [];
        int removedFiles = 0;
        int rewritten = 0;
        int removedNodes = 0;
        long freed = 0;

        foreach (CleanupItem item in selected)
        {
            try
            {
                switch (item.Category)
                {
                    case CleanupCategory.ConfigBackup:
                    {
                        string path = Path.Combine(audit.ConfigDirectory, item.RelativePath);
                        if (!File.Exists(path))
                        {
                            errors.Add($"{item.RelativePath}：文件已不存在");
                            break;
                        }

                        if (TryDelete(path, settings.DryRun, out long bytes, out string? error))
                        {
                            removedFiles++;
                            freed += bytes;
                            removed.Add(item.RelativePath);
                        }
                        else if (error is not null)
                        {
                            errors.Add(error);
                        }

                        break;
                    }

                    case CleanupCategory.DeadFile:
                    case CleanupCategory.UnusedTemplate:
                    case CleanupCategory.UnusedModel:
                    {
                        bool found = false;
                        foreach (string path in ResolveInBundles(audit, item.RelativePath))
                        {
                            found = true;
                            if (IsForbidden(path))
                            {
                                continue;
                            }

                            if (TryDelete(path, settings.DryRun, out long bytes, out string? error))
                            {
                                removedFiles++;
                                freed += bytes;
                                removed.Add(item.RelativePath);
                            }
                            else if (error is not null)
                            {
                                errors.Add(error);
                            }
                        }

                        if (!found)
                        {
                            errors.Add($"{item.RelativePath}：资源包里找不到这个文件");
                        }

                        break;
                    }

                    case CleanupCategory.DeadNodes:
                    {
                        foreach (string path in ResolveInBundles(audit, item.RelativePath))
                        {
                            if (!File.Exists(path) || IsForbidden(path))
                            {
                                continue;
                            }

                            (bool ok, int count, long delta, string? error) =
                                RemoveNodes(path, item.Nodes, settings.DryRun);

                            if (!ok)
                            {
                                errors.Add($"{item.RelativePath}：{error}");
                                continue;
                            }

                            if (count > 0)
                            {
                                rewritten++;
                                removedNodes += count;
                                freed += delta;
                                removed.Add($"{item.RelativePath}（{count} 个节点）");
                            }
                        }

                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                errors.Add($"{item.RelativePath}：{ex.Message}");
            }
        }

        return new PruneOutcome(true, removedFiles, rewritten, removedNodes, freed, removed, errors);
    }

    // ---- 内部实现 ----

    /// <summary>把相对路径解析成资源链上的真实文件（同一路径可能存在于多个 bundle，都要处理）。</summary>
    private static IEnumerable<string> ResolveInBundles(ResourceAuditResult audit, string relativePath)
    {
        foreach (string bundle in audit.BundlePaths)
        {
            string bundleRoot = Path.GetFullPath(bundle);
            string candidate = Path.GetFullPath(Path.Combine(bundleRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            // 越界拦截：解析后必须仍在 bundle 目录内
            if (!candidate.StartsWith(bundleRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, bundleRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static bool TryDelete(string path, bool dryRun, out long bytes, out string? error)
    {
        bytes = 0;
        error = null;

        try
        {
            bytes = new FileInfo(path).Length;
            if (dryRun)
            {
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"{Path.GetFileName(path)}：{ex.Message}";
            return false;
        }
    }

    private static (bool Ok, int Removed, long Delta, string? Error) RemoveNodes(
        string path, IReadOnlyList<string> nodes, bool dryRun)
    {
        string original = File.ReadAllText(path);
        JsonObject? root = JsonNode.Parse(original, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject;

        if (root is null)
        {
            return (false, 0, 0, "文件顶层不是对象");
        }

        List<string> removed = [];
        foreach (string node in nodes)
        {
            if (root.Remove(node))
            {
                removed.Add(node);
            }
        }

        if (removed.Count == 0)
        {
            return (true, 0, 0, null);
        }

        if (dryRun)
        {
            return (true, removed.Count, 0, null);
        }

        long before = new UTF8Encoding(false).GetByteCount(original);
        if (root.Count == 0)
        {
            File.Delete(path);
            return (true, removed.Count, before, null);
        }

        string rewritten = Serialize(root, original);
        File.WriteAllText(path, rewritten, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return (true, removed.Count, Math.Max(0, before - new UTF8Encoding(false).GetByteCount(rewritten)), null);
    }

    /// <summary>按原文件的缩进与行尾写回，把 diff 噪音压到最小。</summary>
    private static string Serialize(JsonObject root, string original)
    {
        bool crlf = original.Contains("\r\n", StringComparison.Ordinal);
        int indent = DetectIndent(original);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = indent,
            NewLine = crlf ? "\r\n" : "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        string text = root.ToJsonString(options);
        return crlf ? text.Replace("\r\n", "\n").Replace("\n", "\r\n") : text;
    }

    private static int DetectIndent(string text)
    {
        int newline = text.IndexOf('\n');
        if (newline < 0)
        {
            return 2;
        }

        int spaces = 0;
        for (int i = newline + 1; i < text.Length && text[i] == ' '; i++)
        {
            spaces++;
        }

        return spaces is >= 1 and <= 8 ? spaces : 2;
    }

    private static bool IsForbidden(string path)
        => ForbiddenFiles.Any(f => string.Equals(f, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
}
