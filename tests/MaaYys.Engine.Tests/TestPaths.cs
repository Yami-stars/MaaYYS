using System.IO;

namespace MaaYys.Engine.Tests;

/// <summary>仓库内资源定位（测试从 bin 目录向上查找 MaaYys.sln）。</summary>
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>P1.2 起复用的干跑资源包（DirectHit + DoNothing，无设备依赖）。</summary>
    public static string TestResourceDir => Path.Combine(RepoRoot, "test_resource");

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MaaYys.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未能从测试输出目录向上找到 MaaYys.sln（仓库根）");
    }
}
