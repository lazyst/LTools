using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core.Actions;

namespace LTools.Core.Link;

/// <summary>
/// 交互类动作单元的**执行器侧**：把「要用户输入 / 选择 / 确认」转给普通权限宿主，宿主没连上就在本地弹。
/// </summary>
/// <remarks>
/// <para>
/// 动作在提权进程里跑（要注入、要 UIA、要脚本），但界面归宿主——所以弹窗这件事要过链路。
/// 返回 <c>null</c> 表示**没转交**（宿主没起 / 刚启动还没连上 / 对端出错），调用方照旧用本地窗口，
/// 这样「宿主挂了」只是体验降级，不会让动作卡死。
/// </para>
/// <para>
/// 方法都是阻塞的：调用它们的是动作的执行线程（本来就是同步的步骤单元），不是 UI 或钩子线程。
/// 停止热键通过 <see cref="CancellationToken"/> 打断等待，并让宿主把还开着的窗口关掉。
/// </para>
/// </remarks>
internal static class UiBridge
{
    /// <summary>要一段文本；<c>null</c> = 没转交（调用方本地弹）。</summary>
    public static UiInputReply? TryInput(string title, string prompt, string defaultValue, bool multiline,
        CancellationToken token)
    {
        string dialogId = NewDialogId();
        var request = new UiInputRequest(dialogId, title, prompt, defaultValue, multiline);
        return Ask<UiInputReply>(UiMethods.Input, request, dialogId, timeoutMs: 0, token);
    }

    /// <summary>确认框 / 选择器；<c>null</c> = 没转交。</summary>
    public static UiChoiceReply? TryChoice(string title, string message,
        IReadOnlyList<string> options, IReadOnlyList<string> buttons, bool selectMode, int timeoutMs,
        CancellationToken token)
    {
        string dialogId = NewDialogId();
        var request = new UiChoiceRequest(dialogId, title, message,
            new List<string>(options), new List<string>(buttons), selectMode, timeoutMs);
        return Ask<UiChoiceReply>(UiMethods.Choice, request, dialogId, timeoutMs, token);
    }

    private static string NewDialogId() => Guid.NewGuid().ToString("N");

    private static T? Ask<T>(string method, object request, string dialogId, int timeoutMs, CancellationToken token)
        where T : class
    {
        if (!LinkWorker.IsConnected) return null;

        // 「一直等」（timeoutMs = 0）也给链路一个够长的上限：真正的超时由宿主那侧算，
        // 这里只是防止对端消失后永远挂着（链路层把 0 当成 1ms，不能传 0）。
        int rpcTimeout = timeoutMs > 0 ? timeoutMs + 5000 : 12 * 60 * 60 * 1000;

        try
        {
            LinkMessage reply = LinkWorker.RequestAsync(method, request, rpcTimeout, token)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("Ui", $"宿主没能弹出交互窗口（{method}）：{reply.Error}，退回本地弹窗");
                Cancel(dialogId);
                return null;
            }

            return reply.Read<T>();
        }
        catch (OperationCanceledException)
        {
            // 停止热键：把宿主那个窗口关掉（它自己不知道流程已经停了），再按「已停止」抛出。
            Cancel(dialogId);
            throw new ActionAbortedException("已停止", userCancelled: true);
        }
        catch (Exception ex) when (ex is not ActionAbortedException)
        {
            // 超时 / 对端异常：宿主可能已经把窗口开出来了，先让它收掉，再退回本地弹窗。
            AppLog.Warn("Ui", $"交互请求失败（{method}）：{ex.Message}，退回本地弹窗");
            Cancel(dialogId);
            return null;
        }
    }

    /// <summary>让宿主关掉还开着的交互窗口（幂等；没开就是 no-op）。</summary>
    private static void Cancel(string dialogId) =>
        _ = LinkWorker.NotifyAsync(UiMethods.Cancel, new UiCancelRequest(dialogId));
}
