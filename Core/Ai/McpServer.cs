using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace LTools.Core.Ai;

/// <summary>
/// 本机 MCP 服务（HTTP，仅监听 127.0.0.1）。
/// </summary>
/// <remarks>
/// <para>
/// 用官方 <c>ModelContextProtocol.AspNetCore</c> 起一个极简 WebApplication，工具面见 <see cref="AiTools"/>。
/// 服务默认关闭；只有用户在设置 → AI 里打开才会启动。端口 / 令牌 / 审批模式都在 <see cref="AiSettings"/>。
/// </para>
/// <para>
/// 安全边界：
/// <list type="bullet">
/// <item>只绑定回环地址，拒绝非 loopback 的远端地址；</item>
/// <item>每个请求校验 Bearer 令牌（定长时间比较），令牌不写日志；</item>
/// <item>请求体上限 16 MB（容纳 base64 图片）；</item>
/// <item>写工具另有 <c>AllowWrites</c> + 审批模式两道闸（见 <see cref="AiToolHandlers"/>）。</item>
/// </list>
/// </para>
/// <para>
/// 线程：HTTP 回调线程与 UI 线程是两条线；任何动作库 / WPF 访问都走 <see cref="AiUi"/>，
/// 且绝不在 UI 线程上等 HTTP 结果。
/// </para>
/// </remarks>
internal static class McpServer
{
    private const string Route = "/mcp";
    private const long MaxBodyBytes = 16 * 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>串行化 <see cref="Apply"/>（相同时刻只允许一个启动 / 重启在跑）。</summary>
    private static readonly object ApplyGate = new();

    private static WebApplication? _app;
    private static int _port;

    /// <summary>正在进行（或已完成）的后台关停任务；新实例启动前要等它释放端口。</summary>
    private static Task _stopTask = Task.CompletedTask;

    /// <summary>服务是否在跑（设置页状态灯 / health 用）。</summary>
    public static bool IsRunning => _app != null;

    /// <summary>正在监听的端口（未运行为 0）。</summary>
    public static int RunningPort => _port;

    /// <summary>最近一次启动 / 运行错误（不含令牌）。</summary>
    public static string? LastError { get; private set; }

