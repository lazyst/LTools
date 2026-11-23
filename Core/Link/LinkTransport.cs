using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace LTools.Core.Link;

/// <summary>
/// 链路服务端（现在跑在提权进程里，进程拆分完成后就是执行器）。一次只服务**一个**客户端：
/// 断了下一条连接接上，状态按「当前客户端」算。
/// </summary>
/// <remarks>
/// 第一条消息必须是 <see cref="LinkHello"/>（协议版本 + 令牌），否则拒绝——挡的是「别的会话 /
/// 意外的进程连进来」，**挡不住同用户的注入**（见 <c>docs/PROCESS_SPLIT.md</c> §8）。
/// </remarks>
internal sealed class LinkServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly string _token;
    private readonly Func<LinkChannel, LinkMessage, Task<LinkMessage>> _handler;
    private readonly Action<LinkMessage>? _onEvent;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _loop;
    private LinkChannel? _client;

    public LinkServer(string pipeName, string token,
        Func<LinkChannel, LinkMessage, Task<LinkMessage>> handler,
        Action<LinkMessage>? onEvent = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("管道名不能为空", nameof(pipeName));
        _pipeName = pipeName;
        _token = token ?? "";
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _onEvent = onEvent;
    }

    public string PipeName => _pipeName;

    public bool IsConnected => _client is { IsOpen: true };

    /// <summary>客户端连上（true）/ 断开（false）。</summary>
    public event Action<bool>? ClientChanged;

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

    /// <summary>让当前客户端做一件事；没有客户端时抛 <see cref="LinkException"/>。</summary>
    public Task<LinkMessage> RequestAsync(string method, object? payload = null,
        int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        LinkChannel client = _client is { IsOpen: true } current
            ? current
            : throw new LinkException("没有客户端连接");
        return client.RequestAsync(method, payload, timeoutMs, cancellationToken);
    }

    /// <summary>给当前客户端发一条通知；没有客户端就丢掉（通知是「尽量送到」）。</summary>
    public Task NotifyAsync(string method, object? payload = null, CancellationToken cancellationToken = default) =>
        _client is { IsOpen: true } client ? client.NotifyAsync(method, payload, cancellationToken) : Task.CompletedTask;

    public async Task StopAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();

        if (_client is { } client) await client.CloseAsync("服务端停止").ConfigureAwait(false);

        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 停的时候读循环会抛 */ }
            _loop = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ============ 接受连接 ============

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);

                var channel = new LinkChannel(pipe, HandleAsync, _onEvent);
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                channel.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_client, channel))
                    {
                        _client = null;
                        ClientChanged?.Invoke(false);
                    }

                    closed.TrySetResult();
                };

                _client = channel;

                // 管道交给 channel 释放；这一轮不再管它。
                pipe = null;
                channel.Start();
                ClientChanged?.Invoke(true);

                await closed.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Link", $"等待连接失败：{ex.Message}");
                try { await Task.Delay(300, _shutdown.Token).ConfigureAwait(false); }
                catch { break; }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    // ============ 握手 ============

    private async Task<LinkMessage> HandleAsync(LinkChannel channel, LinkMessage request)
    {
        if (!channel.Greeted)
        {
            if (!string.Equals(request.Method, "hello", StringComparison.Ordinal))
                return LinkMessage.Response(request, error: "第一条消息必须是 hello");

            LinkHello? hello = request.Read<LinkHello>();
            if (hello == null || hello.Protocol != LinkNames.Protocol)
                return LinkMessage.Response(request, error: $"协议版本不一致（本端 {LinkNames.Protocol}，对端 {hello?.Protocol}）");

            if (!string.Equals(hello.Token, _token, StringComparison.Ordinal))
                return LinkMessage.Response(request, error: "令牌不对");

            channel.Greeted = true;
            AppLog.Info("Link", $"客户端连上（{hello.Role}）");
            return LinkMessage.Response(request, new LinkHelloAck(LinkNames.Protocol, LinkBits.Version));
        }

        return await _handler(channel, request).ConfigureAwait(false);
    }
}

/// <summary>
/// 链路客户端（现在跑在普通权限宿主里，进程拆分完成后就是整个界面进程）。
/// </summary>
internal sealed class LinkClient : IAsyncDisposable
{
    private readonly LinkChannel _channel;

    private LinkClient(LinkChannel channel)
    {
        _channel = channel;
        channel.Closed += (_, reason) => Closed?.Invoke(reason);
    }

    public bool IsOpen => _channel.IsOpen;

    /// <summary>断开时触发（参数是原因，主动关闭为 null）。</summary>
    public event Action<string?>? Closed;

    /// <summary>连上并完成握手；连不上 / 被拒绝都抛异常（超时是 <see cref="TimeoutException"/>）。</summary>
    public static async Task<LinkClient> ConnectAsync(string pipeName, string token, string role = "host",
        Action<LinkMessage>? onEvent = null, int timeoutMs = 3000, CancellationToken cancellationToken = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        LinkChannel? channel = null;

        try
        {
            await pipe.ConnectAsync(Math.Max(1, timeoutMs), cancellationToken).ConfigureAwait(false);

            channel = new LinkChannel(pipe, handler: null, onEvent);
            channel.Start();

            LinkMessage ack = await channel
                .RequestAsync("hello", new LinkHello(LinkNames.Protocol, token, role), timeoutMs, cancellationToken)
                .ConfigureAwait(false);

            if (!ack.Ok) throw new LinkException(ack.Error ?? "握手被拒绝");

            return new LinkClient(channel);
        }
        catch
        {
            if (channel != null) await channel.CloseAsync().ConfigureAwait(false);
            else pipe.Dispose();
            throw;
        }
    }

    /// <summary>在 <paramref name="totalMs"/> 内反复试连（宿主启动时服务端可能还没就绪）。</summary>
    public static async Task<LinkClient> ConnectWithRetryAsync(string pipeName, string token, string role = "host",
        Action<LinkMessage>? onEvent = null, int totalMs = 5000, CancellationToken cancellationToken = default)
    {
        long deadline = Environment.TickCount64 + Math.Max(1, totalMs);
        Exception last = new LinkException("连不上");

        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int slice = (int)Math.Clamp(deadline - Environment.TickCount64, 100, 800);
            try
            {
                return await ConnectAsync(pipeName, token, role, onEvent, slice, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                try { await Task.Delay(150, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }
        }

        throw last;
    }

    public Task<LinkMessage> RequestAsync(string method, object? payload = null,
        int timeoutMs = 5000, CancellationToken cancellationToken = default) =>
        _channel.RequestAsync(method, payload, timeoutMs, cancellationToken);

    public Task NotifyAsync(string method, object? payload = null, CancellationToken cancellationToken = default) =>
        _channel.NotifyAsync(method, payload, cancellationToken);

    public async Task CloseAsync(string? reason = null) => await _channel.CloseAsync(reason).ConfigureAwait(false);

    public async ValueTask DisposeAsync() => await _channel.DisposeAsync().ConfigureAwait(false);
}
