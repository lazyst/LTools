using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Views;

namespace LTools.Core.Link;

/// <summary>
/// 交互类动作单元的**宿主侧**：替提权进程弹窗（要文本 / 确认 / 选择），把结果回过去。
/// </summary>
/// <remarks>
/// <para>
/// 动作在提权进程里执行，界面归这里——所以它是「执行器 → 宿主」的请求处理端，
/// 与 <see cref="PanelHost"/>（宿主 → 执行器）方向相反。见 <c>docs/PROCESS_SPLIT.md</c> §15.2。
/// </para>
/// <para>
/// 窗口一律**非模态** + 等 <c>Closed</c>：模态框会把 UI 线程卡在自己的消息循环里，
/// 停止热键 / <c>ui.cancel</c> 就关不掉它。每个窗口按 <c>dialogId</c> 登记，取消时按 id 找。
/// </para>
/// </remarks>
internal static class UiHost
{
    private static readonly ConcurrentDictionary<string, Action> Open = new(StringComparer.Ordinal);

    /// <summary>处理一条交互请求；不是交互方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) => request.Method switch
    {
        UiMethods.Input => InputAsync(request),
        UiMethods.Choice => ChoiceAsync(request),
        UiMethods.Cancel => Task.FromResult(Cancel(request)),
        _ => null,
    };

    private static async Task<LinkMessage> InputAsync(LinkMessage request)
    {
        UiInputRequest? req = request.Read<UiInputRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        var done = new TaskCompletionSource<UiInputReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Ui.Post(() =>
        {
            try
            {
                InputDialog dialog = InputDialog.ShowNonModal(req.Title, req.Prompt, req.Default, req.Multiline);
                Open[req.DialogId] = () => CloseIfOpen(dialog);
                dialog.Closed += (_, _) =>
                {
                    Open.TryRemove(req.DialogId, out _);
                    done.TrySetResult(new UiInputReply(dialog.Ok, dialog.Value ?? ""));
                };
            }
            catch (Exception ex)
            {
                Open.TryRemove(req.DialogId, out _);
                done.TrySetException(ex);
            }
        });

        try
        {
            UiInputReply reply = await done.Task.ConfigureAwait(false);
            return LinkMessage.Response(request, reply);
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
    }

    private static async Task<LinkMessage> ChoiceAsync(LinkMessage request)
    {
        UiChoiceRequest? req = request.Read<UiChoiceRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        var done = new TaskCompletionSource<UiChoiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Ui.Post(() =>
        {
            try
            {
                ChoiceWindow window = ChoiceWindowService.Open(
                    req.Title, req.Message,
                    req.Options ?? new List<string>(), req.Buttons ?? new List<string>(), req.SelectMode);

                DispatcherTimer? timer = null;
                if (req.TimeoutMs > 0)
                {
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(req.TimeoutMs) };
                    timer.Tick += (_, _) =>
                    {
                        timer!.Stop();
                        ChoiceWindowService.Close(window);
                    };
                    timer.Start();
                }

                Open[req.DialogId] = () => ChoiceWindowService.Close(window);
                window.Closed += (_, _) =>
                {
                    timer?.Stop();
                    Open.TryRemove(req.DialogId, out _);
                    done.TrySetResult(new UiChoiceReply(window.Accepted, window.Index, window.Value ?? ""));
                };
            }
            catch (Exception ex)
            {
                Open.TryRemove(req.DialogId, out _);
                done.TrySetException(ex);
            }
        });

        try
        {
            UiChoiceReply reply = await done.Task.ConfigureAwait(false);
            return LinkMessage.Response(request, reply);
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
    }

    private static LinkMessage Cancel(LinkMessage request)
    {
        UiCancelRequest? req = request.Read<UiCancelRequest>();
        if (req == null || !Open.TryRemove(req.DialogId, out Action? close))
            return LinkMessage.Response(request);

        Ui.Post(close);
        return LinkMessage.Response(request);
    }

    private static void CloseIfOpen(InputDialog dialog)
    {
        if (dialog.IsVisible) dialog.Close();
    }
}
