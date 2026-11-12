using System.Windows;
using LTools.Core.Actions;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「确认框 / 选择器」的打开与等待：任意线程可调（内部投递到 UI 线程），流程在后台线程轮询结果。
/// </summary>
/// <remarks>
/// 不用 <c>ShowDialog</c>：模态对话框会把 UI 线程卡在它自己的消息循环里，停止热键就中止不了流程。
/// 这里窗口照常 Show，动作单元用 <see cref="Wait"/> 轮询，超时 / 停止都能把窗口关掉。
/// </remarks>
internal static class ChoiceWindowService
{
    /// <summary>打开一个确认框（<paramref name="selectMode"/> = false）或选择器（true）。</summary>
    public static ChoiceWindow Open(string title, string message,
        IReadOnlyList<string> options, IReadOnlyList<string> buttons, bool selectMode)
    {
        var app = Application.Current
            ?? throw new ActionException("界面未初始化，无法弹出对话框");

        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Open(title, message, options, buttons, selectMode));

        var window = new ChoiceWindow(title, message, options, buttons, selectMode);
        window.Show();
        window.Activate();
        window.Focus();
        return window;
    }

    /// <summary>
    /// 等用户作答：返回 <c>(是否作答, 下标, 文本)</c>。超时关窗并返回 false；停止热键关窗并抛中止。
    /// </summary>
    public static (bool Accepted, int Index, string Value) Wait(StepContext ctx, ChoiceWindow window, int timeoutMs)
    {
        var app = Application.Current
            ?? throw new ActionException("界面未初始化");

        DateTime? deadline = timeoutMs > 0 ? DateTime.UtcNow.AddMilliseconds(timeoutMs) : null;

        while (true)
        {
            var state = app.Dispatcher.Invoke(() =>
                (Closed: window.IsClosed, window.Accepted, window.Index, window.Value));

            if (state.Closed) return (state.Accepted, state.Index, state.Value);

            if (deadline.HasValue && DateTime.UtcNow >= deadline.Value)
            {
                Close(window);
                return (false, -1, "");
            }

            if (!ctx.Wait(50))
            {
                Close(window);
                throw new ActionAbortedException("已停止", userCancelled: true);
            }
        }
    }

    /// <summary>关掉窗口（已经关了就什么都不做）。</summary>
    public static void Close(ChoiceWindow? window)
    {
        if (window == null) return;

        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => Close(window));
            return;
        }

        if (!window.IsClosed) window.Close();
    }
}
