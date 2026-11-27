using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using LTools.Core;
using LTools.Features;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 退出全屏点选器 / 遮罩后做一次「谁还压着任务栏」的自检，只在真有可疑窗口时记一条 warning。
/// </summary>
/// <remarks>
/// 用户报过：右键取消点选后**别的窗口都能点、任务栏点什么都不动**，再点一次右键才恢复。
/// 这类现象的典型成因是「有个置顶窗口压在任务栏那条上」——遮罩自己是全屏 topmost、
/// 被 <see cref="TransientMinimize"/> 最小化又恢复的置顶小窗（贴图 / 悬浮 / 工具条 / Toast）
/// 也会在恢复时重新插到 topmost 带最前面。正常退出时这些窗口都不该和任务栏矩形相交。
/// 所以这里在退出那一刻把「本进程还可见的窗口」全查一遍：相交的、还是置顶的，直接点名，
/// 下次再出问题就不用猜了。<b>正常情况只写一条 info，没有噪音。</b>
/// </remarks>
internal static class PickerExitAudit
{
    /// <summary>自检一次；有置顶窗口压着任务栏就记 warning。</summary>
    public static void Report()
    {
        try
        {
            IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
            var trayRect = new System.Drawing.Rectangle();
            bool hasTray = tray != IntPtr.Zero
                           && Win32.GetWindowRect(tray, out Win32.Rect r)
                           && (trayRect = System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).Width > 0;

            var suspicious = new List<string>();
            int visible = 0;

            foreach (Window window in Application.Current.Windows)
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd)) continue;

                var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
                if (w <= 0 || h <= 0) continue;

                long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
                bool topmost = (ex & Win32.WsExTopmost) != 0;
                visible++;

                if (!hasTray || !topmost) continue;
                var mine = new System.Drawing.Rectangle(x, y, w, h);
                var hit = System.Drawing.Rectangle.Intersect(mine, trayRect);
                if (hit.Width > 0 && hit.Height > 0)
                {
                    string cls = ClassOf(hwnd);
                    suspicious.Add($"{cls}/{window.Title} @ {x},{y} {w}x{h}（压住任务栏 {hit.Width}x{hit.Height}）");
                }
            }

            if (suspicious.Count > 0)
            {
                AppLog.Warn("Picker", "退出点选器后仍有置顶窗口压着任务栏（左键点任务栏没反应多半就是它）："
                                      + string.Join("；", suspicious));
            }
            else
            {
                AppLog.Info("Picker", $"退出点选器自检：没有置顶窗口压着任务栏（本进程可见窗口 {visible} 个）");
            }

            if (!hasTray) return;

            // Explorer 自己的状态：它认为前台是全屏应用时，任务栏**看得见、悬停有高亮、点击不响应**。
            // 这一行是判断「任务栏自己拒收点击」还是「有东西盖着」的决定性证据。
            int hr = Win32.SHQueryUserNotificationState(out int state);
            IntPtr fg = Win32.GetForegroundWindow();
            Win32.GetWindowThreadProcessId(fg, out uint fgPid);
            AppLog.Info("Picker", $"退出点选器：通知状态 {(hr == 0 ? Win32.DescribeUserNotificationState(state) : "查询失败 " + hr)}"
                                  + $"；前台 {ClassOf(fg)}/pid {fgPid}{(fgPid == Environment.ProcessId ? "（本进程）" : "")}");

            // 最直接的答案：任务栏上三个点，点下去会落到哪个窗口 / 哪个进程。
            // 落点还是 Shell_TrayWnd 就说明任务栏本身收得到点击（问题不在遮挡），
            // 落到我们自己的窗口就说明是它盖住了。
            int cy = trayRect.Top + trayRect.Height / 2;
            var hits = new List<string>();
            foreach (int cx in new[] { trayRect.Left + 24, trayRect.Left + trayRect.Width / 2, trayRect.Right - 24 })
            {
                IntPtr at = Win32.TopLevelWindowAtPoint(cx, cy);
                Win32.GetWindowThreadProcessId(at, out uint pid);
                hits.Add($"{cx},{cy} → {ClassOf(at)}/pid {pid}{(pid == Environment.ProcessId ? "（本进程）" : "")}");
            }
            AppLog.Info("Picker", $"退出点选器：任务栏 {trayRect.Left},{trayRect.Top} {trayRect.Width}x{trayRect.Height}，落点 " + string.Join("；", hits));
        }
        catch (Exception ex)
        {
            // 自检永远不能影响退出流程
            AppLog.Warn("Picker", "退出点选器自检失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 退出后再看一眼（250ms 后）：Explorer 的「全屏应用」判定是异步更新的，
    /// 退出那一刻查到的状态未必就是用户随后点任务栏时的状态。
    /// </summary>
    public static void CheckLater(int delayMs = 250)
    {
        try
        {
            var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(delayMs),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    if (Win32.SHQueryUserNotificationState(out int state) != 0) return;
                    IntPtr fg = Win32.GetForegroundWindow();
                    Win32.GetWindowThreadProcessId(fg, out uint pid);
                    AppLog.Info("Picker", $"退出点选器 {delayMs}ms 后：通知状态 {Win32.DescribeUserNotificationState(state)}"
                                          + $"；前台 {ClassOf(fg)}/pid {pid}{(pid == Environment.ProcessId ? "（本进程）" : "")}");
                    if (Win32.IsFullScreenState(state)) RestoreTaskbarIfSuppressed();
                }
                catch { /* 自检不影响任何流程 */ }
            };
            timer.Start();
        }
        catch { /* 没有 Dispatcher（无头冒烟）时忽略 */ }
    }

    /// <summary>
    /// 遮罩关掉后 Explorer 还停在「全屏应用 / 演示」状态时，把任务栏重新按回「可见 + 置顶」，
    /// 逼它重新评估一次（不抢焦点、不弹菜单）。返回是否真的做了一步。
    /// </summary>
    /// <remarks>
    /// 任务栏在这种状态下**看得见、悬停有高亮、点击却不响应**（用户报过，再点一次右键才恢复）。
    /// 正常退出时通知状态本来就是「acceptsNotifications」，这里什么都不做。
    /// </remarks>
    public static bool RestoreTaskbarIfSuppressed()
    {
        try
        {
            if (Win32.SHQueryUserNotificationState(out int state) != 0 || !Win32.IsFullScreenState(state))
                return false;

            IntPtr tray = Win32.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return false;

            Win32.ShowWindowAsync(tray, Win32.SwShownoactivate);
            Win32.SetWindowPos(tray, Win32.HwndTopmost, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate | Win32.SwpShowwindow);
            AppLog.Info("Picker", "退出点选器：通知状态还停在「" + Win32.DescribeUserNotificationState(state)
                                  + "」，已把任务栏按回置顶并重新显示");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Picker", "恢复任务栏失败：" + ex.Message);
            return false;
        }
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(128);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
