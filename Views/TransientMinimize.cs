using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Views;

/// <summary>
/// 临时把 LTools 的所有可见窗口最小化（截图 / 屏幕取点 / 试运行前用），返回恢复函数。
/// </summary>
/// <remarks>
/// <para>
/// **必须用最小化，不能用 <see cref="Window.Hide"/>**：WPF 的 <c>ShowDialog</c> 模态循环在窗口被 Hide 时
/// 会立刻结束（<c>ShowDialog()</c> 直接返回 false、模态标记清空），编辑器随后用 Show 重新显示就成了
/// 「僵尸窗口」——点任何会设置 <c>DialogResult</c> 的按钮都会抛 InvalidOperationException，流程也会提前返回。
/// 最小化不影响模态循环，窗口同样不会出现在截图里。
/// </para>
/// <para>
/// 应用是 <c>ShutdownMode=OnExplicitShutdown</c>（托盘常驻），收起全部窗口不会退出进程。
/// </para>
/// </remarks>
internal static class TransientMinimize
{
    /// <summary>窗口最小化动画大致时长；调用方等这么久再截图。</summary>
    public const int SettleMs = 320;

    /// <summary>最小化全部可见窗口；调用方在 finally 中执行返回的委托恢复。</summary>
    public static Action MinimizeAll(Window? owner)
    {
        var app = Application.Current;
        if (app == null) return static () => { };

        var saved = new List<(Window Window, WindowState State)>();
        foreach (Window window in app.Windows)
        {
            if (!window.IsVisible || window.WindowState == WindowState.Minimized) continue;
            try
            {
                saved.Add((window, window.WindowState));
                window.WindowState = WindowState.Minimized;
            }
            catch { /* 关闭中的窗口忽略 */ }
        }

        return () =>
        {
            foreach (var (window, state) in saved)
            {
                try { if (window.IsVisible) window.WindowState = state; }
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
