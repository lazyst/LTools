using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LTools.Core.Share;
using ShareSmoke;

// 局域网共享冒烟：真起 Kestrel + 真 HttpClient。
// 断言的是「页面能拿到什么、下载字节是否一致、分片能不能续、令牌拦不拦得住」。

// --log：把输出写进文件（用 explorer.exe 以普通权限跑时看不到控制台）。
if (args.Contains("--log"))
{
    string logDir = Path.Combine(Path.GetTempPath(), "opencode", "share-smoke");
    Directory.CreateDirectory(logDir);
    Console.SetOut(new StreamWriter(Path.Combine(logDir, "report.txt"), append: false, Encoding.UTF8)
    {
        AutoFlush = true,
    });
}

int failed = 0;
int passed = 0;

void Check(bool condition, string message)
{
    if (condition) { passed++; Console.WriteLine("  ✓ " + message); }
    else { failed++; Console.WriteLine("  ✗ " + message); }
}

string repo = FindRepoRoot();
string bin = Path.Combine(repo, "bin", "Debug", "net10.0-windows10.0.19041.0");

// Kestrel / ModelContextProtocol 的依赖不在冒烟输出目录：缺什么就从产品的 bin 里补。
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    string candidate = Path.Combine(bin, name.Name + ".dll");
    return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

Assembly asm = typeof(ShareOptions).Assembly;
Type storeType = asm.GetType("LTools.Core.Share.ShareStore", throwOnError: true)!;
Type serverType = asm.GetType("LTools.Core.Share.ShareServer", throwOnError: true)!;

string tempRoot = Path.Combine(Path.GetTempPath(), "ltools-share-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempRoot);

object store = Activator.CreateInstance(storeType, tempRoot)!;
storeType.GetField("_default", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, store);

MethodInfo apply = serverType.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;
MethodInfo stop = serverType.GetMethod("Stop", BindingFlags.Public | BindingFlags.Static)!;
MethodInfo addPath = storeType.GetMethod("AddPath")!;

int port = 18790 + Random.Shared.Next(60);
var options = new ShareOptions
{
    Port = port,
    Running = true,
    Inbound = ShareInbound.allow,
    MaxUploadMb = 64,
};

using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(20) };

