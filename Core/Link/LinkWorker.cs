using System;
using System.Threading;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// 提权进程这一侧的链路客户端：随 LTools 启动，后台反复试连宿主的管道，
/// 连上后用 <see cref="PanelHost"/> 应答面板命令。
/// </summary>
/// <remarks>
/// <para>
/// 方向是「宿主（普通权限）做服务端、提权进程做客户端」——中完整性进程不能写高完整性的命名管道，
/// 反过来可以（见 <see cref="LinkServer"/> 的说明与 <c>docs/PROCESS_SPLIT.md</c> §5.1）。
/// </para>
/// <para>
/// 宿主可能晚启动 / 重启：这里带退避重连（250ms → 最多 5s）。启动与关停都不阻塞 UI；
/// 关停只取消重连循环，不等它退出。
/// </para>
/// </remarks>
internal static class LinkWorker
{
    private static CancellationTokenSource? _cts;
    private static LinkClient? _client;

    /// <summary>后台重连循环已启动。</summary>
    public static bool IsRunning => Volatile.Read(ref _cts) != null;

    /// <summary>当前连着宿主。</summary>
    public static bool IsConnected => Volatile.Read(ref _client) is { IsOpen: true };

    /// <summary>启动后台重连（幂等）。</summary>
    public static void Start()
    {
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) != null)
        {
            cts.Dispose();
            return;
        }

        CancellationToken token = cts.Token;

        // 令牌由提权进程写（可信写入方）：宿主只读它。同步写，保证随后拉起的宿主能读到。
        LinkToken.Ensure();

        _ = Task.Run(() => RunAsync(token));
    }

    /// <summary>给宿主推一条事件；没有宿主就丢掉（通知是「尽量送到」）。</summary>
    public static Task NotifyAsync(string method, object? payload = null, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _client)?.NotifyAsync(method, payload, cancellationToken) ?? Task.CompletedTask;

    /// <summary>
    /// 向宿主发一条请求（交互类动作单元用它弹窗：界面归宿主）。
    /// 没连上时抛 <see cref="InvalidOperationException"/>，调用方据此退回本地弹窗。
    /// </summary>
    public static Task<LinkMessage> RequestAsync(string method, object? payload = null,
        int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        LinkClient? client = Volatile.Read(ref _client);
        if (client is not { IsOpen: true })
            return Task.FromException<LinkMessage>(new InvalidOperationException("没连上界面宿主"));

        return client.RequestAsync(method, payload, timeoutMs, cancellationToken);
    }

    /// <summary>
    /// 把「显示面板」交给宿主。宿主还没连上（主程序刚启动那几秒）就**记着**，连上后补发——
    /// 面板只有一个家（普通权限宿主），提权进程不再自己开一份。
    /// 光标坐标在这里取，宿主拿到的就是唤起瞬间的位置。
    /// </summary>
    public static void RequestShow(IntPtr contextHwnd)
    {
        int x = 0, y = 0;
        try
        {
            if (LTools.Native.Win32.GetCursorPos(out LTools.Native.Win32.Point pt)) { x = pt.X; y = pt.Y; }
        }
        catch { /* 取不到就让宿主自己定位 */ }

        Interlocked.Exchange(ref _pendingShow, new PendingShow(contextHwnd, x, y));
        FlushPending();
    }

    /// <summary>把「开 / 关面板」交给宿主；没连上同样记着，连上后补发。</summary>
    public static void RequestToggle()
    {
        Interlocked.Exchange(ref _pendingToggle, 1);
        FlushPending();
    }

    /// <summary>把「显示面板并停在暂存区」交给宿主（同样可以在没连上时先记着）。</summary>
    public static void RequestStash()
    {
        Interlocked.Exchange(ref _pendingStash, 1);
        FlushPending();
    }

    /// <summary>
    /// 让宿主关掉面板（点悬浮按钮那一下算「点到面板外面」）。
    /// **不排队**：宿主没连上时直接丢掉——等连上再关会关掉后面新开的那一扇。
    /// </summary>
    public static void RequestClose()
    {
        if (Volatile.Read(ref _client) is not { IsOpen: true }) return;
        _ = NotifyAsync(LinkMethods.EventPanelClose);
    }

    /// <summary>
    /// 把「开关速记」交给宿主（窗口只有一个家，见 <see cref="Features.QuickNote.Toggle"/>）。
    /// 没连上同样先记着，连上后补发。
    /// </summary>
    public static void RequestNote()
    {
        Interlocked.Exchange(ref _pendingNote, 1);
        FlushPending();
    }

    /// <summary>把「开关辅助工具」交给宿主（窗口只有一个家，见 <see cref="Views.InfoHelperWindow.Toggle"/>）。</summary>
    public static void RequestInfoHelper()
    {
        Interlocked.Exchange(ref _pendingInfoHelper, 1);
        FlushPending();
    }

    private sealed record PendingShow(IntPtr Hwnd, int X, int Y);

    private static PendingShow? _pendingShow;
    private static int _pendingToggle;
    private static int _pendingStash;
    private static int _pendingNote;
    private static int _pendingInfoHelper;

    /// <summary>宿主连上了 / 刚被要求：把攒着的那一次补发出去（同一件事只留最后一次）。</summary>
    private static void FlushPending()
    {
        if (Volatile.Read(ref _client) is not { IsOpen: true }) return;

        if (Interlocked.Exchange(ref _pendingToggle, 0) != 0)
            _ = NotifyAsync(LinkMethods.EventPanelToggle);

        if (Interlocked.Exchange(ref _pendingStash, 0) != 0)
            _ = NotifyAsync(LinkMethods.EventPanelStash);

        if (Interlocked.Exchange(ref _pendingShow, null) is { } show)
            _ = NotifyAsync(LinkMethods.EventPanelShow, new PanelShowRequest(show.Hwnd.ToInt64(), show.X, show.Y));

        if (Interlocked.Exchange(ref _pendingNote, 0) != 0)
            _ = NotifyAsync(NoteMethods.Toggle);

        if (Interlocked.Exchange(ref _pendingInfoHelper, 0) != 0)
            _ = NotifyAsync(LinkMethods.EventInfoHelper);
    }

    /// <summary>停止重连并断开（幂等）。不等后台任务退出。</summary>
    public static void Stop()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { /* 已释放 */ }

        LinkClient? client = Interlocked.Exchange(ref _client, null);
        if (client != null) _ = client.DisposeAsync();
    }

    /// <summary>
    /// 宿主发来的事件：面板 / 点选器状态（钩子靠它们让开「长按右键 = 面板」手势）、
    /// 遮罩关窗要吞掉的抬起、录屏跑完的通知。
    /// </summary>
    private static void HandleHostEvent(LinkMessage message)
    {
        try
        {
            if (message.Method == LinkMethods.EventPanelState && message.Read<PanelStateEvent>() is { } state)
                PanelHostBridge.SetRemotePanelOpen(state.Open);
            else if (message.Method == LinkMethods.EventPickerActive
                     && message.Read<PickerActiveEvent>() is { } picker)
                Hooks.MouseHook.SetWindowPickerActive(picker.Active);
            else if (message.Method == LinkMethods.EventInputSwallow
                     && message.Read<SwallowInputEvent>() is { } swallow)
                Features.CapturePro.ExitInput.Apply(swallow);
            else if (message.Method == LinkMethods.EventCaptureRecordDone)
                CaptureBridge.OnRecordingFinished();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"处理宿主事件失败（{message.Method}）：{ex.Message}");
        }
    }

    private static async Task RunAsync(CancellationToken token)
    {
        int delay = 250;

        while (!token.IsCancellationRequested)
        {
            LinkClient? client = null;
            try
            {
                // 令牌由提权进程写（宿主只读）：还没写就等一等，别猜。
                string? secret = LinkToken.Read();
                if (secret == null)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    continue;
                }

                client = await LinkClient.ConnectAsync(LinkToken.PipeName, secret, "worker",
                    onEvent: HandleHostEvent, timeoutMs: 800, cancellationToken: token,
                    handler: PanelHost.HandleAsync)
                    .ConfigureAwait(false);

                Volatile.Write(ref _client, client);
                delay = 250;

                // 连上之前按的「显示 / 切换面板」不能丢：补发出去（面板只有一个家）。
                FlushPending();

                try { await client.NotifyAsync(LinkMethods.EventClientReady, cancellationToken: token).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Warn("Link", $"通知宿主就绪失败：{ex.Message}"); }

                AppLog.Info("Link", "已连上界面宿主");

                // 断开（宿主退出 / 重启）就回去重连。
                await client.WhenClosed.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 宿主还没起 / 正在重启：正常，退避后再试（不刷日志）。
            }
            finally
            {
                if (client != null)
                {
                    Interlocked.CompareExchange(ref _client, null, client);
                    PanelHostBridge.SetRemotePanelOpen(false);   // 断开就当面板没了
                    try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* 已断开 */ }
                }
            }

            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            delay = Math.Min(delay * 2, 5000);
        }
    }
}
