using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MaaYys.Engine.Native;

namespace MaaYys.Engine;

/// <summary>桌面窗口信息（MaaToolkitDesktopWindow 的托管投影）。</summary>
/// <param name="Handle">Win32 窗口句柄（HWND）。</param>
/// <param name="ClassName">窗口类名（Win32）。</param>
/// <param name="WindowName">窗口标题。</param>
public sealed record DesktopWindowInfo(IntPtr Handle, string ClassName, string WindowName)
{
    public override string ToString() => $"hWnd=0x{Handle.ToInt64():X} class=[{ClassName}] title=[{WindowName}]";
}

/// <summary>
/// 桌面窗口枚举（MaaToolkit.dll 的 MaaToolkitDesktopWindow 系列 API）。
/// 对齐 MaaPiCli interactor.cpp 的行为：class_regex / window_regex 均为子串正则搜索
/// （boost::regex_search ↔ Regex.IsMatch），任一窗口同时满足两者即命中。
/// </summary>
public static class MaaDesktopWindows
{
    /// <summary>枚举当前全部桌面窗口。</summary>
    public static List<DesktopWindowInfo> FindAll()
    {
        MaaToolkitDesktopWindowListHandle list = new(MaaFrameworkApi.MaaToolkitDesktopWindowListCreate());
        if (list.IsInvalid)
        {
            throw new InvalidOperationException("MaaToolkitDesktopWindowListCreate 返回空句柄");
        }

        if (MaaFrameworkApi.MaaToolkitDesktopWindowFindAll(list) == MaaDef.False)
        {
            throw new InvalidOperationException("MaaToolkitDesktopWindowFindAll 失败");
        }

        List<DesktopWindowInfo> result = [];
        ulong count = list.Count;
        for (ulong i = 0; i < count; i++)
        {
            IntPtr window = MaaFrameworkApi.MaaToolkitDesktopWindowListAt(list, i);
            if (window == IntPtr.Zero)
            {
                continue;
            }

            IntPtr hWnd = MaaFrameworkApi.MaaToolkitDesktopWindowGetHandle(window);
            string className = MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitDesktopWindowGetClassName(window)) ?? string.Empty;
            string windowName = MaaFramework.FromUtf8Native(MaaFrameworkApi.MaaToolkitDesktopWindowGetWindowName(window)) ?? string.Empty;
            result.Add(new DesktopWindowInfo(hWnd, className, windowName));
        }

        return result;
    }

    /// <summary>
    /// 按类名/标题正则枚举窗口（子串匹配语义；null 或空表示不限制）。
    /// </summary>
    public static List<DesktopWindowInfo> FindAll(string? classRegex, string? windowRegex)
    {
        Regex? classPattern = Compile(classRegex);
        Regex? windowPattern = Compile(windowRegex);

        List<DesktopWindowInfo> matched = [];
        foreach (DesktopWindowInfo window in FindAll())
        {
            if (classPattern is not null && !classPattern.IsMatch(window.ClassName))
            {
                continue;
            }

            if (windowPattern is not null && !windowPattern.IsMatch(window.WindowName))
            {
                continue;
            }

            matched.Add(window);
        }

        return matched;
    }

    /// <summary>
    /// 枚举指定窗口的子窗口（Win32 EnumChildWindows）。
    /// 诊断用途：部分游戏（Unity/CEF 等）的输入实际由子窗口处理，
    /// 若向顶层窗口 SendMessage 无效，可能需要改投子窗口。
    /// </summary>
    public static List<DesktopWindowInfo> FindChildren(IntPtr parent)
    {
        List<DesktopWindowInfo> children = [];
        if (parent == IntPtr.Zero)
        {
            return children;
        }

        bool EnumProc(IntPtr hWnd, IntPtr lParam)
        {
            children.Add(new DesktopWindowInfo(hWnd, GetClassName(hWnd), GetWindowText(hWnd)));
            return true;
        }

        EnumChildWindows(parent, EnumProc, IntPtr.Zero);
        return children;
    }

    private static string GetClassName(IntPtr hWnd)
    {
        StringBuilder sb = new(256);
        _ = GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        StringBuilder sb = new(256);
        _ = GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>编译正则；null/空/空白返回 null（不限制）。语法不合法时抛出并附带上下文。</summary>
    private static Regex? Compile(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"窗口正则语法不合法: '{pattern}'（{ex.Message}）", ex);
        }
    }
}
