using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>握手请求 / 答复（连接上来的第一条消息，<see cref="LinkServer"/> 只认它）。</summary>
internal sealed record LinkHello(int Protocol, string Token, string Role);

internal sealed record LinkHelloAck(int Protocol, string Version);

/// <summary>链路层的失败（连不上 / 握手不对 / 对端断开）。超时另走 <see cref="TimeoutException"/>。</summary>
internal sealed class LinkException : Exception
{
    public LinkException(string message) : base(message) { }
}

/// <summary>两端的版本串（握手回执里带出去，排查「主程序 / 宿主版本不一致」用）。</summary>
internal static class LinkBits
{
    public static string Version { get; } =
        typeof(LinkBits).Assembly.GetName().Version?.ToString() ?? "0";
}

/// <summary>
/// 一条已经建立的链路：一个 <see cref="Stream"/>（命名管道），负责帧切分、请求 / 答复配对、
/// 事件分发与断开收尾。服务端与客户端共用，不区分方向。
/// </summary>
/// <remarks>
/// 读循环是唯一的读者；写有锁。<b>对方请求的处理绝不能同步跑在读循环里</b>（会把后续帧堵住），
/// 每条请求丢给线程池，答复再回写。断开时所有在等的请求立即失败，不会挂住调用方。
/// </remarks>
internal sealed class LinkChannel : IAsyncDisposable
{
    private const int InitialBuffer = 8 * 1024;

    private readonly Stream _stream;
    private readonly Func<LinkChannel, LinkMessage, Task<LinkMessage>>? _handler;
    private readonly Action<LinkMessage>? _onEvent;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<LinkMessage>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _reader;
    private int _nextId;
    private int _closed;

    public LinkChannel(Stream stream, Func<LinkChannel, LinkMessage, Task<LinkMessage>>? handler = null,
        Action<LinkMessage>? onEvent = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _handler = handler;
        _onEvent = onEvent;
    }

    /// <summary>断开时触发（参数是原因，主动关闭为 null）。只在第一次断开时触发。</summary>
    public event Action<LinkChannel, string?>? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>服务端握手用：这一条连接已经通过 <c>hello</c>。</summary>
    public bool Greeted { get; set; }

    public void Start()
    {
        _reader ??= Task.Run(ReadLoopAsync);
    }

    /// <summary>发一条请求并等答复；超时 / 断开都抛异常。</summary>
    public async Task<LinkMessage> RequestAsync(string method, object? payload = null,
        int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new LinkException("连接已断开");

        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            await SendAsync(LinkMessage.Request(id, method, payload), cancellationToken).ConfigureAwait(false);

            using var timeout = new CancellationTokenSource(Math.Max(1, timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            using CancellationTokenRegistration registration =
                linked.Token.Register(() => completion.TrySetCanceled(linked.Token));

            try
            {
                return await completion.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException($"请求「{method}」超时（{timeoutMs} ms）");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>发一条单向通知（不等答复）。</summary>
    public Task NotifyAsync(string method, object? payload = null, CancellationToken cancellationToken = default) =>
        SendAsync(LinkMessage.Event(method, payload), cancellationToken);

    public async Task CloseAsync(string? reason = null)
    {
        Close(reason);
        if (_reader != null)
        {
            try { await _reader.ConfigureAwait(false); } catch { /* 读循环自己的异常 */ }
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync("主动关闭").ConfigureAwait(false);

    // ============ 读 ============

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[InitialBuffer];
        var chunk = new byte[16 * 1024];
        int filled = 0;

        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(chunk, _shutdown.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    Close("对端关闭");
                    return;
                }

                EnsureCapacity(ref buffer, filled + read);
                Buffer.BlockCopy(chunk, 0, buffer, filled, read);
                filled += read;

                int offset = 0;
                while (LinkFraming.TryRead(buffer.AsSpan(offset, filled - offset),
                           out LinkMessage? message, out int consumed, out string? error))
                {
                    offset += consumed;
                    Dispatch(message!);
                }

                if (offset > 0 && offset < filled) Buffer.BlockCopy(buffer, offset, buffer, 0, filled - offset);
                filled -= offset;

                // 剩下不够一帧：判断是「还差数据」还是「帧坏了」。坏帧说明协议已经对不上，
                // 继续读只会把后面的数据当垃圾。
                LinkFraming.TryRead(buffer.AsSpan(0, filled), out _, out _, out string? framingError);
                if (framingError != null)
                {
                    Close(framingError);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Close(null);
        }
        catch (Exception ex)
        {
            Close("读失败：" + ex.Message);
        }
    }

    private static void EnsureCapacity(ref byte[] buffer, int needed)
    {
        if (needed <= buffer.Length) return;

        int size = buffer.Length;
        while (size < needed && size < LinkFraming.MaxFrameBytes + LinkFraming.HeaderSize) size *= 2;
        Array.Resize(ref buffer, Math.Max(needed, Math.Min(size, LinkFraming.MaxFrameBytes + LinkFraming.HeaderSize)));
    }

    private void Dispatch(LinkMessage message)
    {
        switch (message.Kind)
        {
            case LinkKinds.Response:
                if (_pending.TryRemove(message.Id, out TaskCompletionSource<LinkMessage>? completion))
                    completion.TrySetResult(message);
                break;

            case LinkKinds.Event:
                try { _onEvent?.Invoke(message); }
                catch (Exception ex) { AppLog.Warn("Link", $"事件处理失败（{message.Method}）：{ex.Message}"); }
                break;

            default:
                _ = RespondAsync(message);
                break;
        }
    }

    private async Task RespondAsync(LinkMessage request)
    {
        LinkMessage response;
        try
        {
            response = _handler == null
                ? LinkMessage.Response(request, error: "对端不支持请求")
                : await _handler(this, request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            response = LinkMessage.Response(request, error: ex.Message);
        }

        try { await SendAsync(response).ConfigureAwait(false); }
        catch (Exception ex) { AppLog.Warn("Link", $"回写答复失败（{request.Method}）：{ex.Message}"); }
    }

    // ============ 写 ============

    private async Task SendAsync(LinkMessage message, CancellationToken cancellationToken = default)
    {
        byte[] frame = LinkFraming.Frame(message);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ============ 收尾 ============

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;

        try { _shutdown.Cancel(); } catch { /* 已释放 */ }

        foreach (KeyValuePair<int, TaskCompletionSource<LinkMessage>> pair in _pending)
            pair.Value.TrySetException(new LinkException("连接已断开" + (reason == null ? "" : "：" + reason)));
        _pending.Clear();

        try { _stream.Dispose(); } catch { /* 已释放 */ }

        try { Closed?.Invoke(this, reason); } catch { /* 订阅方自己吞 */ }
    }
}
