using System.IO;
using System.Runtime.InteropServices;

namespace MaaYys.Engine.Devices;

/// <summary>
/// 游戏窗口「幽灵化」修复：窗口位置、大小、<c>IsIconic</c>、<c>IsWindowVisible</c> 全都正常，
/// 截图也抓得到画面，但桌面上完全透明 —— 只能看到它后面的桌面。
///
/// 成因（P2.5 实测，阴阳师桌面版 + MaaFramework Win32 控制器）：
/// <list type="number">
///   <item>游戏被最小化时，窗口被设上 <c>WS_EX_LAYERED</c> + <c>WS_EX_TRANSPARENT</c> 且 alpha=0，
///         并挪到屏幕外 (-40000,-40000)；</item>
///   <item>MaaFramework 为了截图把窗口 <c>SW_RESTORE</c> 回原位，几何与可见性都恢复正确，
///         <b>但没有还原分层属性</b>，alpha 停在 0；</item>
///   <item>结果：窗口在正常渲染、WGC 抓得到（预览正常），DWM 合成时 alpha=0 完全透明。</item>
/// </list>
///
/// 处置原则：**最小化状态一律不碰**（恢复窗口是 MaaFramework 的职责，擅自 SW_RESTORE 会把游戏
/// 从后台弹出来打扰用户）；只修「已非最小化、却仍然 alpha=0 / 点击穿透」这种确定异常的状态。
/// 操作幂等：正常窗口只多花两次 GetWindowLongPtr/GetLayeredWindowAttributes。
/// </summary>
public static class GameWindowVisibility
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;
    private const int SwRestore = 9;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint RdwInvalidate = 0x0001;
    private const uint RdwFrame = 0x0400;
    private const uint RdwAllChildren = 0x0080;
    private const uint RdwUpdateNow = 0x0100;

    /// <summary>
    /// 检测并修复幽灵化窗口，返回是否做了改动；<paramref name="detail"/> 说明改了什么。
    /// 最小化的窗口直接跳过（返回 false）。
    /// </summary>
    public static bool EnsureVisible(IntPtr hwnd, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        // 最小化：不动。等控制器把它恢复后，巡检会接手修 alpha。
        if (IsIconic(hwnd))
        {
            return false;
        }

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        bool opaqueFixed = false;
        if ((exStyle & WsExLayered) != 0
            && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _)
            && alpha == 0)
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            opaqueFixed = true;
        }

        bool clickThroughFixed = false;
        if ((exStyle & WsExTransparent) != 0)
        {
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle & ~WsExTransparent));
            clickThroughFixed = true;
        }

        if (!opaqueFixed && !clickThroughFixed)
        {
            return false;
        }

        // 改完样式必须强制重绘一帧，否则 DWM 可能继续沿用旧的透明表面
        SetWindowPos(
            hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        RedrawWindow(
            hwnd, IntPtr.Zero, IntPtr.Zero,
            RdwInvalidate | RdwAllChildren | RdwUpdateNow | RdwFrame);

        detail = (opaqueFixed, clickThroughFixed) switch
        {
            (true, true) => "alpha 0→255 + 移除点击穿透",
            (true, false) => "alpha 0→255",
            _ => "移除点击穿透",
        };
        Log($"hwnd=0x{hwnd.ToInt64():X} 修复幽灵窗口：{detail}");
        return true;
    }

    /// <summary>修复记录落盘（GUI 进程无控制台，出问题时这是唯一的追溯线索）。</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "debug");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "window-fix.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // 日志失败不影响修复
        }
    }

    /// <summary>只探测不修改：窗口是否处于「已非最小化但不可见」的幽灵状态。</summary>
    public static bool IsGhost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((exStyle & WsExTransparent) != 0)
        {
            return true;
        }

        return (exStyle & WsExLayered) != 0
               && GetLayeredWindowAttributes(hwnd, out _, out byte alpha, out _)
               && alpha == 0;
    }

    /// <summary>把窗口从最小化恢复并确保不透明（供「显示游戏窗口」这类显式操作使用）。</summary>
    public static bool ShowAndFix(IntPtr hwnd, out string detail)
    {
        detail = string.Empty;
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
        {
            return false;
        }

        bool restored = false;
        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
            restored = true;
        }

        bool fixedStyle = EnsureVisible(hwnd, out string styleDetail);
        detail = string.Join(
            " + ",
            new[] { restored ? "从最小化恢复" : null, fixedStyle ? styleDetail : null }
                .Where(s => !string.IsNullOrEmpty(s)));
        return restored || fixedStyle;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool GetLayeredWindowAttributes(
        IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RedrawWindow(
        IntPtr hwnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
}
