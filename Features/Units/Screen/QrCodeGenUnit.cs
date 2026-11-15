using System.Drawing.Imaging;
using LTools.Core.Actions;
using LTools.Features.Units.Files;

namespace LTools.Features.Units.Screen;

/// <summary>
/// 生成二维码（<c>image.qrcodeGen</c>）：文本 → png 文件路径。
/// </summary>
/// <remarks>
/// <para>
/// 与「识别二维码」拆开：生成是「给后续步骤一张图」，识别是「从一张图里读文本」，
/// 输入输出完全不同，别做一个带 <c>operation</c> 的二合一单元。
/// </para>
/// <para>
/// <c>target</c> 留空时走 <see cref="ImageLibrary.SaveCapture"/>（<c>cache/screenshots/</c>，
/// 运行期产物，下次运行开始 / 启动时清空，不进备份）——生成图是程序产物，不按引用长期回收；
/// 填了路径就写到用户指定的位置。两种都输出绝对路径，可直接给「文字识别」「图片处理」
/// 「剪贴板图片」当入参。
/// </para>
/// </remarks>
public sealed class QrCodeGenUnit : IStepUnit
{
    private static readonly Dictionary<string, string> LevelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["L"] = "L · 约 7% 纠错",
        ["M"] = "M · 约 15% 纠错（默认）",
        ["Q"] = "Q · 约 25% 纠错",
        ["H"] = "H · 约 30% 纠错",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "image.qrcodeGen",
        Name = "生成二维码",
        Category = "屏幕",
        Icon = "image",
        Description = "把文本编成二维码 png；留空目标路径时输出到运行期缓存（下次运行清空）",
        Inputs = new[]
        {
            new InputSpec("text", "内容", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "网址 / 文本；可写 ${变量}，支持中文（按 UTF-8 编码）"),
            new InputSpec("size", "边长", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(256), Description: "图片边长（像素）：64–2048；内容越长越要调大"),
            new InputSpec("margin", "安静区", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(4), Advanced: true,
                Description: "四周留白的模块数：0–32；扫码器一般要求 ≥ 4"),
            new InputSpec("level", "纠错等级", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("M"), Options: QrCodeAlgorithms.Levels,
                Labels: LevelLabels, Advanced: true,
                Description: "越高越抗遮挡，但同样内容会编出更密的码"),
            new InputSpec("target", "输出文件", ValueKind.text, InputEditor.file,
                Description: "留空 = cache/screenshots（运行期产物，下次运行开始清空）；填了就按这个路径写 png"),
        },
        Outputs = new[]
        {
            new OutputSpec("file", "图片路径", ValueKind.text, "qrFile",
                Description: "生成的 png 绝对路径，可直接给「文字识别」「图片处理」「剪贴板图片」用"),
            new OutputSpec("width", "宽", ValueKind.number, Description: "图片像素宽"),
            new OutputSpec("height", "高", ValueKind.number, Description: "图片像素高"),
            new OutputSpec("text", "内容", ValueKind.text, Description: "写进二维码的原文"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string text = args.Text("text");
        if (text.Length == 0) throw ActionErrors.Precondition("二维码内容不能为空");

        using System.Drawing.Bitmap bitmap = QrCodeAlgorithms.Generate(
            text, (int)args.Number("size"), (int)args.Number("margin"), args.Text("level"));

        string target = args.Text("target").Trim();
        string path;
        if (target.Length == 0)
        {
            path = ImageLibrary.SaveCapture(bitmap);
        }
        else
        {
            path = FileText.Resolve(target, "");
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            bitmap.Save(path, ImageFormat.Png);
        }

        ctx.SetOutput("file", Value.FromText(path));
        ctx.SetOutput("width", Value.FromNumber(bitmap.Width));
        ctx.SetOutput("height", Value.FromNumber(bitmap.Height));
        ctx.SetOutput("text", Value.FromText(text));
    }
}
