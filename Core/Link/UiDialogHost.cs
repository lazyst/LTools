using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
internal static class UiDialogHost
{
    private static readonly ConcurrentDictionary<string, Action> Open = new(StringComparer.Ordinal);

    /// <summary>
    /// 交互框**同时只显示一个**（见 <c>docs/RUN_INTEROP.md</c> §7.1）：并发时两个 <c>ui.input</c>
    /// 同时弹出来，用户分不清哪个属于哪个动作。等待前一个关掉的请求标题加「（等前一个输入框）」。
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>还没显示就被取消的 dialogId（排队期间的 <c>ui.cancel</c>）。</summary>
    private static readonly ConcurrentDictionary<string, byte> Cancelled = new(StringComparer.Ordinal);

    /// <summary>
    /// 排到交互框的队里；返回是否真的拿到（false = 等待期间被取消，调用方回一个「取消」答复）。
    /// </summary>
    private static async Task<bool> AcquireAsync(string dialogId)
    {
        if (Gate.Wait(0))
        {
            if (!Cancelled.ContainsKey(dialogId)) return true;
            Gate.Release();
            return false;
        }

        // 每 100ms 看一次有没有被取消：停止热键 / 超时不该让用户等前一个框关掉才生效。
        while (!await Gate.WaitAsync(100).ConfigureAwait(false))
        {
            if (!Cancelled.ContainsKey(dialogId)) continue;
            return false;
        }

        if (!Cancelled.ContainsKey(dialogId)) return true;
        Gate.Release();
        return false;
    }

    /// <summary>排队时给标题加一句说明（已经在等前一个框）。</summary>
    private static string QueuedTitle(string? title) =>
        Gate.CurrentCount == 0 ? $"{title}（等前一个输入框）" : title ?? "";

    /// <summary>处理一条交互请求；不是交互方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) => request.Method switch
    {
        UiMethods.Input => InputAsync(request),
        UiMethods.Choice => ChoiceAsync(request),
        UiMethods.TextWindow => TextWindowAsync(request),
        UiMethods.SelectFile => SelectFileAsync(request),
        UiMethods.PickPoint => PickPointAsync(request),
        UiMethods.Cancel => Task.FromResult(Cancel(request)),
        _ => null,
    };

