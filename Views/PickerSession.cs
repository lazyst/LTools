using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using LTools.Hooks;

namespace LTools.Views;

/// <summary>
/// 全屏点选器（窗口 / 控件）的会话：收起 LTools 全部窗口 → 鼠标钩子让路 → 等桌面重绘 →
/// 取光标所在显示器与它的缩放。
/// </summary>
/// <remarks>
/// 这三步每一条都是踩过的坑：自己的窗口会挡住目标并混进候选；裸右键长按手势会吞掉遮罩的
/// down / up（表现为「连点两次才退出」）；最小化动画没结束就截图会拍进半透明的窗口。
/// 新增全屏点选入口一律 <c>using var session = PickerSession.Begin(owner)</c>，别再各写一份。
/// </remarks>
internal sealed class PickerSession : IDisposable
{
    private readonly Action _restore;
    private bool _disposed;

    private PickerSession(Rectangle monitor, double scaleX, double scaleY, Action restore)
    {
        Monitor = monitor;
        ScaleX = scaleX > 0 ? scaleX : 1;
        ScaleY = scaleY > 0 ? scaleY : 1;
        _restore = restore;
    }

    /// <summary>光标所在显示器（屏幕物理像素）。</summary>
    public Rectangle Monitor { get; }

    public double ScaleX { get; }

    public double ScaleY { get; }

    public static PickerSession Begin(Window? owner)
    {
        // 遮罩是 Topmost 全屏、不设 Owner：owner 被最小化后，owned window 会被系统一起隐藏
        //（照 ScreenPicker.TryPickRegion 的已验证做法）。
        Action restore = TransientMinimize.MinimizeAll(owner);
        MouseHook.SetWindowPickerActive(true);
        try
        {
            Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完
            var monitor = ScreenPicker.MonitorUnderCursor();
            var (sx, sy) = ScreenPickerWindow.MonitorScale(monitor);
            return new PickerSession(monitor, sx, sy, () =>
            {
                MouseHook.SetWindowPickerActive(false);
                restore();
            });
        }
        catch
        {
            MouseHook.SetWindowPickerActive(false);
            restore();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _restore();
    }
}
