using System.Net.Http;
using System.Security.Cryptography;
using LTools.Core.Actions;
using LTools.Features.Units.Files;

namespace LTools.Features.Units.Web;

/// <summary>
/// 下载文件（<c>web.download</c>）：流式写盘 + 顺手算 MD5，与 <c>web.request</c> 共用网络栈。
/// </summary>
/// <remarks>
/// <para>
/// <c>web.request</c> 是「取网上的数据」的唯一出口，它自己也能存文件，但给的是「响应文本 / JSON /
/// 状态码」这一套；下载要的是「文件名 / 大小 / 校验值 / 重名处理」，所以另开一个动作单元，
/// **不给 <c>web.request</c> 加 <c>operation</c>**。网络栈（<c>HttpClient</c> / 发请求 /
/// 请求头解析）全部复用 <see cref="WebRequestUnit"/> 暴露的 <c>Send</c> / <c>ApplyHeaders</c>，
/// 不另建连接池。
/// </para>
/// <para>
/// 响应体按 80 KB 一块边读边写、边喂 <see cref="IncrementalHash"/>，大文件不会整个进内存；
/// 每块走一次 <see cref="StepContext.EnterIteration"/>，按停止热键能立刻中断。先写
/// <c>.ltools-part</c> 临时文件，成了才 <c>File.Move</c> 成正式名字——**绝不留半个看起来下完了的文件**。
/// </para>
/// <para>
/// 失败按性质标：404 → <see cref="ActionErrors.NotFound"/>、其它非 2xx →
/// <see cref="ActionErrors.Precondition"/>、超时 → <see cref="ActionErrors.Timeout"/>、
/// 停止 → <c>ActionAbortedException</c>。键鼠与输入都碰不到，干跑（<c>dryRun</c>）直接跳过。
/// </para>
/// </remarks>
public sealed class WebDownloadUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "web.download",
        Name = "下载文件",
        Category = "网络",
        Icon = "globe",
        Description = "把网址下载成文件（流式写盘，不占内存）；顺手算 MD5、回传 ETag，重名可自动改名",
        Kind = UnitKind.action,
        Inputs = new[]
        {
            new InputSpec("url", "地址", ValueKind.text, InputEditor.text, Required: true,
                Description: "完整 http(s)://… 地址；可写 ${变量}"),
            new InputSpec("savePath", "存到目录", ValueKind.text, InputEditor.folder, Required: true,
                Description: "下载到哪个文件夹；相对路径按数据根解析"),
            new InputSpec("saveName", "文件名", ValueKind.text,
                Description: "留空 = 按响应头 Content-Disposition 或网址最后一段推断；只取文件名，不会逃出目录"),
            new InputSpec("headers", "请求头", ValueKind.text, InputEditor.multiline,
                Description: "每行一条「名字: 值」，与「HTTP 请求」同一套写法"),
            new InputSpec("cookie", "Cookie", ValueKind.text,
                Description: "整条 Cookie 值；留空不设置"),
            new InputSpec("autoRename", "重名自动改名", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(true),
                Description: "目标已存在时追加 (1) / (2)；关掉 = 直接覆盖"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(60_000), Advanced: true,
                Description: "1000–600000；大文件调大一些；按停止热键可以更早中断"),
        },
        Outputs = new[]
        {
            new OutputSpec("file", "文件", ValueKind.text, "downloadedFile",
                Description: "实际保存的完整路径"),
            new OutputSpec("size", "字节数", ValueKind.number, "downloadSize",
                Description: "下载到的字节数"),
            new OutputSpec("md5", "MD5", ValueKind.text, "downloadMd5",
                Description: "文件内容的 MD5（小写十六进制），便于校验完整性"),
            new OutputSpec("etag", "ETag", ValueKind.text,
                Description: "响应头里的 ETag；服务器没给就为空"),
            new OutputSpec("found", "下载成功", ValueKind.@bool, "downloadFound",
                Description: "2xx 且写盘完成为真；失败直接抛错，不会留半个文件"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string url = args.Text("url").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw ActionErrors.Precondition($"地址要写成完整的 http(s)://…（当前：{url}）");

        string folder = args.Text("savePath").Trim();
        if (folder.Length == 0) throw ActionErrors.Precondition("「存到目录」不能为空");

        int timeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 1000, 600_000);
        bool autoRename = args.Bool("autoRename");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        WebRequestUnit.ApplyHeaders(request, args.Text("headers"));
        string cookie = args.Text("cookie").Trim();
        if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        cts.CancelAfter(timeoutMs);

        using HttpResponseMessage response = WebRequestUnit.Send(request, cts, ctx, timeoutMs, url);
        int status = (int)response.StatusCode;
        if (status == 404)
            throw ActionErrors.NotFound($"下载地址不存在（HTTP 404）：{url}");
        if (status is < 200 or > 299)
            throw ActionErrors.Precondition($"下载失败（HTTP {status}）：{url}");

        string etag = response.Headers.ETag?.ToString() ?? "";
        string specified = args.Text("saveName").Trim();
        string name = specified.Length > 0
            ? SanitizeFileName(specified)
            : GuessName(url, response.Content.Headers.ContentDisposition?.ToString());

        string path = Path.Combine(FileText.Resolve(folder, ""), name);
        if (autoRename) path = UniquePath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        string part = path + ".ltools-part";
        long size = 0;
        string md5 = "";
        try
        {
            // 先写半成品：成了才改成正式名字，失败时用户目录里不会多一个「看起来下完了」的文件。
            using (var source = response.Content.ReadAsStream(cts.Token))
            using (var target = File.Create(part))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            {
                byte[] buffer = new byte[81_920];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ctx.EnterIteration();   // 停止热键 / 时间预算
                    hasher.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                    size += read;
                }
                target.Flush();
                md5 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            // autoRename = true 时 path 本来就不存在；= false 时这一步就是用户要的「覆盖」。
            File.Move(part, path, overwrite: true);
        }
        catch (OperationCanceledException) when (ctx.Token.IsCancellationRequested)
        {
            throw new ActionAbortedException("已停止", userCancelled: true);
        }
        catch (OperationCanceledException)
        {
            throw ActionErrors.Timeout($"下载超时（{timeoutMs} ms）：{url}");
        }
        finally
        {
            if (File.Exists(part)) TryDelete(part);
        }

        ctx.SetOutput("file", Value.FromText(path));
        ctx.SetOutput("size", Value.FromNumber(size));
        ctx.SetOutput("md5", Value.FromText(md5));
        ctx.SetOutput("etag", Value.FromText(etag));
        ctx.SetOutput("found", Value.FromBool(true));
    }

    /// <summary>
    /// 推断文件名：优先 <c>filename*=UTF-8''…</c>，其次 <c>filename=…</c>，再退回网址最后一段。
    /// 纯函数，自检直调。
    /// </summary>
    internal static string GuessName(string url, string? contentDisposition)
    {
        string cd = contentDisposition ?? "";
        string name = "";

        int star = cd.IndexOf("filename*=", StringComparison.OrdinalIgnoreCase);
        if (star >= 0)
            name = DispositionName(cd[(star + "filename*=".Length)..], rfc5987: true);

        if (name.Length == 0)
        {
            int plain = cd.IndexOf("filename=", StringComparison.OrdinalIgnoreCase);
            if (plain >= 0)
                name = DispositionName(cd[(plain + "filename=".Length)..], rfc5987: false);
        }

        if (name.Length == 0 && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            name = Uri.UnescapeDataString(uri.AbsolutePath);

        return SanitizeFileName(name);
    }

    private static string DispositionName(string tail, bool rfc5987)
    {
        string raw = tail.Split(';')[0].Trim();
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"') raw = raw[1..^1];

        if (rfc5987)
        {
            int quote = raw.IndexOf("''", StringComparison.Ordinal);
            if (quote >= 0) raw = raw[(quote + 2)..];
            try { raw = Uri.UnescapeDataString(raw); }
            catch (UriFormatException) { /* 坏转义：原样留着，后面还会清非法字符 */ }
        }
        return raw.Trim();
    }

    /// <summary>只取文件名、换掉非法字符；空名字退回 <c>download</c>。纯函数，自检直调。</summary>
    /// <remarks>
    /// <c>Path.GetFileName("a:b.txt")</c> 在 Windows 会把 <c>a:</c> 当盘符、只留 <c>b.txt</c>，
    /// 所以**先**把冒号换掉再去目录部分，否则用户填的文件名会被悄悄截短。
    /// </remarks>
    internal static string SanitizeFileName(string name)
    {
        name = (name ?? "").Trim().Replace(':', '_');
        name = Path.GetFileName(name);
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        name = name.Replace('/', '_').Replace('\\', '_').TrimStart('.', ' ');
        return name.Length == 0 ? "download" : name;
    }

    /// <summary>
    /// 目标已存在时追加 <c> (1)</c> / <c> (2)</c>；不存在则原样返回。纯命名判定，自检直调。
    /// </summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 1; i < 10_000; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }

        throw ActionErrors.Precondition($"同名文件太多，改不出新名字：{path}");
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* 半成品删不掉也只是留个 .ltools-part，不挡这次结果 */ }
        catch (UnauthorizedAccessException) { }
    }
}