    private static async Task<LinkMessage> InputAsync(LinkMessage request)
    {
        UiInputRequest? req = request.Read<UiInputRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        string title = QueuedTitle(req.Title);
        if (!await AcquireAsync(req.DialogId).ConfigureAwait(false))
            return LinkMessage.Response(request, new UiInputReply(false, ""));

        var done = new TaskCompletionSource<UiInputReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Ui.Post(() =>
        {
            try
            {
                InputDialog dialog = InputDialog.ShowNonModal(title, req.Prompt, req.Default, req.Multiline);
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
        finally
        {
            Gate.Release();
            Cancelled.TryRemove(req.DialogId, out _);
        }
    }

    private static async Task<LinkMessage> ChoiceAsync(LinkMessage request)
    {
        UiChoiceRequest? req = request.Read<UiChoiceRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        string title = QueuedTitle(req.Title);
        if (!await AcquireAsync(req.DialogId).ConfigureAwait(false))
            return LinkMessage.Response(request, new UiChoiceReply(false, -1, ""));

        var done = new TaskCompletionSource<UiChoiceReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Ui.Post(() =>
        {
            try
            {
                ChoiceWindow window = ChoiceWindowService.Open(
                    title, req.Message,
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
        finally
        {
            Gate.Release();
            Cancelled.TryRemove(req.DialogId, out _);
        }
    }

    private static async Task<LinkMessage> TextWindowAsync(LinkMessage request)
    {
        UiTextWindowRequest? req = request.Read<UiTextWindowRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        string mode = (req.Mode ?? "").Trim().ToLowerInvariant();
        string key = (req.Key ?? "").Trim();

        if (mode == "close")
        {
            TextWindowService.Close(key);
            return LinkMessage.Response(request, new UiTextWindowReply(true, "", false));
        }

        bool read = mode == "read";
        bool append = !read && req.Append;
        bool wait = !read && req.Wait;

        // 「显示」模式不排队（非阻塞、按 key 复用）；读取 / 等关闭会挡住用户，串行显示。
        bool blocks = read || wait;
        string title = QueuedTitle(req.Title);
        if (blocks && !await AcquireAsync(req.DialogId).ConfigureAwait(false))
            return LinkMessage.Response(request, new UiTextWindowReply(false, "", false));

        var done = new TaskCompletionSource<UiTextWindowReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        Ui.Post(() =>
        {
            try
            {
                // 显示模式不抢焦点（用户可能在别处继续操作），读取模式要抢（要打字）。
                TextViewWindow window = TextWindowService.Open(
                    key, title, req.Text ?? "", read, req.Monospace, append, activate: read);

                // 取消只关这一扇：key 为空时 TextWindowService.Close 会关掉全部文本窗口，太宽。
                Open[req.DialogId] = () =>
                {
                    if (!window.IsClosed) window.Close();
                };

                // 显示且不等：把当前内容带回去就继续（不把流程卡在窗口上）。
                if (!read && !wait)
                {
                    Open.TryRemove(req.DialogId, out _);
                    done.TrySetResult(new UiTextWindowReply(true, window.Text, false));
                    return;
                }

                window.Closed += (_, _) =>
                {
                    Open.TryRemove(req.DialogId, out _);
                    bool confirmed = read ? window.Confirmed : true;
                    done.TrySetResult(new UiTextWindowReply(
                        confirmed, confirmed ? window.Text : "", read && window.Changed));
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
            return LinkMessage.Response(request, await done.Task.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
        finally
        {
            if (blocks)
            {
                Gate.Release();
                Cancelled.TryRemove(req.DialogId, out _);
            }
        }
    }

    private static async Task<LinkMessage> SelectFileAsync(LinkMessage request)
    {
        UiSelectFileRequest? req = request.Read<UiSelectFileRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        string title = QueuedTitle(req.Title);
        // 系统文件框的请求里没有 dialogId（它关不掉，取消也不指望）：排队登记用一个本地 id。
        string queueId = "file-" + Guid.NewGuid().ToString("N")[..8];
        if (!await AcquireAsync(queueId).ConfigureAwait(false))
            return LinkMessage.Response(request, new UiSelectFileReply(false, ""));

        var done = new TaskCompletionSource<UiSelectFileReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 系统对话框是模态的：这里会占住 UI 线程直到用户选完 / 取消（关不掉，见 FilePicker 的说明）。
        Ui.Post(() =>
        {
            try
            {
                var (ok, path) = FilePicker.TryPick(req.Kind ?? "", title,
                    req.Filter ?? "", req.InitialDir ?? "");
                done.TrySetResult(new UiSelectFileReply(ok, path));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        try
        {
            return LinkMessage.Response(request, await done.Task.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
        finally
        {
            Gate.Release();
            Cancelled.TryRemove(queueId, out _);
        }
    }

    private static async Task<LinkMessage> PickPointAsync(LinkMessage request)
    {
        UiPickPointRequest? req = request.Read<UiPickPointRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        if (!await AcquireAsync(req.DialogId).ConfigureAwait(false))
            return LinkMessage.Response(request, new UiPickPointReply(false, 0, 0));

        var done = new TaskCompletionSource<UiPickPointReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 遮罩是 ShowDialog，会占住宿主 UI 线程直到用户单击 / 按 Esc（关不掉，见 ScreenPicker）。
        // 收起宿主的窗口与卡片、抓冻结画面、恢复现场都在 ScreenPicker 里，这里只管带结果回来。
        Ui.Post(() =>
        {
            try
            {
                Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                bool ok = ScreenPicker.TryPickPoint(owner, req.Hint ?? "", out int x, out int y);
                done.TrySetResult(new UiPickPointReply(ok, x, y));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        try
        {
            return LinkMessage.Response(request, await done.Task.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
        finally
        {
            Gate.Release();
            Cancelled.TryRemove(req.DialogId, out _);
        }
    }

    private static LinkMessage Cancel(LinkMessage request)
    {
        UiCancelRequest? req = request.Read<UiCancelRequest>();
        if (req == null) return LinkMessage.Response(request);

        if (Open.TryRemove(req.DialogId, out Action? close))
        {
            Ui.Post(close);
            return LinkMessage.Response(request);
        }

        // 还没弹出来（在等前一个交互框）：记下来，排到队首时直接给「取消」，别弹一个马上要关的框。
        if (Cancelled.Count > 128) Cancelled.Clear();   // 只是兜底：正常路径由 Acquire 消费掉
        Cancelled[req.DialogId] = 1;
        return LinkMessage.Response(request);
    }

    private static void CloseIfOpen(InputDialog dialog)
    {
        if (dialog.IsVisible) dialog.Close();
    }
}
