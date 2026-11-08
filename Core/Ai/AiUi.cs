using System.Windows;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 回调线程 ↔ WPF UI 线程的唯一通道。
/// </summary>
/// <remarks>
/// <para>
/// <c>ActionRegistry</c> / <c>ConfigStore</c> / <c>MenuSystem</c> 与 WPF 对象都不是线程安全的，
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

    /// <summary>原生确认框（沙箱模式下采纳 / 删除草稿用）。用户点「否」返回 false。</summary>
    public static bool Confirm(string title, string message)
    {
        return Run(() => MessageBox.Show(
            message,
            "LTools · " + title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes);
    }
}