    /// <summary>按当前配置启动 / 停止 / 重启。设置页保存后与启动时各调一次（都由后台线程调用）。</summary>
    public static void Apply()
    {
        lock (ApplyGate)
        {
            Task pending;
            lock (Gate)
            {
                StopCore();
                pending = _stopTask;
            }

            // 旧实例要先真正释放端口，新实例才能绑同一个端口。Apply 只在后台线程跑，这里可以等；
            // 有界等待兜底：真卡住也不至于让「改端口」永远没反应（表现为启动失败并写 LastError）。
            try { pending.Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { AppLog.Warn("Ai", $"等待上一次 MCP 服务关停时出错：{ex.Message}"); }

            lock (Gate)
            {
                var settings = AiSettings.Load();
                LastError = null;
                if (!settings.Enabled) return;

                try
                {
                    StartCore(settings);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    AppLog.Error("Ai", $"MCP 服务启动失败（端口 {settings.Port}）：{ex.Message}", ex);
                    try
                    {
                        settings.LastError = ex.Message;
                        settings.Save();
                    }
                    catch { /* 写配置失败不再套一层 */ }
                }
            }
        }
    }

    /// <summary>
    /// 停止服务（退出 / 关闭开关 / 改端口）。**绝不在调用线程上等关停**，所以 UI 线程可以直接调。
    /// </summary>
    public static void Stop()
    {
        lock (Gate) StopCore();
    }

    /// <summary>
    /// 摘掉当前实例并把关停排到后台（调用方需持有 <see cref="Gate"/>）。
    /// </summary>
    /// <remarks>
    /// **不能在这里同步等待**：<c>StopAsync</c> / <c>DisposeAsync</c> 内部有 await，在 UI 线程上
    /// <c>GetAwaiter().GetResult()</c> 会让续体排在 Dispatcher 队列里等一个正被阻塞的线程 ——
    /// 开着 MCP 服务从托盘退出会直接卡死（托盘菜单再也点不动）。见 AGENTS.md「退出卡死」。
    /// </remarks>
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
            catch (Exception ex) { AppLog.Warn("Ai", $"停止 MCP 服务时出错：{ex.Message}"); }
            try { await app.DisposeAsync().ConfigureAwait(false); }
            catch { /* 已释放 */ }

            AppLog.Info("Ai", $"MCP 服务已停止（{sw.ElapsedMilliseconds} ms）");
        });
    }

    private static void StartCore(AiSettings settings)
    {
        string url = $"http://127.0.0.1:{settings.Port}";
        string token = settings.Token;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // 用真实程序集名：写 "LTools.Mcp" 会让 ASP.NET 去找同名程序集加载 hosting startup，
            // 每次都往 mcp-debug.log 里记一条假的 Critical（本程序没有这个程序集）。
            ApplicationName = typeof(McpServer).Assembly.GetName().Name ?? "LTools",
            EnvironmentName = Environments.Production,
            Args = Array.Empty<string>(),
        });

        // 控制台日志在 WinExe 里没有意义，还会把 MCP 的 stdout 弄脏（stdio 桥以后要用）。
        // 只把 Warning 以上（MCP SDK 内部错误）写到 ai/mcp-debug.log，正常请求不落盘。
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddProvider(new FileLoggerProvider(
            Path.Combine(AppPaths.DataRoot, "ai", "mcp-debug.log")));
        builder.WebHost.UseUrls(url);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "ltools",
                    Title = "LTools",
                    Version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                };
                options.ServerInstructions =
                    "先读 ltools_docs_get(\"format\")，再用 ltools_units_list / ltools_units_get 确认动作单元键。" +
                    "只能改暂存区草稿（ltools_staging_*）；正式动作只读，采纳要先得到用户确认。";
            })
            .WithHttpTransport(options => options.Stateless = true)
            .WithToolsFromAssembly(typeof(AiTools).Assembly);

        var app = builder.Build();

        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments(Route))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (ctx.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            string host = ctx.Request.Host.Host;
            if (!(host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                  || host.Equals("::1", StringComparison.Ordinal)))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (ctx.Request.ContentLength is > MaxBodyBytes)
            {
                ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (!AiSettings.TokenEquals(ProvidedToken(ctx), token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }

            await next();
        });

        app.MapMcp(Route);

        // StartAsync 会在端口被占用等情况下同步抛错，便于设置页直接显示原因。
        app.StartAsync().GetAwaiter().GetResult();
        _app = app;
        _port = settings.Port;
        LastError = null;

        // 上次启动失败的原因已经过时，清掉，免得设置页在服务停掉后还显示旧的错误。
        if (!string.IsNullOrWhiteSpace(settings.LastError))
        {
            try
            {
                settings.LastError = null;
                settings.Save();
            }
            catch { /* 写配置失败不影响已启动的服务 */ }
        }

        // 后台等关闭信号；异常（如绑定被系统回收）只记录，不影响主程序。
        _ = app.WaitForShutdownAsync().ContinueWith(t =>
        {
            if (t.IsFaulted) AppLog.Warn("Ai", $"MCP 服务意外结束：{t.Exception?.GetBaseException().Message}");
        }, TaskScheduler.Default);

        AppLog.Info("Ai",
            $"MCP 服务已启动：{url}{Route}（审批 {settings.ApprovalMode}，写入 {(settings.WritesAllowed ? "开" : "关")}）");
    }

    private static string ProvidedToken(HttpContext ctx)
    {
        string auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return auth[7..].Trim();

        string header = ctx.Request.Headers["X-LTools-Token"].ToString();
        if (header.Length > 0) return header.Trim();

        return ctx.Request.Query["token"].ToString();
    }

    /// <summary>MCP 排错用：SDK / ASP.NET 的日志写到 <c>ai/mcp-debug.log</c>（WinExe 没有控制台）。</summary>
    private sealed class FileLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        private readonly string _path;

        public FileLoggerProvider(string path) => _path = path;

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) =>
            new FileLogger(_path, categoryName);

        public void Dispose() { }

        private sealed class FileLogger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly string _path;
            private readonly string _category;

            public FileLogger(string path, string category)
            {
                _path = path;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
                logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    if (File.Exists(_path) && new FileInfo(_path).Length > 1024 * 1024)
                    {
                        string old = _path + ".1";
                        try
                        {
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(_path, old);
                        }
                        catch { /* 轮转失败就继续追加 */ }
                    }

                    string line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
                    if (exception != null) line += Environment.NewLine + exception;
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch { /* 排错日志失败不影响服务 */ }
            }
        }
    }
}
