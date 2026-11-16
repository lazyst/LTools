using System;
using System.Threading;
using System.Windows;
using LTools.Core;
using LTools.Features;
using LTools.Hooks;
using LTools.Views;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 屏幕选字的唯一入口：收起 LTools 自己的窗口 → 冻结抓帧 → 打开取字覆盖层 → 用户复制走要的文字。
/// </summary>
/// <remarks>
/// 与 <see cref="CaptureSession"/> 同一套三道闸（都在 <c>finally</c> 里成对还原）：
/// ① <see cref="TransientMinimize"/>（不收起自己的窗口会被截进画面，也挡住框选）；
/// ② <see cref="MouseHook.SetWindowPickerActive"/>（否则「裸右键长按 = 面板」会吞掉右键）；
/// ③ <see cref="HotkeyRegistry.Suspended"/>（单键绑定会吞掉框选里的按键，<c>Esc</c> 就没了）。
/// 取景与覆盖层跟截图一样盖住**所有显示器的并集**，坐标仍是物理像素。
/// </remarks>
internal static class ScreenTextSession
{
    /// <summary>弹一次屏幕选字。用户取消（或没识别到要的文字）返回 false。</summary>
    public static bool TryPick(Window? owner)
    {
        var app = Application.Current;
        if (app == null) throw new InvalidOperationException("界面未初始化，无法屏幕选字");

        var restore = TransientMinimize.MinimizeAll(owner);
        bool wasSuspended = HotkeyRegistry.Suspended;
        MouseHook.SetWindowPickerActive(true);
        HotkeyRegistry.Suspended = true;

        try
        {
            Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完

            // 覆盖层盖住所有显示器（与截图覆盖层同一套取景），跨屏也能框
            var area = ScreenShot.Bounds();
            var shot = ScreenShot.Capture(area);
            var (sx, sy) = ScreenPickerWindow.MonitorScale(ScreenShot.ToRectangle(area));

            var window = new CaptureTextSelectWindow(shot, ScreenShot.ToRectangle(area), sx, sy);
            return window.ShowDialog() == true && window.Copied;
        }
        finally
        {
            HotkeyRegistry.Suspended = wasSuspended;
            MouseHook.SetWindowPickerActive(false);
            restore();
        }
    }

    /// <summary>在 UI 线程上弹一次（内置功能跑在后台线程，WPF 窗口只能在 UI 线程建）。</summary>
    public static bool PickOnUi(Window? owner)
    {
        var app = Application.Current;
        if (app == null) throw new InvalidOperationException("界面未初始化，无法屏幕选字");

        return app.Dispatcher.Invoke(() => TryPick(owner));
    }
}
