using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 屏幕底部滚轮调音量（对应原版 lib/VolumeControl.ahk）。
/// 鼠标位于屏幕底部 5px 内滚动滚轮 → 系统音量 ±1 档（每格）。跟随全局启用开关。
/// </summary>
/// <remarks>
/// 实现：通过 <see cref="InputHelper.Tap"/> 发送 VK_VOLUME_UP/DOWN 媒体键。
/// 这两个键（0xAD/0xAE/0xAF）是带 E0 扩展前缀的媒体键——<see cref="InputHelper"/> 的
/// <see cref="InputHelper.IsExtendedKey"/> 已包含它们，确保 SendInput 发送时带
/// KEYEVENTF_EXTENDEDKEY。浏览器据此正确识别为 VolumeUp/VolumeDown 媒体键
/// （code="VolumeUp"/"VolumeDown"），而非将其扫描码（0x30/0x2E/0x20）误判为
/// 普通字符键（KeyB/KeyC/KeyD），从而避免触发网页快捷键（如抖音的收藏/评论）。
/// 每步调节幅度约 2%（系统默认音量步长），与原版 AHK 行为一致。
/// </remarks>
internal static class Volume
{
    private const int BottomMargin = 5; // 底部 5px 触发
    private const int Step = 1;         // 每格滚轮敲击次数；VK_VOLUME_UP/Down 每次约2%

    /// <summary>处理一次滚轮事件。返回 true 表示已消费（吞掉滚轮）。</summary>
    public static bool OnWheel(int x, int y, int delta)
    {
        if (!AppState.IsToolEnabled) return false;
        int screenH = ScreenHeight();
        if (screenH - y > BottomMargin) return false; // 不在底部 5px 内，放行
        int steps = Math.Sign(delta);
        for (int i = 0; i < Step; i++)
        {
            InputHelper.Tap((ushort)(steps > 0 ? Win32.VkVolumeUp : Win32.VkVolumeDown));
        }
        return true;
    }

    private static int ScreenHeight()
    {
        var hdc = GetDC(IntPtr.Zero);
        int h = GetDeviceCaps(hdc, 10 /*VERTRES*/);
        ReleaseDC(IntPtr.Zero, hdc);
        return h;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
}
