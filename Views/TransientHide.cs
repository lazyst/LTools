using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Views;

/// <summary>
/// 临时隐藏 LTools 的所有可见窗口（截图 / 屏幕取点 / 试运行前用），返回恢复函数。
/// </summary>
/// <remarks>
/// 直接 <see cref="Window.Hide"/> 而不是最小化：最小化有动画且会把窗口堆到任务栏，
/// 截出来的画面还会带一层毛玻璃；隐藏是同步的，等 DWM 重绘完再截图即可。
/// 应用是 <c>ShutdownMode=OnExplicitShutdown</c>（托盘常驻），隐藏全部窗口不会退出进程。
/// </remarks>
internal static class TransientHide
{
    /// <summary>隐藏全部可见窗口；调用方在 finally 中执行返回的委托恢复。</summary>
    public static Action HideAll(Window? owner)
    {
        var app = Application.Current;
        if (app == null) return static () => { };

        var hidden = new List<Window>();
        foreach (Window w in app.Windows)
        {
            if (!w.IsVisible) continue;
            try { w.Hide(); hidden.Add(w); }
            catch { /* 关闭中的窗口忽略 */ }
        }

        return () =>
        {
            foreach (var w in hidden)
            {
                try { if (!w.IsVisible) w.Show(); }
                catch { /* 已关闭 */ }
            }
            if (owner != null)
            {
                try { owner.Activate(); }
                catch { /* 前台锁 */ }
            }
        };
    }
}
