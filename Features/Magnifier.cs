using System.Threading.Tasks;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>Windows 自带放大镜的开关（保留自旧的 CapsLock+Tab 手势）。</summary>
internal static class Magnifier
{
    /// <summary>
    /// 已开则关闭；未开则发 Win+= 打开，1s 后最小化放大镜控制窗口。
    /// 在后台线程执行（含 1s Sleep，不阻塞调用方）。
    /// </summary>
    public static void Toggle() => Task.Run(() =>
    {
        try
        {
            var hwnd = Win32.FindWindow("MagUIClass", null);
            if (hwnd != IntPtr.Zero)
            {
                Win32.SendMessage(hwnd, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
                TrayService.Notify("放大镜已关闭");
            }
            else
            {
                // Win+=（VK_LWIN + VK_OEM_PLUS 和弦）
                InputHelper.Combo((ushort)Win32.VkLwin, (ushort)Win32.VkOemPlus);
                TrayService.Notify("放大镜已开启");
                // 1s 后最小化放大镜控制窗口
                Thread.Sleep(1000);
                var mag = Win32.FindWindow("MagUIClass", null);
                if (mag != IntPtr.Zero)
                {
                    Win32.ShowWindowAsync(mag, Win32.SwMinimize);
                    TrayService.Notify("放大镜控制窗口已最小化");
                }
            }
        }
        catch { /* 放大镜操作失败静默 */ }
    });
}
