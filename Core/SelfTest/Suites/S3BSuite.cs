using System.Drawing;
using System.Drawing.Imaging;
using System.Xml.XPath;
using LTools.Core.Actions;
using LTools.Features.Units.Screen;
using LTools.Features.Units.Web;
using ZXing.QrCode.Internal;

namespace LTools.Core.SelfTest;

/// <summary>
/// S3-B（二维码 / HTML 提取 / UIA 控件操作）：纯计算向量与声明断言。
/// 不碰键鼠 / 窗口 / **网络** / 真实 UIA 树 / 用户配置；QR 与 HTML 走内存位图 / 内存字符串，
/// 真机点控件与删动作后的引用回收留 G3 点验。
/// </summary>
internal static class S3BSuite
{
    private const string SampleHtml =
        "<html><body><a href='/a'>Alpha &amp; Beta</a><a href='/b'>Gamma</a>" +
        "<div class='x'>Hi</div></body></html>";

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("s3b", "二维码生成 → 识别往返（英文 / 中文）", QrRoundTrip));
        cases.Add(new("s3b", "二维码：空白图 / 纠错等级", QrEdges));
        cases.Add(new("s3b", "二维码两个单元的声明", QrDeclarations));
        cases.Add(new("s3b", "web.html 提取向量（命中 / 未命中 / 属性 / 实体 / 语法错）", HtmlVectors));
        cases.Add(new("s3b", "web.html 与 ui.uia 声明", WebAndUiaDeclarations));
        cases.Add(new("s3b", "Pinyin 补字与无重复", PinyinCoverage));
    }

    private static void QrRoundTrip()
    {
        foreach (string text in new[] { "HELLO-LTOOLS-123", "hello 二维码 https://x.test/a?b=1" })
        {
            using Bitmap bitmap = QrCodeAlgorithms.Generate(text, 256, 4, "M");
            Check.Equal(256, bitmap.Width, "生成位图宽");
            Check.Equal(256, bitmap.Height, "生成位图高");

            QrCodeAlgorithms.DecodeResult result = QrCodeAlgorithms.Read(bitmap);
            Check.Equal(text, result.Text ?? "", "二维码往返文本一致");
            Check.Equal("QR_CODE", result.Type, "码制是 QR_CODE");
        }
    }

    private static void QrEdges()
    {
        using var blank = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(blank)) g.Clear(Color.White);
        Check.True(QrCodeAlgorithms.Read(blank).Text == null, "空白图识别不到（不抛异常）");

        Check.Equal(ErrorCorrectionLevel.L, QrCodeAlgorithms.LevelOf("L"), "纠错等级 L");
        Check.Equal(ErrorCorrectionLevel.M, QrCodeAlgorithms.LevelOf(""), "未知等级回落 M");
        Check.Equal(ErrorCorrectionLevel.Q, QrCodeAlgorithms.LevelOf("q"), "纠错等级大小写不敏感");
        Check.Equal(ErrorCorrectionLevel.H, QrCodeAlgorithms.LevelOf("H"), "纠错等级 H");

        // 小尺寸 / 零安静区也能编出来（只是更难扫），不该直接抛。
        using Bitmap small = QrCodeAlgorithms.Generate("x", 64, 0, "L");
        Check.Equal(64, small.Width, "最小边长 64 生效");
    }

    private static void QrDeclarations()
    {
        UnitSpec gen = StepRegistry.Default.Get("image.qrcodeGen").Spec;
        gen.Validate();
        Check.Equal("屏幕", gen.Category, "生成归「屏幕」");
        foreach (string key in new[] { "text", "size", "margin", "level", "target" })
            Check.True(gen.Inputs.Any(i => i.Key == key), $"生成声明参数 {key}");
        foreach (string key in new[] { "file", "width", "height", "text" })
            Check.True(gen.Outputs.Any(o => o.Key == key), $"生成声明输出 {key}");
        Check.False(gen.Inputs.Single(i => i.Key == "target").Required, "目标路径可留空");
        foreach (string option in gen.Inputs.Single(i => i.Key == "level").Options!)
            Check.False(string.Equals(
                OptionLabels.For(gen.Inputs.Single(i => i.Key == "level"), option), option,
                StringComparison.Ordinal), $"纠错等级「{option}」有中文名");

        UnitSpec read = StepRegistry.Default.Get("image.qrcodeRead").Spec;
        read.Validate();
        Check.Equal(UnitKind.pure, read.Kind, "识别是取值动作单元，可内联");
        Check.Equal(InputEditor.image, read.Inputs.Single(i => i.Key == "image").Editor,
            "识别的输入键叫 image（引用回收按 ProgramWalker.LiteralText(step,\"image\") 扫描，零改动）");
        foreach (string key in new[] { "value", "list", "count", "type", "found" })
            Check.True(read.Outputs.Any(o => o.Key == key), $"识别声明输出 {key}");
    }

    private static void HtmlVectors()
    {
        var all = HtmlUnit.Extract(SampleHtml, "//a", "");
        Check.Equal(2, all.Count, "命中两个 a 节点");
        Check.Equal("Alpha & Beta", all[0], "HTML 实体被解码");
        Check.Equal("Gamma", all[1], "第二个命中");

        Check.Equal("/a", HtmlUnit.Extract(SampleHtml, "//a", "href")[0], "按属性取值");
        Check.Equal("x", HtmlUnit.Extract(SampleHtml, "//div", "class")[0], "div 的 class 属性");
        Check.Equal(0, HtmlUnit.Extract(SampleHtml, "//h1", "").Count, "未命中返回空列表（不是报错）");

        Check.Throws<XPathException>(
            () => HtmlUnit.Extract(SampleHtml, "//a[", ""), "非法 XPath 抛 XPathException");
        ActionException empty = Check.Throws<ActionException>(
            () => HtmlUnit.Extract(SampleHtml, "", ""), "空 XPath 抛前置条件");
        Check.Equal(ActionErrorKind.precondition, empty.Kind, "空 XPath 标前置条件");
    }

    private static void WebAndUiaDeclarations()
    {
        UnitSpec html = StepRegistry.Default.Get("web.html").Spec;
        html.Validate();
        Check.Equal(UnitKind.pure, html.Kind, "HTML 提取是纯动作单元");
        foreach (string key in new[] { "value", "list", "count", "found" })
            Check.True(html.Outputs.Any(o => o.Key == key), $"web.html 声明输出 {key}");
        Check.True(StepRegistry.Default.Get("web.request").Spec.Inputs.All(i => i.Key != "operation"),
            "web.request 仍是唯一 HTTP 出口，没加 operation");

        UnitSpec uia = StepRegistry.Default.Get("ui.uia").Spec;
        uia.Validate();
        Check.Equal("界面", uia.Category, "控件操作归「界面」");
        Check.False(uia.Inputs.Single(i => i.Key == "timeoutMs").Advanced,
            "ui.uia 的 timeoutMs 必须可见");
        Check.Contains(uia.Inputs.Single(i => i.Key == "timeoutMs").Description, "-1",
            "timeoutMs 说明写全三值");
        Check.True((uia.Inputs.Single(i => i.Key == "title").PickerSet & PickerKind.window) != 0,
            "窗口标题挂「点击窗口选取」入口");

        InputSpec action = uia.Inputs.Single(i => i.Key == "action");
        Check.True(action.OptionParams != null, "action 用 OptionParams 收起 value");
        Check.True(action.OptionParams!["setValue"].Contains("value"), "设值显示 value");
        Check.False(action.OptionParams!["click"].Contains("value"), "单击不显示 value");
        Check.False(action.OptionParams!["getText"].Contains("value"), "读文本不显示 value");
        foreach (string option in action.Options!)
            Check.False(string.Equals(OptionLabels.For(action, option), option, StringComparison.Ordinal),
                $"操作「{option}」有中文名");
        foreach (string key in new[] { "found", "text", "x", "y", "bounds", "count" })
            Check.True(uia.Outputs.Any(o => o.Key == key), $"ui.uia 声明输出 {key}");
    }

    private static void PinyinCoverage()
    {
        Check.Equal(0, Pinyin.DuplicateCount, "拼音表没有重复字（多音字按词只留一个读音）");
        Check.Equal("scewm", Pinyin.Initials("生成二维码"), "生成二维码 → scewm");
        Check.Equal("sbewm", Pinyin.Initials("识别二维码"), "识别二维码 → sbewm");
        Check.Equal("htmltq", Pinyin.Initials("HTML 提取"), "HTML 提取 → htmltq");
        Check.Equal("kjcz", Pinyin.Initials("控件操作"), "控件操作 → kjcz");
        Check.True(Pinyin.Matches("生成二维码", Pinyin.NormalizeQuery("ewm")), "ewm 能搜到二维码");
    }
}
