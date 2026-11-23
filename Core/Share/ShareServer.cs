using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LTools.Core.Share;

/// <summary>
/// 局域网共享服务（HTTP，Kestrel 绑 <c>0.0.0.0</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 只有本机跑服务，其他设备用浏览器打开固定地址。安全边界见 <c>docs/LAN_SHARE.md</c> §8：
/// 默认关闭、只接受选中网卡的同一子网、浏览器只读、令牌可选。
/// </para>
/// <para>
/// 线程：Kestrel 回调只碰 <see cref="ShareStore"/>（内部有锁）与文件流；写剪贴板走
/// <see cref="Native.StaRunner"/>，通知界面走 <see cref="Core.Ui.Post"/>，绝不在请求线程等 UI。
/// </para>
/// <para>
/// 关停照 <see cref="Ai.McpServer"/> 的教训：`Stop()` 只把 `StopAsync` / `DisposeAsync`
/// 排到后台任务，调用线程（可能是 UI）绝不等，否则退出会卡死托盘。
/// </para>
/// </remarks>
internal static class ShareServer
{
    private const int PerMinute = 120;
    private const int UploadsPerMinute = 10;

    private static readonly object Gate = new();
    private static readonly object ApplyGate = new();

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonIn = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static WebApplication? _app;
    private static Task _stopTask = Task.CompletedTask;
    private static ShareOptions _options = new();
    private static LanEndpoint? _endpoint;
    private static List<LanEndpoint> _endpoints = new();
    private static int _port;
    private static string? _lastDevice;
    private static DateTime? _lastSeen;

    /// <summary>状态 / 内容变化（窗口据此刷新）。可能在 Kestrel 线程上触发。</summary>
    public static event Action? Changed;

    public static bool IsRunning => _app != null;

    public static int RunningPort => _port;

    public static string? LastError { get; private set; }

    public static ShareOptions Options
    {
        get { lock (Gate) return _options; }
    }

    public static LanEndpoint? Endpoint
    {
        get { lock (Gate) return _endpoint; }
    }

    /// <summary>本机所有可用的局域网地址（第一项是主地址，二维码用它）。</summary>
    public static IReadOnlyList<LanEndpoint> Endpoints
    {
        get { lock (Gate) return _endpoints.ToArray(); }
    }

    public static string? LastDevice
    {
        get { lock (Gate) return _lastDevice; }
    }

    public static DateTime? LastSeen
    {
        get { lock (Gate) return _lastSeen; }
    }

    /// <summary>按配置启动 / 停止。启动时与设置保存后各调一次（后台线程更安全）。</summary>
    public static void Apply(ShareOptions options)
    {
        lock (ApplyGate)
        {
            Task pending;
            lock (Gate)
            {
                StopCore();
                pending = _stopTask;
            }

            try { pending.Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { AppLog.Warn("Share", $"等待上一次共享服务关停时出错：{ex.Message}"); }

            bool started;
            lock (Gate)
            {
                _options = (options ?? new ShareOptions()).Normalized();
                LastError = null;
                started = _options.Running;
                if (started)
                {
                    try
                    {
                        StartCore(_options);
                    }
                    catch (Exception ex)
                    {
                        LastError = ex.Message;
                        AppLog.Error("Share", $"共享服务启动失败（端口 {_options.Port}）：{ex.Message}", ex);
                        started = false;
                    }
                }
            }

            if (!started && LastError == null) AppLog.Info("Share", "共享服务未启用");
            RaiseChanged();
        }
    }

    /// <summary>只停服务（退出时用）。绝不在调用线程上等关停。</summary>
    public static void Stop()
    {
        lock (Gate) StopCore();
        RaiseChanged();
    }

    /// <summary>更新最近连接的设备名（浏览器页随请求头带过来）。</summary>
    public static void RememberDevice(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string trimmed = name.Trim();
        if (trimmed.Length > 40) trimmed = trimmed[..40];

        lock (Gate)
        {
            _lastDevice = trimmed;
            _lastSeen = DateTime.Now;
        }
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { /* 界面回调失败不影响服务 */ }
    }

    private static void StopCore()
    {
        var app = _app;
        _app = null;
        _port = 0;
        if (app == null) return;

        var previous = _stopTask;
        _stopTask = Task.Run(async () =>
        {
            try { await previous.ConfigureAwait(false); }
            catch { /* 上一次关停失败不影响这一次 */ }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { await app.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) { AppLog.Warn("Share", $"停止共享服务时出错：{ex.Message}"); }
            try { await app.DisposeAsync().ConfigureAwait(false); }
            catch { /* 已释放 */ }

            AppLog.Info("Share", $"共享服务已停止（{sw.ElapsedMilliseconds} ms）");
        });
    }

    private static void StartCore(ShareOptions options)
    {
        ShareStore.Default.EnsureLayout();
        _endpoints = LanAddress.Candidates().ToList();
        _endpoint = _endpoints.FirstOrDefault();
        _port = options.Port;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(ShareServer).Assembly.GetName().Name ?? "LTools",
            EnvironmentName = Environments.Production,
            Args = Array.Empty<string>(),
        });

