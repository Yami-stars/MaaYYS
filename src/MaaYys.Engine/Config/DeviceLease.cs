namespace MaaYys.Engine.Config;

/// <summary>
/// 设备独占登记（多实例防冲突）。
/// 场景：两个实例同时连同一个游戏窗口时，截图不会互斥但**输入会互相干扰**
/// （A 实例的点击落在 B 实例正在操作的界面上），因此连接前必须登记占用。
/// 键为实际设备的稳定标识：Win32 用窗口句柄、ADB 用 host:port。
/// 进程内静态表，实例断开/删除/程序退出即释放。
/// </summary>
public static class DeviceLease
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Holders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试占用设备。
    /// </summary>
    /// <param name="deviceKey">设备稳定标识（<c>win32:0x1234</c> / <c>adb:127.0.0.1:16384</c>）。</param>
    /// <param name="ownerId">实例 Id。</param>
    /// <param name="holderId">占用失败时的持有者实例 Id。</param>
    /// <returns>已由本实例持有或首次占用成功返回 true。</returns>
    public static bool TryAcquire(string deviceKey, string ownerId, out string? holderId)
    {
        holderId = null;
        if (string.IsNullOrWhiteSpace(deviceKey) || string.IsNullOrWhiteSpace(ownerId))
        {
            return true;
        }

        lock (Gate)
        {
            if (Holders.TryGetValue(deviceKey, out string? existing))
            {
                if (string.Equals(existing, ownerId, StringComparison.Ordinal))
                {
                    return true;
                }

                holderId = existing;
                return false;
            }

            Holders[deviceKey] = ownerId;
            return true;
        }
    }

    /// <summary>释放本实例对某设备的占用（非持有者调用无副作用）。</summary>
    public static void Release(string deviceKey, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return;
        }

        lock (Gate)
        {
            if (Holders.TryGetValue(deviceKey, out string? existing) &&
                string.Equals(existing, ownerId, StringComparison.Ordinal))
            {
                Holders.Remove(deviceKey);
            }
        }
    }

    /// <summary>释放某实例持有的全部设备（删除实例 / 窗口关闭）。</summary>
    public static void ReleaseAll(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        lock (Gate)
        {
            foreach (string key in Holders.Where(p => string.Equals(p.Value, ownerId, StringComparison.Ordinal))
                         .Select(p => p.Key).ToList())
            {
                Holders.Remove(key);
            }
        }
    }

    /// <summary>查询占用者（无占用返回 null）。</summary>
    public static string? HolderOf(string deviceKey)
    {
        lock (Gate)
        {
            return Holders.TryGetValue(deviceKey, out string? holder) ? holder : null;
        }
    }

    /// <summary>当前占用快照（诊断/UI 提示用）。</summary>
    public static IReadOnlyDictionary<string, string> Snapshot()
    {
        lock (Gate)
        {
            return new Dictionary<string, string>(Holders, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>由设备描述生成占用键。</summary>
    public static string KeyFor(Devices.DeviceDescriptor device) => device.LeaseKey;
}
