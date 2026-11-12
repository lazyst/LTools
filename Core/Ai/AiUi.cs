using System.Windows;
using LTools.Views;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 回调线程 ↔ WPF UI 线程的唯一通道。
/// </summary>
/// <remarks>
/// <para>
/// <c>ActionRegistry</c> / <c>ConfigStore</c> / <c>HotkeyRegistry</c> 与 WPF 对象都不是线程安全的，
/// HTTP / MCP 回调线程上直接碰会炸（字典并发写、跨线程访问 DispatcherObject）。
/// 所有这类访问都走这里投递到 UI 线程再取结果；**不要在 UI 线程上等 HTTP 结果**（会死锁）。
/// </para>
/// </remarks>
internal static class AiUi
{
    /// <summary>在 UI 线程上执行并返回结果。</summary>
    public static T Run<T>(Func<T> action)
    {
        var app = Application.Current;
        if (app == null) throw new InvalidOperationException("LTools 界面尚未就绪");
        if (app.Dispatcher.CheckAccess()) return action();
        return app.Dispatcher.Invoke(action);
    }

    /// <summary>在 UI 线程上执行。</summary>
    public static void Run(Action action) => Run(() =>
    {
        action();
        return true;
    });

    /// <summary>
    /// 项目统一确认框（sandbox 下采纳 / 删除草稿、设置页重置令牌、暂存区页操作用）。取消 / 关闭返回 false。
    /// </summary>
    /// <remarks>
    /// MCP 回调线程经 <c>Run</c> 同步取结果，那一刻可能没有任何前台窗口：<see cref="ConfirmDialog"/>
    /// 在无 Owner 时自己置顶（见 <c>ShouldTopmost</c>），所以这里固定传 null Owner——
    /// 别拿隐藏的 Topmost 面板当 Owner，那样会闪黑并挡住长按右键手势。
    /// </remarks>
    public static bool Confirm(string title, string message, bool danger = false, string? okText = null)
    {
        return Run(() => ConfirmDialog.Confirm(null, "LTools · " + title, message, danger, okText));
    }
}
