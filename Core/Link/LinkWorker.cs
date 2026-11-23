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

    /// <summary>停止重连并断开（幂等）。不等后台任务退出。</summary>
    public static void Stop()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        try { cts?.Cancel(); } catch { /* 已释放 */ }

        LinkClient? client = Interlocked.Exchange(ref _client, null);
        if (client != null) _ = client.DisposeAsync();
    }

    private static async Task RunAsync(CancellationToken token)
    {
        int delay = 250;

        while (!token.IsCancellationRequested)
        {
            LinkClient? client = null;
            try
            {
                // 令牌由宿主写（它是服务端）。还没写就等一等，别猜。
                string? secret = LinkToken.Read();
                if (secret == null)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    continue;
                }

                client = await LinkClient.ConnectAsync(LinkToken.PipeName, secret, "worker",
                    onEvent: null, timeoutMs: 800, cancellationToken: token, handler: PanelHost.HandleAsync)
                    .ConfigureAwait(false);

                Volatile.Write(ref _client, client);
                delay = 250;

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
                    try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* 已断开 */ }
                }
            }

            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            delay = Math.Min(delay * 2, 5000);
        }
    }
}
