using LTools.Core.Actions;

namespace LTools.Features.Units.Screen;

/// <summary>
/// 识别二维码（<c>image.qrcodeRead</c>）：png → 文本。
/// </summary>
/// <remarks>
/// <para>
/// 取值动作单元（<c>UnitKind.pure</c>），可以内联在参数里。找不到二维码只是
/// <c>found = false</c>，**不报错**（与「查找窗口」「截图找图」的取值器同口径）；
/// 文件不存在才是 <see cref="ActionErrors.NotFound"/>。
/// </para>
/// <para>
/// 图片路径走 <see cref="ImageLibrary.Resolve(string, string)"/> 并把
/// <c>ctx.ResourceRoot</c> 传进去：草稿里的 <c>files/x.png</c> 先在草稿资源根找，
/// 正式动作先在 <c>actions/&lt;id&gt;/files/</c> 找。参数名就叫 <c>image</c>，
/// <see cref="ImageLibrary.CollectAction"/> 按 <c>ProgramWalker.LiteralText(step, "image")</c>
/// 扫描，删动作后引用的图会被按引用回收，**不需要另改回收代码**。
/// </para>
/// <para>本地识别，不加在线识别兜底（上游已排除云服务）。</para>
/// </remarks>
public sealed class QrCodeReadUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "image.qrcodeRead",
        Name = "识别二维码",
        Category = "屏幕",
        Icon = "search",
        Kind = UnitKind.pure,
        Description = "读图片里的二维码；找不到 found = false，不报错（本地识别，不联网）",
        Inputs = new[]
        {
            new InputSpec("image", "图片", ValueKind.text, InputEditor.image, Required: true,
                Description: "图片路径 / 「截图」步骤的输出；files/x.png 先在动作资源根找"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "内容", ValueKind.text, "qrText",
                Description: "识别到的文本；没识别到为空"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "同「内容」，方便直接当文本用"),
            new OutputSpec("list", "全部", ValueKind.list,
                Description: "识别到的码文本列表（本批最多 1 项，见 §9.5 偏差）"),
            new OutputSpec("count", "数量", ValueKind.number, "qrCount",
                Description: "识别到的码数量（0 / 1）"),
            new OutputSpec("type", "码制", ValueKind.text, Description: "如 QR_CODE；没识别到为空"),
            new OutputSpec("found", "找到了", ValueKind.@bool, "qrFound",
                Description: "是否识别到二维码；没识别到不报错"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = ImageLibrary.Resolve(ScreenArgs.ImagePath(args), ctx.ResourceRoot);
        if (!File.Exists(path)) throw ActionErrors.NotFound($"图片不存在：{path}");

        // 按字节读，避开中文路径下 GDI+ 的路径解码差异（与 image.transform 的 Load 同一条弯路）。
        using var stream = File.OpenRead(path);
        using var bitmap = new System.Drawing.Bitmap(stream);

        QrCodeAlgorithms.DecodeResult result = QrCodeAlgorithms.Read(bitmap);
        string text = result.Text ?? "";
        bool found = result.Text != null;

        ctx.SetOutput("value", Value.FromText(text));
        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("list", Value.FromList(
            found ? new[] { Value.FromText(text) } : Array.Empty<Value>()));
        ctx.SetOutput("count", Value.FromNumber(found ? 1 : 0));
        ctx.SetOutput("type", Value.FromText(result.Type));
        ctx.SetOutput("found", Value.FromBool(found));
    }
}
