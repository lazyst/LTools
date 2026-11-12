using System.Diagnostics;
using LTools.Core.Actions;

namespace LTools.Features.Units.Web;

/// <summary>
/// 用默认浏览器打开网址（<c>web.open</c>）。
/// </summary>
/// <remarks>
/// 网址不是文件，所以归「网络」分类、也不做路径解析；
/// 本地文件走 <c>file.openFile</c>，发 HTTP 请求 / 取响应走 <c>web.request</c>。
/// </remarks>
public sealed class OpenUrlUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "web.open",
        Name = "用浏览器打开网址",
        Category = "网络",
        Icon = "globe",
        Description = "用系统默认浏览器打开一个网址；只打开、不读取内容（要发请求 / 拿响应请用「网络请求」）",
        Inputs = new[]
        {
            new InputSpec("url", "网址", ValueKind.text, InputEditor.text, Required: true,
                Description: "完整网址，如 https://example.com；可写 ${变量} / 内联取值，也支持 mailto: 等系统协议"),
        },
        Outputs = new[]
        {
            new OutputSpec("url", "实际网址", ValueKind.text, "url",
                Description: "实际打开的网址；打不开时本步直接失败，不会有输出"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string url = args.Text("url").Trim();
        if (url.Length == 0) throw new ActionException("网址为空");

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        ctx.SetOutput("url", Value.FromText(url));
    }
}
