using System.Net;
using System.Xml.XPath;
using HtmlAgilityPack;
using LTools.Core.Actions;

namespace LTools.Features.Units.Web;

/// <summary>
/// HTML 提取（<c>web.html</c>）：用 XPath 从 HTML 文本里取节点文本 / 属性。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>web.request</c> 串成「请求 → 提取」两步：<c>html</c> 填「HTTP 请求」的
/// <c>${response}</c>。本单元**不发请求**、也不给 <c>web.request</c> 加 <c>operation</c>——
/// 后者仍是唯一的 HTTP 出口。
/// </para>
/// <para>
/// 三种语义分明：**没命中** = <c>found = false</c>（不报错，配「如果」判断即可）；
/// **XPath 语法错 / 表达式不返回节点集** = <see cref="ActionErrors.Precondition"/>（这才是写错了）；
/// **命中** = <c>value</c> 给第一个、<c>list</c> 给全部（照 <c>PathListOutput</c> 的既有形状）。
/// </para>
/// <para>
/// 输出形状按采纳文档固定为 <c>value</c> / <c>list</c> / <c>count</c> / <c>found</c>；
/// 不设 <c>mode = single/list</c>（少一个语义分叉，要单个就用 <c>value</c>），
/// 也不设 <c>encoding</c>（传进来的已经是解码好的文本）。见 §9.5 偏差。
/// </para>
/// </remarks>
public sealed class HtmlUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "web.html",
        Name = "HTML 提取",
        Category = "网络",
        Icon = "filter",
        Kind = UnitKind.pure,
        Description = "用 XPath 从 HTML 里提取文本 / 属性；「HTTP 请求」→「HTML 提取」两步串起来",
        Inputs = new[]
        {
            new InputSpec("html", "HTML", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "HTML 文本；一般填「HTTP 请求」的响应文本 ${response}"),
            new InputSpec("xpath", "XPath", ValueKind.text, Required: true,
                Description: "如 //a、//div[@class='item']；语法错才报错，没命中只是 found = false"),
            new InputSpec("attribute", "取属性", ValueKind.text,
                Description: "填了就取这个属性的值（如 href）；留空 = 取节点文本（自动解 HTML 实体）"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "第一个", ValueKind.text, "htmlValue",
                Description: "第一个命中的文本；没命中为空"),
            new OutputSpec("list", "全部", ValueKind.list, "htmlList",
                Description: "全部命中的文本列表"),
            new OutputSpec("count", "数量", ValueKind.number, "htmlCount",
                Description: "命中节点数量"),
            new OutputSpec("text", "文本", ValueKind.text,
                Description: "全部命中按行拼接的文本"),
            new OutputSpec("found", "命中了", ValueKind.@bool, "htmlFound",
                Description: "是否命中至少一个节点"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string xpath = args.Text("xpath").Trim();
        string attribute = args.Text("attribute").Trim();
        List<string> values;
        try
        {
            values = Extract(args.Text("html"), xpath, attribute);
        }
        catch (XPathException ex)
        {
            throw ActionErrors.Precondition($"XPath 写错了：{ex.Message}", ex);
        }
        catch (ActionException)
        {
            throw;   // Extract 自己抛的空 XPath / 前置条件，原样带性质往上走
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw ActionErrors.Precondition($"HTML 提取失败：{ex.Message}", ex);
        }

        ctx.SetOutput("value", Value.FromText(values.Count > 0 ? values[0] : ""));
        ctx.SetOutput("list", Value.FromList(values.Select(v => Value.FromText(v))));
        ctx.SetOutput("count", Value.FromNumber(values.Count));
        ctx.SetOutput("text", Value.FromText(string.Join("\n", values)));
        ctx.SetOutput("found", Value.FromBool(values.Count > 0));
    }

    /// <summary>纯抽取：动作单元与 <c>--selftest</c> 共用，不碰 WPF / 网络。</summary>
    internal static List<string> Extract(string html, string xpath, string attribute)
    {
        if (xpath.Trim().Length == 0) throw ActionErrors.Precondition("XPath 不能为空");

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        HtmlNodeCollection? nodes = doc.DocumentNode.SelectNodes(xpath.Trim());
        var values = new List<string>();
        if (nodes == null) return values;

        foreach (HtmlNode node in nodes)
        {
            string raw = attribute.Trim().Length > 0
                ? node.GetAttributeValue(attribute.Trim(), "")
                : node.InnerText;
            values.Add(WebUtility.HtmlDecode(raw).Trim());
        }
        return values;
    }
}