try
{
    Console.WriteLine("== 启动 ==");
    apply.Invoke(null, new object[] { options });
    Check(await WaitReady(http), $"服务在 127.0.0.1:{port} 起来");

    Console.WriteLine("== 只读浏览 ==");
    using (var state = await GetJson(http, "/api/state"))
    {
        Check(state.RootElement.GetProperty("ok").GetBoolean(), "/api/state 回 ok");
        Check(state.RootElement.GetProperty("items").GetArrayLength() == 0, "初始清单为空");
        Check(state.RootElement.GetProperty("inbound").GetString() == "allow", "状态里报「自动接收」");
    }

    string page = await http.GetStringAsync("/");
    Check(page.Contains("LTools", StringComparison.Ordinal), "/ 返回页面");
    Check(page.Contains("待发送", StringComparison.Ordinal) && page.Contains("id=\"pending\"", StringComparison.Ordinal),
        "页面是「先攒后发」那一版（有「待发送」列表，选文件不立刻上传）");
    Check(page.Contains("data.inbound", StringComparison.Ordinal) && !page.Contains("allowInbound", StringComparison.Ordinal),
        "页面读接收三档 inbound（旧的 allowInbound 已经不在）");

    string source = Path.Combine(tempRoot, "src.txt");
    File.WriteAllText(source, "hello-share", new UTF8Encoding(false));
    object item = addPath.Invoke(store, new object[] { source, false })!;
    string id = (string)item.GetType().GetProperty("Id")!.GetValue(item)!;

    using (var state = await GetJson(http, "/api/state"))
    {
        Check(state.RootElement.GetProperty("items").GetArrayLength() == 1, "清单里有 1 项");
        var first = state.RootElement.GetProperty("items")[0];
        Check(first.GetProperty("id").GetString() == id, "id 对得上");
        Check(first.GetProperty("text").GetString() == "hello-share", "小文本内容随状态返回");
        Check(!first.GetProperty("stale").GetBoolean(), "原文件在，不标失效");
    }

    using (var raw = await http.GetAsync($"/api/raw/item/{id}"))
    {
        Check(raw.StatusCode == HttpStatusCode.OK, "下载 200");
        Check(await raw.Content.ReadAsStringAsync() == "hello-share", "下载字节一致");
        Check(raw.Headers.AcceptRanges.Contains("bytes"), "声明 Accept-Ranges（断点下载）");
    }

    Console.WriteLine("== 路径与错误 ==");
    Check((await http.GetAsync("/api/raw/inbox/..%2Fsecret.txt")).StatusCode == HttpStatusCode.NotFound,
        "inbox 里的 ../ 被拒");
    Check((await http.GetAsync("/api/raw/item/nope")).StatusCode == HttpStatusCode.NotFound,
        "不存在的 id 404");
    Check((await http.GetAsync("/nowhere")).StatusCode == HttpStatusCode.NotFound,
        "未知路径 404");

    Console.WriteLine("== 分片上传（含断点续传）==");
    byte[] payload = RandomNumberGenerator.GetBytes(40_000);
    string sha = Convert.ToHexString(SHA256.HashData(payload));

    using (var init = await PostJsonDoc(http, "/api/upload/init",
               new { name = "上传.bin", size = payload.Length, uploadId = "smoke-upload" }))
    {
        Check(init.RootElement.GetProperty("ok").GetBoolean(), "init ok");
        Check(init.RootElement.GetProperty("offset").GetInt64() == 0, "首次 offset = 0");
    }

    int half = payload.Length / 2;
    Check(await Append(http, "smoke-upload", 0, payload[..half]), "第一段写入");
    Check(await Append(http, "smoke-upload", half, payload[half..]), "第二段从断点追加");

    using (var pending = await GetJson(http, "/api/upload/pending"))
    {
        Check(pending.RootElement.GetProperty("pending").GetArrayLength() == 0
              || pending.RootElement.GetProperty("pending")[0].GetProperty("received").GetInt64() == payload.Length,
            "pending 报告已收全");
    }

    using (var finish = await PostJsonDoc(http,
               $"/api/upload/finish?uploadId=smoke-upload&name={Uri.EscapeDataString("上传.bin")}&size={payload.Length}&sha256={sha}",
               new { }))
    {
        Check(finish.RootElement.GetProperty("ok").GetBoolean(), "finish ok");
        Check(finish.RootElement.GetProperty("name").GetString() == "上传.bin", "收件名对得上");
    }

    string inboxPath = Path.Combine(tempRoot, "inbox", "上传.bin");
    Check(File.Exists(inboxPath), "文件落进 inbox/");
    Check(File.ReadAllBytes(inboxPath).SequenceEqual(payload), "收件字节一致");

    Check((await PostJson(http,
              $"/api/upload/finish?uploadId=smoke-upload&name=x.bin&size={payload.Length}",
              new { })).StatusCode == HttpStatusCode.NotFound, "重复 finish 找不到临时文件");

    Console.WriteLine("== 手机发来的文字 ==");
    MethodInfo itemsMethod = storeType.GetMethod("Items")!;
    int OutCount() => ((System.Collections.IEnumerable)itemsMethod.Invoke(store, null)!).Cast<object>().Count();

    int outBeforeText = OutCount();
    using (var text = await PostJson(http, "/api/text", new { text = "手机发来的" }))
        Check(text.IsSuccessStatusCode, "/api/text 收下文字");

    Check(OutCount() == outBeforeText, "对方发来的文字不进「发出去的」清单");

    string inboxText = Directory.EnumerateFiles(Path.Combine(tempRoot, "inbox"), "文字 *.txt")
        .FirstOrDefault() ?? "";
    Check(inboxText.Length > 0, "文字落进 inbox/文字 <时间>.txt");
    Check(inboxText.Length > 0 && File.ReadAllText(inboxText) == "手机发来的", "落盘内容对得上");

    Console.WriteLine("== 收到的目录 ==");
    string customInbox = Path.Combine(tempRoot, "custom-inbox");
    apply.Invoke(null, new object[]
    {
        new ShareOptions
        {
            Port = port, Running = true, Inbound = ShareInbound.allow,
            InboxDir = customInbox, MaxUploadMb = 64,
        }
    });
    Check(await WaitReady(http), "换收到的目录后服务重启");

    using (var changed = await PostJson(http, "/api/text", new { text = "进自定义目录" }))
        Check(changed.IsSuccessStatusCode, "文字收下");

    string customFile = Directory.Exists(customInbox)
        ? Directory.EnumerateFiles(customInbox, "文字 *.txt")
            .FirstOrDefault(f => File.ReadAllText(f) == "进自定义目录") ?? ""
        : "";
    Check(customFile.Length > 0, "文字落在配置的目录里：" + customInbox);
    Check(!Directory.EnumerateFiles(Path.Combine(tempRoot, "inbox"), "*.txt")
            .Any(f => File.ReadAllText(f) == "进自定义目录"),
        "默认 inbox 里没有它（按内容比：名字带分钟，可能撞名）");

    using (var state = await GetJson(http, "/api/state"))
        Check(state.RootElement.GetProperty("inbox").EnumerateArray()
                .Any(e => e.GetProperty("name").GetString() == Path.GetFileName(customFile)),
            "状态里列的是自定义目录的内容");

    using (var raw = await http.GetAsync("/api/raw/inbox/" + Uri.EscapeDataString(Path.GetFileName(customFile))))
        Check(raw.IsSuccessStatusCode && await raw.Content.ReadAsStringAsync() == "进自定义目录",
            "自定义目录里的文件能下载");

    // 回到默认：后面的窗口段按默认 inbox 断言。
    apply.Invoke(null, new object[]
    {
        new ShareOptions { Port = port, Running = true, Inbound = ShareInbound.allow, MaxUploadMb = 64 }
    });
    Check(await WaitReady(http), "恢复默认目录后服务重启");

    Console.WriteLine("== 需要确认 ==");
    Type promptType = asm.GetType("LTools.Views.ShareInboundPrompt", throwOnError: true)!;
    PropertyInfo autoAnswer = promptType.GetProperty("AutoAnswer", BindingFlags.Public | BindingFlags.Static)!;

    apply.Invoke(null, new object[]
    {
        new ShareOptions { Port = port, Running = true, Inbound = ShareInbound.confirm, MaxUploadMb = 64 }
    });
    Check(await WaitReady(http), "切成「需要确认」后服务重启");

    using (var state = await GetJson(http, "/api/state"))
        Check(state.RootElement.GetProperty("inbound").GetString() == "confirm", "状态里报「需要确认」");

    // 本机先点「取消」
    autoAnswer.SetValue(null, new Func<string, string, bool>((_, _) => false));
    using (var deniedText = await PostJson(http, "/api/text", new { text = "不该收下的文字" }))
    {
        Check(deniedText.StatusCode == HttpStatusCode.Forbidden, "拒绝时文字 403");
        Check((await deniedText.Content.ReadAsStringAsync()).Contains("declined", StringComparison.Ordinal),
            "回执写明 declined（页面据此说「电脑拒绝了这次发送」）");
    }
    Check(!Directory.EnumerateFiles(Path.Combine(tempRoot, "inbox"), "文字 *.txt")
            .Any(f => File.ReadAllText(f) == "不该收下的文字"),
        "拒绝的文字没落盘");

    // 本机点「允许接收」
    autoAnswer.SetValue(null, new Func<string, string, bool>((_, what) => what.Contains("确认.bin")));
    const string confirmId = "confirm-upload";
    using (var init = await PostJsonDoc(http, "/api/upload/init", new { name = "确认.bin", size = 8, uploadId = confirmId }))
        Check(init.RootElement.GetProperty("ok").GetBoolean(), "允许之后才 init");
    Check(!File.Exists(Path.Combine(tempRoot, "inbox", ".partial", confirmId + ".part")),
        "允许之后也还没写字节（等 append）");

    // 已经允许过的同一个 uploadId 不再问：把回答改成「拒绝」也照样放行
    autoAnswer.SetValue(null, new Func<string, string, bool>((_, _) => false));
    using (var again = await PostJson(http, "/api/upload/init", new { name = "确认.bin", size = 8, uploadId = confirmId }))
        Check(again.StatusCode == HttpStatusCode.OK, "同一个 uploadId 不重复问");

    // 没经过 init 的上传（没被允许过）不许追加
    using (var rogue = await PostJson(http, "/api/upload/append?uploadId=rogue&offset=0", new { }))
    {
        Check(rogue.StatusCode == HttpStatusCode.Forbidden, "没确认过的上传不许追加");
        Check((await rogue.Content.ReadAsStringAsync()).Contains("unapproved", StringComparison.Ordinal),
            "回执写明 unapproved");
    }

    Check(await Append(http, confirmId, 0, new byte[8]), "允许后能追加分片");
    using (var finish = await PostJson(http,
               $"/api/upload/finish?uploadId={confirmId}&name={Uri.EscapeDataString("确认.bin")}&size=8", new { }))
        Check(finish.StatusCode == HttpStatusCode.OK, "允许后能收尾");
    Check(File.Exists(Path.Combine(tempRoot, "inbox", "确认.bin")), "确认过的文件落进 inbox/");
    autoAnswer.SetValue(null, null);

    Console.WriteLine("== 不接受 ==");
    apply.Invoke(null, new object[]
    {
        new ShareOptions { Port = port, Running = true, Inbound = ShareInbound.block, MaxUploadMb = 64 }
    });
    Check(await WaitReady(http), "改配置后服务重启");
    using (var denied = await PostJson(http, "/api/upload/init", new { name = "x.bin", size = 10, uploadId = "x" }))
    {
        Check(denied.StatusCode == HttpStatusCode.Forbidden, "不接受时上传 403");
        Check((await denied.Content.ReadAsStringAsync()).Contains("paused", StringComparison.Ordinal),
            "回执写明 paused");
    }
    using (var deniedText = await PostJson(http, "/api/text", new { text = "暂停时的文字" }))
        Check(deniedText.StatusCode == HttpStatusCode.Forbidden, "不接受时文字 403");
    using (var state = await GetJson(http, "/api/state"))
        Check(state.RootElement.GetProperty("inbound").GetString() == "block", "状态里报「不接受」");

    Console.WriteLine("== 令牌 ==");
    string token = ShareOptions.NewToken();
    apply.Invoke(null, new object[]
    {
        new ShareOptions { Port = port, Running = true, Inbound = ShareInbound.allow, MaxUploadMb = 64, RequireToken = true, Token = token }
    });
    Check(await WaitReady(http), "开令牌后服务重启");

    Check((await http.GetAsync("/api/state")).StatusCode == HttpStatusCode.Unauthorized, "无令牌 401");
    Check((await http.GetAsync("/api/state?k=wrong")).StatusCode == HttpStatusCode.Unauthorized, "错令牌 401");

    string hint = await http.GetStringAsync("/");
    Check(hint.Contains("访问令牌", StringComparison.Ordinal), "根路径只回提示页");

    using (var ok = await http.GetAsync("/api/state?k=" + token))
        Check(ok.StatusCode == HttpStatusCode.OK, "带令牌 200");

    using (var pageWithToken = await http.GetAsync("/s/" + token))
    {
        Check(pageWithToken.StatusCode == HttpStatusCode.OK, "/s/<令牌> 返回页面");
        Check((await pageWithToken.Content.ReadAsStringAsync()).Contains("LTools", StringComparison.Ordinal),
            "令牌页是真正的共享页");
    }

    Console.WriteLine("== 共享窗口 ==");
    UiSmoke.Run(Check, tempRoot, args.Contains("--explorer"));

    if (args.Contains("--uihost"))
    {
        Console.WriteLine("== 界面宿主（普通权限进程）==");
        UiHostSmoke.Run(Check, repo);
    }
}
catch (Exception ex)
{
    failed++;
    Console.WriteLine("  ✗ 冒烟异常：" + ex);
}
finally
{
    try { stop.Invoke(null, null); } catch { /* 关停失败不影响结论 */ }
    Thread.Sleep(300);
    try { Directory.Delete(tempRoot, recursive: true); } catch { /* 临时目录 */ }
}

