using System.Net;
using System.Text;
using LTools.Core.Actions;
using LTools.Features.Units.Files;

namespace LTools.Features.Units.Text;

/// <summary>
/// 编解码：Base64 / URL / HTML，文本与文件两种来源（<c>text.encode</c>）。
/// </summary>
/// <remarks>
/// <para>
/// Base64 既能处理文本也能处理整个文件——「图片转 Base64」就是 <c>file</c> 填图片、
/// 方式选编码；解码时把 <c>file</c> 填上，字节直接落盘（反过来做 Base64 → 图片）。
/// </para>
/// <para>
/// 解码 Base64 时容忍换行、空格、URL 安全字符（<c>-_</c>）和缺失的 <c>=</c> 补齐，
/// 因为从网页 / JWT 里复制出来的串经常是这几种样子。
/// </para>
/// </remarks>
public sealed class EncodeUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "base64", "unbase64", "url", "unurl", "html", "unhtml" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["base64"] = "Base64 编码",
        ["unbase64"] = "Base64 解码",
        ["url"] = "URL 编码",
        ["unurl"] = "URL 解码",
        ["html"] = "HTML 转义",
        ["unhtml"] = "HTML 反转义",
    };

    /// <summary>只列能用来「把字节解释成文字」的几种，不含 <c>auto</c>（那是读文件时才有的猜测）。</summary>
    private static readonly string[] Charsets = { "utf8", "unicode", "gbk" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.encode",
        Name = "编解码",
        Category = "文本与数据",
        Icon = "script",
        Description = "Base64 / URL / HTML 编码解码；Base64 可直接读整个文件（图片转 Base64）或把结果写成文件",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("base64"),
                Options: Operations, Labels: OperationLabels,
                Description: "编码把内容变成一串字符，解码把一串字符还原成内容"),
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline,
                Description: "要编码的文本，或要解码的 Base64 / URL / HTML 文本"),
            new InputSpec("file", "文件", ValueKind.text, InputEditor.file,
                Description: "编码：读这个文件的字节（图片、压缩包都行）；解码：把还原出的字节写到这个文件。留空 = 只处理上面的文本"),
            new InputSpec("charset", "字符集", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: Charsets,
                VisibleWhen: "operation == 'base64' || operation == 'unbase64'",
                Description: "文本 ↔ 字节按哪个字符集换算；处理文件字节时无关"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "结果", ValueKind.text, "encoded",
                Description: "编码 / 解码后的文本；写文件时为空"),
            new OutputSpec("file", "写出的文件", ValueKind.text,
                Description: "解码并写入文件时的路径；没写文件为空"),
            new OutputSpec("size", "字节数", ValueKind.number,
                Description: "参与编码 / 解码的字节数"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        string text = args.Text("text");
        string file = args.Text("file").Trim();
        Encoding encoding = FileText.GetEncoding(args.Text("charset"));

        ctx.SetOutput("file", Value.FromText(""));

        switch (op)
        {
            case "base64":
            {
                byte[] bytes = file.Length > 0
                    ? File.ReadAllBytes(FileText.Resolve(file, ""))
                    : encoding.GetBytes(text);
                ctx.SetOutput("text", Value.FromText(Convert.ToBase64String(bytes)));
                ctx.SetOutput("size", Value.FromNumber(bytes.Length));
                break;
            }

            case "unbase64":
            {
                byte[] bytes = DecodeBase64(text);
                ctx.SetOutput("size", Value.FromNumber(bytes.Length));
                if (file.Length > 0)
                {
                    string path = FileText.Resolve(file, "");
                    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                    File.WriteAllBytes(path, bytes);
                    ctx.SetOutput("text", Value.FromText(""));
                    ctx.SetOutput("file", Value.FromText(path));
                }
                else
                {
                    ctx.SetOutput("text", Value.FromText(encoding.GetString(bytes)));
                }
                break;
            }

            case "url":
                ctx.SetOutput("text", Value.FromText(Uri.EscapeDataString(text)));
                ctx.SetOutput("size", Value.FromNumber(encoding.GetByteCount(text)));
                break;

            case "unurl":
                ctx.SetOutput("text", Value.FromText(Uri.UnescapeDataString(text)));
                ctx.SetOutput("size", Value.FromNumber(encoding.GetByteCount(text)));
                break;

            case "html":
                ctx.SetOutput("text", Value.FromText(WebUtility.HtmlEncode(text)));
                ctx.SetOutput("size", Value.FromNumber(encoding.GetByteCount(text)));
                break;

            case "unhtml":
                ctx.SetOutput("text", Value.FromText(WebUtility.HtmlDecode(text)));
                ctx.SetOutput("size", Value.FromNumber(encoding.GetByteCount(text)));
                break;

            default:
                throw new ActionException($"未知方式：{op}");
        }
    }

    /// <summary>
    /// 宽松解析 Base64：去掉空白、把 URL 安全字符换回来、补足 <c>=</c>。
    /// 解析不了就是这一步失败（不返回半个结果）。
    /// </summary>
    private static byte[] DecodeBase64(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            sb.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        string clean = sb.ToString().TrimEnd('=');
        if (clean.Length == 0) throw new ActionException("Base64 文本为空");

        int remainder = clean.Length % 4;
        if (remainder == 1) throw new ActionException("不是合法的 Base64：长度不对");
        if (remainder > 0) clean += new string('=', 4 - remainder);

        try
        {
            return Convert.FromBase64String(clean);
        }
        catch (FormatException)
        {
            throw new ActionException("不是合法的 Base64 文本");
        }
    }
}
