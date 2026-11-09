using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 屏幕边缘滚轮调音量（对应原版 lib/VolumeControl.ahk）。
/// 鼠标停在**光标所在显示器**的上 / 下边缘时滚轮 → 系统音量 ±1 档（每格）；
/// 算哪条边由设置「通用」页的「边缘滚轮音量」决定（底部 / 顶部 / 上下都算）。
/// </summary>
/// <remarks>
/// <para>
/// 实现：通过 <see cref="InputHelper.Tap"/> 发送 VK_VOLUME_UP/DOWN 媒体键。
/// 这两个键（0xAD/0xAE/0xAF）是带 E0 扩展前缀的媒体键——<see cref="InputHelper"/> 的
/// <see cref="InputHelper.IsExtendedKey"/> 已包含它们，确保 SendInput 发送时带
/// KEYEVENTF_EXTENDEDKEY。浏览器据此正确识别为 VolumeUp/VolumeDown 媒体键
/// （code="VolumeUp"/"VolumeDown"），而非将其扫描码（0x30/0x2E/0x20）误判为
/// 普通字符键（KeyB/KeyC/KeyD），从而避免触发网页快捷键（如抖音的收藏/评论）。
/// 每步调节幅度约 2%（系统默认音量步长），与原版 AHK 行为一致。
/// </para>
/// <para>
/// 边界判定必须按 <c>MonitorFromPoint</c> + <c>rcMonitor</c> 取**该显示器**的物理矩形：
/// 早先按主屏 <c>GetDeviceCaps(VERTRES)</c> 算，副屏 / 竖屏 / 缩放不是 100% 时全错。
/// </para>
/// </remarks>
internal static class Volume
{
    private const int EdgeMargin = 8;   // 边缘 8px 内触发（物理像素；高 DPI 下 5px 几乎点不中）
    private const int Step = 1;         // 每格滚轮敲击次数；VK_VOLUME_UP/Down 每次约2%

    /// <summary>处理一次滚轮事件。返回 true 表示已消费（吞掉滚轮）。</summary>
    public static bool OnWheel(int x, int y, int delta)
    {
        if (!AppState.EdgeWheelVolume) return false;
        if (!NearEdge(x, y, (WheelVolumeEdge)AppState.EdgeWheelVolumeEdge)) return false;

        int steps = Math.Sign(delta);
        for (int i = 0; i < Step; i++)
        {
            InputHelper.Tap((ushort)(steps > 0 ? Win32.VkVolumeUp : Win32.VkVolumeDown));
        }
        return true;
    }

    /// <summary>光标是否落在所处显示器的指定边缘内。</summary>
    private static bool NearEdge(int x, int y, WheelVolumeEdge edge)
    {
        var mon = Win32.MonitorFromPoint(new Win32.Point { X = x, Y = y }, Win32.MonitorDefaultToNearest);
        if (mon == IntPtr.Zero) return false;

        var mi = new Win32.MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MonitorInfo>() };
        if (!Win32.GetMonitorInfo(mon, ref mi)) return false;

        var rc = mi.rcMonitor;
        bool top = edge is WheelVolumeEdge.top or WheelVolumeEdge.both;
        bool bottom = edge is WheelVolumeEdge.bottom or WheelVolumeEdge.both;
        if (top && y - rc.Top <= EdgeMargin) return true;
        if (bottom && rc.Bottom - y <= EdgeMargin) return true;
        return false;
    }
}