        // WinExe 没有控制台；共享服务不落 ASP.NET 日志文件，错误统一走 AppLog。
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.WebHost.UseUrls($"http://0.0.0.0:{options.Port}");

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            PathString path = ctx.Request.Path;
            bool known = path == "/" || path.StartsWithSegments("/s") || path.StartsWithSegments("/api");
            if (!known)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            IPAddress? remote = Normalize(ctx.Connection.RemoteIpAddress);
            if (!IsLocal(remote))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsync("forbidden");
                return;
            }

            bool upload = path.StartsWithSegments("/api/upload");
            if (!RateLimit.Allow(remote, upload, out int retryAfter))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.Response.Headers.RetryAfter = retryAfter.ToString();
                return;
            }

            ShareOptions current = Options;
            if (current.RequireToken)
            {
                if (path == "/")
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync(TokenHintHtml);
                    return;
                }

                if (!TokenEquals(ProvidedToken(ctx), current.Token))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsync("unauthorized");
                    return;
                }
            }

            await next();
        });

        app.MapGet("/", (HttpContext ctx) => Page(ctx));
        app.MapGet("/s/{token}", (HttpContext ctx, string token) => Page(ctx));
        app.MapGet("/api/state", GetState);
        app.MapGet("/api/raw/item/{id}", GetRawItem);
        app.MapGet("/api/raw/inbox/{name}", GetRawInbox);
        app.MapPost("/api/text", (Delegate)(Func<HttpContext, Task<IResult>>)PostText);
        app.MapPost("/api/upload/init", (Delegate)(Func<HttpContext, Task<IResult>>)PostUploadInit);
        app.MapPost("/api/upload/append", (Delegate)(Func<HttpContext, Task<IResult>>)PostUploadAppend);
        app.MapPost("/api/upload/finish", (Delegate)(Func<HttpContext, Task<IResult>>)PostUploadFinish);
        app.MapGet("/api/upload/pending", GetUploadPending);

        app.StartAsync().GetAwaiter().GetResult();
        _app = app;

        _ = app.WaitForShutdownAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) AppLog.Warn("Share", $"共享服务意外结束：{t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);

        string url = _endpoint?.Url(options.Port) ?? $"http://127.0.0.1:{options.Port}/";
        AppLog.Info("Share",
            $"共享服务已启动：{url}（令牌 {(options.RequireToken ? "开" : "关")}，回传 {(options.AllowInbound ? "开" : "关")}）");
    }

    // ============ 页面 ============

    private static IResult Page(HttpContext ctx)
    {
        if (Options.RequireToken && !ctx.Request.Path.StartsWithSegments("/s"))
            return Results.Content(TokenHintHtml, "text/html; charset=utf-8");

        return Results.Content(ShareAssets.IndexHtml, "text/html; charset=utf-8");
    }

    // ============ 状态 ============

    private static IResult GetState()
    {
        ShareStore store = ShareStore.Default;
        var items = store.Items()
            .Select(i => new
            {
                id = i.Id,
                name = i.Name,
                size = i.Size,
                mime = i.Mime,
                added = i.AddedAt.ToString("o"),
                stale = !i.Exists,
                copied = i.Copied,
                text = TextPreview(i),
            })
            .ToList();

        var inbox = store.Inbox()
            .Select(f => new
            {
                name = f.Name,
                size = f.Size,
                mime = f.Mime,
                modified = f.Modified.ToString("o"),
            })
            .ToList();

        ShareOptions o = Options;
        return WriteJson(new
        {
            ok = true,
            title = Environment.MachineName,
            allowInbound = o.AllowInbound,
            requireToken = o.RequireToken,
            lastDevice = LastDevice,
            lastSeen = LastSeen?.ToString("o"),
            items,
            inbox,
            version = 1,
        });
    }

    private static string? TextPreview(ShareItem item)
    {
        if (item.Size > 64 * 1024) return null;
        if (!item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return null;
        if (!item.Exists) return null;
        try { return File.ReadAllText(item.Path); }
        catch { return null; }
    }

    // ============ 下载 ============

    private static IResult GetRawItem(string id)
    {
        ShareItem? item = ShareStore.Default.Find(id);
        if (item == null || !item.Exists) return Results.NotFound();
        return Results.File(item.Path, item.Mime, item.Name, enableRangeProcessing: true);
    }

    private static IResult GetRawInbox(string name)
    {
        if (!ShareStore.IsPlainName(name)) return Results.NotFound();
        string path = Path.Combine(ShareStore.Default.InboxDir, name);
        if (!File.Exists(path)) return Results.NotFound();
        return Results.File(path, ShareStore.MimeFor(name), name, enableRangeProcessing: true);
    }

    // ============ 接收 ============

    private sealed class TextBody
    {
        public string? Text { get; set; }
    }

    private static async Task<IResult> PostText(HttpContext ctx)
    {
        if (!Options.AllowInbound) return Results.StatusCode(StatusCodes.Status403Forbidden);

        TextBody? body = await ReadJson<TextBody>(ctx);
        string text = body?.Text ?? "";
        if (text.Length == 0) return BadRequest("empty");

        bool ok = Native.StaRunner.Run(() => Native.NativeClipboard.SetText(text), 3000) == true;
        if (!ok) return Results.StatusCode(StatusCodes.Status500InternalServerError);

        try { ShareStore.Default.AddInboxText(text, $"文字 {ShareStore.Stamp()}.txt"); }
        catch (Exception ex) { AppLog.Warn("Share", $"文字落盘失败：{ex.Message}"); }

        RememberDevice(ctx.Request.Headers["X-Share-Device"].ToString());
        Ui.Post(RaiseChanged);
        return WriteJson(new { ok = true });
    }

    private sealed class InitBody
    {
        public string? Name { get; set; }
        public long Size { get; set; }
        public string? Sha256 { get; set; }
        public string? UploadId { get; set; }
    }

    /// <summary>未完成上传的元数据（<c>.partial/&lt;id&gt;.json</c>），页面据此显示「没传完」。</summary>
    private sealed class PartialMeta
    {
        public string? Name { get; set; }
        public long Size { get; set; }
    }

    private static async Task<IResult> PostUploadInit(HttpContext ctx)
    {
        ShareOptions o = Options;
        if (!o.AllowInbound) return Results.StatusCode(StatusCodes.Status403Forbidden);

        InitBody? body = await ReadJson<InitBody>(ctx);
        if (body == null || string.IsNullOrWhiteSpace(body.Name)) return BadRequest("name");

        long max = (long)o.MaxUploadMb * 1024 * 1024;
        if (body.Size <= 0 || body.Size > max) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        ShareStore store = ShareStore.Default;
        store.EnsureLayout();

        string id = ShareStore.SafeName(body.UploadId);
        if (id.Length == 0) id = ShareOptions.NewToken();

        string part = store.PartialPath(id);
        long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (offset > body.Size)
        {
            TryDelete(part);
            offset = 0;
        }

        try
        {
            File.WriteAllText(Path.Combine(store.PartialDir, id + ".json"),
                JsonSerializer.Serialize(new PartialMeta { Name = body.Name, Size = body.Size }, JsonOut));
        }
        catch { /* 元数据只服务「没传完」提示，写不进不影响上传 */ }

        return WriteJson(new { ok = true, uploadId = id, offset });
    }

    private static async Task<IResult> PostUploadAppend(HttpContext ctx)
    {
        if (!Options.AllowInbound) return Results.StatusCode(StatusCodes.Status403Forbidden);

        string id = ctx.Request.Query["uploadId"].ToString();
        if (!long.TryParse(ctx.Request.Query["offset"].ToString(), out long offset) || offset < 0)
            return BadRequest("offset");

        ShareStore store = ShareStore.Default;
        store.EnsureLayout();
        string part = store.PartialPath(id);
        long current = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (current != offset) return Results.StatusCode(StatusCodes.Status409Conflict);

        try
        {
            await using var fs = new FileStream(part, FileMode.Append, FileAccess.Write, FileShare.None);
            await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Share", $"分片写入失败：{ex.Message}");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        return WriteJson(new { ok = true, offset = new FileInfo(part).Length });
    }

    private static async Task<IResult> PostUploadFinish(HttpContext ctx)
    {
        if (!Options.AllowInbound) return Results.StatusCode(StatusCodes.Status403Forbidden);

        string id = ctx.Request.Query["uploadId"].ToString();
        string name = ctx.Request.Query["name"].ToString();
        string sha = ctx.Request.Query["sha256"].ToString();
        long size = long.TryParse(ctx.Request.Query["size"].ToString(), out long s) ? s : -1;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || size < 0)
            return BadRequest("params");

        ShareStore store = ShareStore.Default;
        string part = store.PartialPath(id);
        if (!File.Exists(part)) return Results.NotFound();

        var info = new FileInfo(part);
        if (info.Length != size)
        {
            TryDelete(part);
            return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
        }

        if (!string.IsNullOrWhiteSpace(sha) && !Sha256Matches(part, sha))
        {
            TryDelete(part);
            return Results.StatusCode(StatusCodes.Status422UnprocessableEntity);
        }

        store.EnsureLayout();
        string dest = store.UniqueInboxPath(name);
        try
        {
            File.Move(part, dest);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Share", $"收件落盘失败：{ex.Message}");
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }

        RememberDevice(ctx.Request.Headers["X-Share-Device"].ToString());
        TryDelete(Path.Combine(store.PartialDir, id + ".json"));
        Ui.Post(RaiseChanged);
        return WriteJson(new { ok = true, name = Path.GetFileName(dest) });
    }

    private static IResult GetUploadPending()
    {
        ShareStore store = ShareStore.Default;
        store.EnsureLayout();

        var list = Directory.EnumerateFiles(store.PartialDir, "*.part")
            .Select(p => new FileInfo(p))
            .Select(f =>
            {
                string id = Path.GetFileNameWithoutExtension(f.Name);
                PartialMeta? meta = TryReadMeta(store, id);
                return new
                {
                    uploadId = id,
                    name = meta?.Name,
                    size = meta?.Size ?? 0,
                    received = f.Length,
                    modified = f.LastWriteTime.ToString("o"),
                };
            })
            .ToList();

        return WriteJson(new { ok = true, pending = list });
    }

    private static PartialMeta? TryReadMeta(ShareStore store, string id)
    {
        try
        {
            string path = Path.Combine(store.PartialDir, id + ".json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<PartialMeta>(File.ReadAllText(path), JsonIn)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool Sha256Matches(string path, string expected)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ============ 工具 ============

    private static async Task<T?> ReadJson<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(ctx.Request.Body, JsonIn, ctx.RequestAborted);
        }
        catch
        {
            return null;
        }
    }

    private static IResult WriteJson(object value) =>
        Results.Text(JsonSerializer.Serialize(value, JsonOut), "application/json; charset=utf-8");

    private static IResult BadRequest(string error) =>
        WriteJson(new { ok = false, error });

    private static IPAddress? Normalize(IPAddress? ip)
    {
        if (ip == null) return null;
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    private static bool IsLocal(IPAddress? remote)
    {
        if (remote == null) return false;
        if (IPAddress.IsLoopback(remote)) return true;

        // 所有网卡都算「同网段」（手机连 Wi-Fi、电脑插网线时两块网卡各自成网）。
        lock (Gate)
        {
            foreach (LanEndpoint endpoint in _endpoints)
                if (endpoint.Contains(remote)) return true;
        }

        return false;
    }

    private static string ProvidedToken(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? "";
        if (path.StartsWith("/s/", StringComparison.Ordinal))
            return Uri.UnescapeDataString(path[3..].Trim('/'));

        string auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();

        string header = ctx.Request.Headers["X-Share-Token"].ToString();
        if (header.Length > 0) return header.Trim();

        return ctx.Request.Query["k"].ToString();
    }

    /// <summary>定长比较（同 <see cref="Ai.AiSettings.TokenEquals"/>）。</summary>
    private static bool TokenEquals(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        if (provided.Length != expected.Length) return false;

        int diff = 0;
        for (int i = 0; i < provided.Length; i++) diff |= provided[i] ^ expected[i];
        return diff == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 下次启动清理 */ }
    }

    private const string TokenHintHtml =
        "<!doctype html><meta charset=\"utf-8\">" +
        "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
        "<body style=\"font:14px/1.6 system-ui;padding:32px;color:#18181B;background:#FAFAFA\">" +
        "<h3 style=\"font-size:16px\">此共享需要访问令牌</h3>" +
        "<p style=\"color:#71717A\">请使用带令牌的完整地址，或直接扫电脑上显示的二维码。</p>" +
        "</body>";

    // ============ 限流 ============

    private static class RateLimit
    {
        private static readonly ConcurrentDictionary<string, Counter> Map = new();

        private sealed class Counter
        {
            public long Minute;
            public int Requests;
            public int Uploads;
        }

        public static bool Allow(IPAddress? ip, bool upload, out int retryAfterSeconds)
        {
            retryAfterSeconds = 30;
            string key = ip?.ToString() ?? "unknown";
            long minute = Environment.TickCount64 / 60_000;

            Counter counter = Map.GetOrAdd(key, _ => new Counter { Minute = minute });
            lock (counter)
            {
                if (counter.Minute != minute)
                {
                    counter.Minute = minute;
                    counter.Requests = 0;
                    counter.Uploads = 0;
                }

                if (++counter.Requests > PerMinute) return false;
                if (upload && ++counter.Uploads > UploadsPerMinute) return false;
                return true;
            }
        }
    }
}
