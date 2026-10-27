using System.Diagnostics;
using Microsoft.Win32;

namespace MaaYys.App.Services;

/// <summary>
/// 开机自启（P6.2 启动设置）：写 <c>HKCU\...\Run</c>，不需要管理员权限。
/// <para>
/// 只动自己那一个值名（<c>MaaYys</c>），不碰任何其他程序的启动项；
/// 关闭时删值而不是写空串——留空串会让系统每次启动都尝试执行一个空命令。
/// </para>
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MaaYys";

    /// <summary>静默启动参数（窗口最小化启动）。</summary>
    public const string SilentArgument = "--silent";

    /// <summary>当前注册表里的启动命令（未设置返回 null）。</summary>
    public static string? CurrentCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>注册表里是否已有自启项（不判断命令内容是否过期）。</summary>
    public static bool IsEnabled => !string.IsNullOrWhiteSpace(CurrentCommand());

    /// <summary>本程序应当写入的启动命令。</summary>
    public static string BuildCommand(bool silent)
    {
        string exe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? "MaaYys.exe";
        return silent ? $"\"{exe}\" {SilentArgument}" : $"\"{exe}\"";
    }

    /// <summary>
    /// 应用自启设置。返回 null 表示成功，否则是给用户看的失败原因。
    /// </summary>
    public static string? Apply(bool enabled, bool silent)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开注册表启动项");

            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(silent), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                                       or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>把注册表现状与本程序当前路径对齐（程序被移动过 / 从别的目录启动时自动修正）。</summary>
    public static string? Sync(bool silent)
    {
        string? current = CurrentCommand();
        if (string.IsNullOrWhiteSpace(current))
        {
            return null;
        }

        string expected = BuildCommand(silent);
        return string.Equals(current, expected, StringComparison.OrdinalIgnoreCase) ? null : Apply(true, silent);
    }
}
