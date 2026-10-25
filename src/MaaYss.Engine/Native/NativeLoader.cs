using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MaaYss.Engine.Native;

/// <summary>
/// 将 DllImport("MaaFramework") 定向解析到项目 third_party/maafw/ 下的绝对路径。
/// 解析顺序：
///   1. 环境变量 MAAYS_MAFW_PATH（指向含 MaaFramework.dll 的目录）
///   2. 自 AppContext.BaseDirectory 向上最多 8 级查找 third_party/maafw/MaaFramework.dll
///   3. 向上查找任意目录下的 MaaFramework.dll
///   4. 均未命中 → 交还默认解析（返回 IntPtr.Zero）
/// MaaFramework.dll 的同目录依赖（MaaUtils.dll 等）由 Windows 加载器按其所在目录自动解析。
/// </summary>
internal static class NativeLoader
{
    private const string DllName = "MaaFramework";
    private const string DllFileName = "MaaFramework.dll";

    /// <summary>模块加载时自动注册解析器（无需显式调用）。</summary>
    // CA2255 建议 ModuleInitializer 仅用于应用程序代码；此处为类库的 DllImport 解析注册，
    // 属既定用法，显式抑制。
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);
#pragma warning restore CA2255

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        string? path = Locate();
        return path is null ? IntPtr.Zero : NativeLibrary.Load(path, assembly, searchPath);
    }

    /// <summary>定位 MaaFramework.dll 的绝对路径；未命中返回 null。</summary>
    public static string? Locate()
    {
        string? env = Environment.GetEnvironmentVariable("MAAYS_MAFW_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string candidate = Path.Combine(env, DllFileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string inThirdParty = Path.Combine(dir.FullName, "third_party", "maafw", DllFileName);
            if (File.Exists(inThirdParty))
            {
                return inThirdParty;
            }

            string direct = Path.Combine(dir.FullName, DllFileName);
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        return null;
    }
}