Console.WriteLine();
Console.WriteLine($"通过 {passed} / 失败 {failed}");
return failed == 0 ? 0 : 1;

// ============ 工具 ============

static string FindRepoRoot()
{
    string? dir = AppContext.BaseDirectory;
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir, "LTools.csproj"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("找不到 LTools.csproj（先 dotnet build）");
}

static async Task<bool> WaitReady(HttpClient http)
{
    for (int i = 0; i < 60; i++)
    {
        try
        {
            using var r = await http.GetAsync("/api/state");
            if (r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized) return true;
        }
        catch { /* 还没起来 */ }
        await Task.Delay(100);
    }
    return false;
}

static async Task<JsonDocument> GetJson(HttpClient http, string path)
{
    using var r = await http.GetAsync(path);
    r.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}

static async Task<HttpResponseMessage> PostJson(HttpClient http, string path, object body)
{
    var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    return await http.PostAsync(path, content);
}

static async Task<JsonDocument> PostJsonDoc(HttpClient http, string path, object body)
{
    using var r = await PostJson(http, path, body);
    r.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
}

static async Task<bool> Append(HttpClient http, string uploadId, long offset, byte[] data)
{
    using var content = new ByteArrayContent(data);
    using var r = await http.PostAsync($"/api/upload/append?uploadId={uploadId}&offset={offset}", content);
    if (!r.IsSuccessStatusCode) return false;
    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
    return doc.RootElement.GetProperty("offset").GetInt64() == offset + data.Length;
}
