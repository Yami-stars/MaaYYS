namespace MaaYys.Engine;

/// <summary>资源链加载结果。<paramref name="LoadedBundles"/> 为实际加载成功的包（按顺序）。</summary>
public sealed record ResourceLoadResult(
    bool Success,
    string? Error,
    IReadOnlyList<string> LoadedBundles)
{
    public static ResourceLoadResult Ok(IReadOnlyList<string> loaded) => new(true, null, loaded);

    public static ResourceLoadResult Fail(string error, IReadOnlyList<string>? loaded = null)
        => new(false, error, loaded ?? []);
}
