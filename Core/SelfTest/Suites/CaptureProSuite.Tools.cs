using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>CaptureProSuite 的「工具目录 / 取色 / 屏幕区域 / 采集清单」部分。</summary>
internal static partial class CaptureProSuite
{
    private static void Tools()
    {
        var all = ToolCatalog.All;
        Check.Equal(Enum.GetValues<CaptureTool>().Length, all.Length, "每个工具都在目录里");
        Check.Equal(all.Length, all.Select(t => t.Tool).Distinct().Count(), "工具不重复");
        Check.Equal(all.Length, all.Select(t => t.Name).Distinct().Count(), "名字不重复");
        Check.Equal(all.Length, all.Select(t => t.Icon).Distinct().Count(), "图标键不重复");
        Check.Equal(all.Length, all.Select(t => t.Shortcut).Distinct().Count(), "字母快捷键不重复");

        // 字母 → 工具（覆盖层在没打字时按它切工具）
        Check.Equal(CaptureTool.Spotlight, ToolCatalog.ForKey(System.Windows.Input.Key.S), "S = 聚光（Ctrl+S 是另存为，先判 Ctrl）");
        Check.Equal(CaptureTool.Pen, ToolCatalog.ForKey(System.Windows.Input.Key.P), "P = 画笔");
        Check.Equal(CaptureTool.Select, ToolCatalog.ForKey(System.Windows.Input.Key.Q), "Q = 选择");
        Check.Equal(CaptureTool.Picker, ToolCatalog.ForKey(System.Windows.Input.Key.C), "C = 取色（Ctrl+C 是复制，先判 Ctrl）");
        Check.True(ToolCatalog.ForKey(System.Windows.Input.Key.F13) == null, "没绑的键返回 null");

        foreach (var info in all)
        {
            Check.True(!string.IsNullOrWhiteSpace(info.Name), info.Tool + " 有名字");
            Check.True(!string.IsNullOrWhiteSpace(info.Hint), info.Tool + " 有说明（工具栏 ToolTip 用它）");
            Check.True(!string.IsNullOrWhiteSpace(info.Icon), info.Tool + " 有图标键");
            // 参数格要么没有，要么不重复（重复会让参数条出现两个一样的加号组）
            Check.Equal(info.Params.Length, info.Params.Distinct().Count(), info.Tool + " 参数格不重复");
        }

        // 参数格按工具分：画笔没块大小，马赛克没颜色，文字没线宽，擦除 / 选择一格都不用
        Check.True(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Color), "画笔有颜色");
        Check.True(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Width), "画笔有线宽");
        Check.False(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Block), "画笔没有块大小");
        Check.True(ToolCatalog.Uses(CaptureTool.Highlighter, ToolParam.Opacity), "荧光笔有透明度");
        Check.True(ToolCatalog.Uses(CaptureTool.Text, ToolParam.TextSize), "文字有字号");
        Check.False(ToolCatalog.Uses(CaptureTool.Text, ToolParam.Width), "文字没有线宽");
        Check.True(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.Block), "马赛克有块大小");
        Check.False(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.Color), "马赛克没有颜色");
        Check.False(ToolCatalog.Uses(CaptureTool.Eraser, ToolParam.Color), "擦除不用任何参数格");
        Check.False(ToolCatalog.Uses(CaptureTool.Select, ToolParam.Width), "选择不用任何参数格");
        Check.Equal(0, ToolCatalog.Of(CaptureTool.Picker).Params.Length, "取色不用任何参数格");

        // 新增的两族参数：序号要形状 / 写法，滤镜要类型 / 强度（马赛克的块大小不给滤镜）
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.BadgeShape), "序号有徽标类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.Numbering), "序号有数字写法");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.SerialNumber), "序号有「下一个序号」");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.SerialFontSize), "序号有字号（与文字 / 水印分开）");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.Width), "序号有线宽（上游 buildSequenceNumberFamily 有 strokeWidth）");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Connection), "放大镜有连接方式");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.SourceOutline), "放大镜有源轮廓开关");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.ArrowRatio), "放大镜有箭头比例");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.LeaderArrowhead), "放大镜有引导线端点");
        Check.True(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.FilterKind), "滤镜有类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.FilterStrength), "滤镜有强度");
        Check.False(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.Block), "滤镜没有马赛克块大小");
        Check.False(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.FilterKind), "马赛克没有滤镜类型（两个工具别混）");
        Check.Equal(CaptureTool.Filter, ToolCatalog.ForKey(System.Windows.Input.Key.F), "F = 滤镜");

        // P1 / P2：与 Snow Shot 对齐的新工具（上游 32 项 → 这里 25 项，其余是 P4 的识别类）
        Check.Equal(25, all.Length, "工具目录 25 项");
        Check.Equal(CaptureTool.AutoFilter, ToolCatalog.ForKey(System.Windows.Input.Key.V), "V = 自动打码");
        Check.Equal(CaptureTool.Line, ToolCatalog.ForKey(System.Windows.Input.Key.I), "I = 直线");
        Check.Equal(CaptureTool.Distance, ToolCatalog.ForKey(System.Windows.Input.Key.D), "D = 测距");
        Check.Equal(CaptureTool.Angle, ToolCatalog.ForKey(System.Windows.Input.Key.G), "G = 测角");
        Check.Equal(CaptureTool.Bracket, ToolCatalog.ForKey(System.Windows.Input.Key.B), "B = 括号");
        Check.Equal(CaptureTool.SpeechBubble, ToolCatalog.ForKey(System.Windows.Input.Key.O), "O = 气泡");
        Check.Equal(CaptureTool.Magnifier, ToolCatalog.ForKey(System.Windows.Input.Key.Z), "Z = 放大镜");
        Check.Equal(CaptureTool.Watermark, ToolCatalog.ForKey(System.Windows.Input.Key.W), "W = 水印");
        Check.Equal(CaptureTool.HighlightRect, ToolCatalog.ForKey(System.Windows.Input.Key.J), "J = 矩形荧光");
        Check.Equal(CaptureTool.BrushFilter, ToolCatalog.ForKey(System.Windows.Input.Key.Y), "Y = 画笔滤镜");
        Check.Equal(CaptureTool.RectEraser, ToolCatalog.ForKey(System.Windows.Input.Key.K), "K = 矩形橡皮");
        Check.Equal(CaptureTool.BrushEraser, ToolCatalog.ForKey(System.Windows.Input.Key.U), "U = 画笔橡皮");

        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Factor), "放大镜有倍率");
        Check.False(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Block), "放大镜没有马赛克块大小");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkText), "水印有文字");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkAngle), "水印有角度");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkGap), "水印有间距");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkTemplate), "水印有模板下拉");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkOpacity), "水印有不透明度（与荧光笔分开）");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.FontSize), "水印有字号（12 / 16 / 24 / 30 预设）");
        Check.False(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.Width), "水印没有线宽");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushFilter, ToolParam.FilterKind), "画笔滤镜有类型");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushFilter, ToolParam.Width), "画笔滤镜有笔头宽度");
        Check.False(ToolCatalog.Uses(CaptureTool.HighlightRect, ToolParam.Width), "矩形荧光没有线宽（只有色 + 透明度）");
        Check.True(ToolCatalog.Uses(CaptureTool.HighlightRect, ToolParam.Opacity), "矩形荧光有透明度");

        // 笔迹类 / 橡皮类的判定只有这一处（手势与最小尺寸都读它）
        foreach (var tool in new[] { CaptureTool.Pen, CaptureTool.Highlighter, CaptureTool.BrushFilter, CaptureTool.BrushEraser })
        {
            Check.True(ToolCatalog.IsFreehand(tool), tool + " 走笔迹");
            Check.Equal(1d, ToolCatalog.MinimumSize(tool), tool + " 的最小尺寸是 1 DIP");
        }
        foreach (var tool in new[] { CaptureTool.Eraser, CaptureTool.RectEraser, CaptureTool.BrushEraser })
            Check.True(ToolCatalog.IsEraser(tool), tool + " 是橡皮");
        Check.False(ToolCatalog.IsFreehand(CaptureTool.RectEraser), "矩形橡皮不是笔迹（拖矩形）");
        Check.False(ToolCatalog.IsEraser(CaptureTool.Pen), "画笔不是橡皮");

        // 一维工具：横着拖也要能画（只判长度，不要求两个方向都动）
        foreach (var tool in new[] { CaptureTool.Line, CaptureTool.Distance, CaptureTool.Bracket, CaptureTool.Arrow })
            Check.True(ToolCatalog.IsLinear(tool), tool + " 是一维工具");
        Check.False(ToolCatalog.IsLinear(CaptureTool.Shape), "形状不是一维工具（宽高都得够）");

        // 每种滤镜都有自己的名字（参数行 chip 与 tooltip 共用，别留空）
        foreach (var kind in Enum.GetValues<FilterKind>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Name(kind)), kind + " 有中文名");
        foreach (var numbering in Enum.GetValues<SerialNumbering>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Chip(numbering)), numbering + " 有 chip 标签");
        foreach (var badge in Enum.GetValues<SerialBadge>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Name(badge)), badge + " 有中文名");

        // 光标：只有选择和文字不是十字
        Check.False(ToolCatalog.WantsCrossCursor(CaptureTool.Select), "选择用箭头");
        Check.False(ToolCatalog.WantsCrossCursor(CaptureTool.Text), "文字用 I 形");
        Check.True(ToolCatalog.WantsCrossCursor(CaptureTool.Shape), "形状用十字");

        // 点一下不该留下 0 像素标注：手绘 1 DIP 就算，形状要 4 DIP
        Check.Equal(1d, ToolCatalog.MinimumSize(CaptureTool.Pen), "手绘的最小尺寸");
        Check.Equal(1d, ToolCatalog.MinimumSize(CaptureTool.Highlighter), "荧光笔的最小尺寸");
        Check.Equal(4d, ToolCatalog.MinimumSize(CaptureTool.Shape), "形状的最小尺寸");

        // 样式：默认值、荧光笔笔头、聚光的压暗换算
        var style = new ToolStyle();
        Check.Equal(ToolStyle.Palette[0], style.Color, "默认颜色是调色盘第一格");
        Check.Equal(4d, style.Width, "默认线宽");
        Check.Equal(20d, style.FontSize, "默认字号");
        Check.Equal(8d, style.Block, "默认块大小");
        Check.Equal(4d, style.WidthFor(CaptureTool.Pen), "画笔用线宽本身");
        Check.True(style.WidthFor(CaptureTool.Highlighter) >= 10, "荧光笔笔头至少 10（否则看着就是普通线）");
        Check.Equal(0.65, Math.Round(style.Dim, 6), "透明度 0.35 → 框外压暗 0.65");
        style.Opacity = 0.95;
        Check.True(style.Dim <= 0.85, "压暗上限 0.85");
        style.Opacity = 1;
        Check.True(style.Dim >= 0, "压暗不为负");
    }

    /// <summary>
    /// 取色：从画面读像素并按 <c>#RRGGBB</c> / <c>rgb()</c> 出文本。
    /// 越界与没有底图都只是 <c>null</c>（光标在窗口边缘时算出界是正常情况，不该报错）。
    /// </summary>
    private static void Picker()
    {
        // 2×1 的假图：左边纯红、右边纯蓝（BGRA 字节序）
        var pixels = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF };
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();

        var left = PickerColor.Read(image, 0, 0);
        var right = PickerColor.Read(image, 1, 0);
        Check.True(left.HasValue && left.Value == Color.FromRgb(0xFF, 0x00, 0x00), "左像素是红（BGRA 没读反）");
        Check.True(right.HasValue && right.Value == Color.FromRgb(0x00, 0x00, 0xFF), "右像素是蓝");

        Check.True(PickerColor.Read(image, 2, 0) == null, "横向越界返回 null");
        Check.True(PickerColor.Read(image, 0, 1) == null, "纵向越界返回 null");
        Check.True(PickerColor.Read(null, 0, 0) == null, "没有底图返回 null");

        var sample = Color.FromRgb(0xFF, 0x80, 0x00);
        Check.Equal("#FF8000", PickerColor.Hex(sample), "十六进制大写补零");
        Check.Equal("rgb(255, 128, 0)", PickerColor.Rgb(sample), "rgb 文本");
        Check.Equal("#000000 · rgb(0, 0, 0)", PickerColor.Describe(Colors.Black), "提示条正文 = 两种写法");

        // 光标旁那块 11 × 11 预览：中间那一格就是取样点，越界时边缘像素重复（照上游逐点 clamp）
        var patchImage = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        patchImage.Freeze();

        byte[]? patch = PickerColor.ReadPatch(patchImage, 0, 0);
        Check.True(patch != null && patch.Length == PickerColor.PatchSize * PickerColor.PatchSize * 4,
            "预览块是 11 × 11 × 4 字节");
        Check.Equal((byte)0x00, patch![4 * (5 * 11 + 5)], "正中那一格的蓝分量 = (0,0) 的蓝（红像素）");
        Check.Equal((byte)0xFF, patch[4 * (5 * 11 + 5) + 2], "正中那一格的红色分量 = 255");
        Check.Equal((byte)0x00, patch[4 * (0 * 11 + 0)], "左上角越界：重复边缘像素（还是左边那个红）");
        Check.Equal((byte)0xFF, patch[4 * (5 * 11 + 10)], "取 (0,0) 时右边几列越界：重复右边那个蓝像素");

        byte[]? rightPatch = PickerColor.ReadPatch(patchImage, 1, 0);
        Check.True(rightPatch != null, "取右像素也有预览块");
        Check.Equal((byte)0xFF, rightPatch![4 * (5 * 11 + 5)], "正中那一格跟着取样点走（蓝）");
        Check.True(PickerColor.ReadPatch(null, 0, 0) == null, "没有底图 → 没有预览块");

        Check.True(PickerColor.PatchCanvas == PickerColor.PatchSize * PickerColor.PatchScale,
            "预览格边长 = 11 × 12 = 132 DIP");

        // 色带上的字：亮的底色用黑字、暗的用白字（照上游 readableTextColor）
        Check.Equal(Color.FromArgb(224, 0, 0, 0), PickerColor.ReadableTextColor(Colors.White), "浅底色配黑字");
        Check.Equal(Color.FromArgb(217, 255, 255, 255), PickerColor.ReadableTextColor(Colors.Black), "深底色配白字");
        Check.Equal(Color.FromArgb(224, 0, 0, 0),
            PickerColor.ReadableTextColor(Color.FromRgb(0x81, 0x00, 0x00)), "任一分量 > 128 就算亮");
    }

    /// <summary>
    /// 跨屏选区：取景范围 = 所有显示器的并集，逐块抓帧的交集。
    /// 坐标是物理像素，主屏左侧 / 上方的副屏会让 Left / Top 变成负数。
    /// </summary>
    private static void ScreenArea()
    {
        var single = ScreenShot.Union(new[] { new Int32Rect(0, 0, 1920, 1080) });
        Check.Equal(new Int32Rect(0, 0, 1920, 1080), single, "单屏 = 自己");

        // 副屏在右边（高矮不同）
        var side = ScreenShot.Union(new[]
        {
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(1920, 0, 1280, 1024),
        });
        Check.Equal(new Int32Rect(0, 0, 3200, 1080), side, "并集取外接矩形");

        // 副屏在左边：Left 为负
        var left = ScreenShot.Union(new[]
        {
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(-1280, -200, 1280, 1024),
        });
        Check.Equal(new Int32Rect(-1280, -200, 3200, 1280), left, "左 / 上方的副屏 → 负坐标");

        // 空表 / 空矩形
        Check.Equal(Int32Rect.Empty, ScreenShot.Union(Array.Empty<Int32Rect>()), "没有显示器 → 空矩形");
        Check.Equal(Int32Rect.Empty,
            ScreenShot.Union(new[] { new Int32Rect(0, 0, 0, 100) }), "宽 0 当空矩形");

        // 交集：逐块抓帧时用
        Check.Equal(new Int32Rect(1800, 500, 120, 580),
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(1800, 500, 500, 584)),
            "选区跨到副屏 → 主屏只抓相交的那块");
        Check.Equal(new Int32Rect(0, 0, 1920, 1080),
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(-100, -100, 3000, 3000)),
            "选区比屏幕大 → 取屏幕那一块");
        Check.Equal(Int32Rect.Empty,
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(1920, 0, 100, 100)),
            "贴边不算相交");
    }

    /// <summary>
    /// 采集历史：保留策略（数量 + 天数）、缩略图尺寸、列表文案。
    /// 全是纯函数——真正落盘的读写由窗口 / 冒烟验证，自检不碰 <c>captures/</c>。
    /// </summary>
    private static void Catalog()
    {
        static CaptureEntry Entry(string id, string createdAt, string kind = CaptureCatalog.KindShot) => new()
        {
            Id = id,
            CreatedAt = createdAt,
            Kind = kind,
            Width = 1200,
            Height = 800,
            Source = "复制",
            State = "done",
        };

        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            Entry("a", "2026-11-16T11:00:00"),
            Entry("b", "2026-11-16T10:00:00"),
            Entry("c", "2026-11-15T10:00:00"),
            Entry("d", "2026-11-10T10:00:00"),
            Entry("e", "2026-09-01T10:00:00"),   // 超过 30 天
        };

        var (keep, drop) = CaptureCatalog.PlanPrune(items, now, 3, 30);
        Check.Equal(3, keep.Count, "最多保留 3 条");
        Check.Equal("a,b,c", string.Join(",", keep.Select(e => e.Id)), "按时间倒序保留最新的三条");
        Check.Equal("d,e", string.Join(",", drop.Select(e => e.Id)), "超量 / 超龄都进丢弃");

        var (keepAll, dropOld) = CaptureCatalog.PlanPrune(items, now, 200, 30);
        Check.Equal(4, keepAll.Count, "没超量时只丢超龄的");
        Check.Equal("e", string.Join(",", dropOld.Select(e => e.Id)), "9 月那条超龄");

        var (noLimit, dropNone) = CaptureCatalog.PlanPrune(items, now, 200, 0);
        Check.Equal(0, dropNone.Count, "天数为 0 = 不按天数剪");

        // 缩略图：长边缩到 240，小图不放大
        var big = CaptureCatalog.ThumbnailSize(1200, 800, 240);
        Check.Equal(240, big.Width, "横图缩到长边 240");
        Check.Equal(160, big.Height, "高度按比例");
        var tall = CaptureCatalog.ThumbnailSize(1000, 3000, 240);
        Check.Equal(80, tall.Width, "竖图宽度按比例");
        Check.Equal(240, tall.Height, "竖图缩到长边 240");
        var small = CaptureCatalog.ThumbnailSize(100, 50, 240);
        Check.Equal(100, small.Width, "小图不放大");
        Check.Equal(50, small.Height, "小图高度不变");
        var empty = CaptureCatalog.ThumbnailSize(0, 50, 240);
        Check.Equal(0, empty.Width, "空尺寸返回 0");
        Check.Equal(0, empty.Height, "空尺寸返回 0");

        // 文案
        Check.Equal("截图", CaptureCatalog.KindLabel(CaptureCatalog.KindShot), "shot 的中文名");
        Check.Equal("识别", CaptureCatalog.KindLabel(CaptureCatalog.KindRecognize), "recognize 的中文名");
        Check.Equal("1200 × 800", CaptureCatalog.FormatSize(1200, 800), "尺寸文案");
        Check.Equal("11-16 11:00",
            CaptureCatalog.FormatTime("2026-11-16T11:00:00", new DateTime(2026, 11, 16)), "今年的时间只写月日时分");
        Check.Equal("2025-11-16 11:00",
            CaptureCatalog.FormatTime("2025-11-16T11:00:00", new DateTime(2026, 11, 16)), "跨年补上年份");

        var shot = Entry("s", "2026-11-16T11:00:00");
        Check.Equal("复制", CaptureCatalog.SummaryOf(shot), "截图摘要 = 完成动作");

        var recognize = Entry("r", "2026-11-16T11:00:00", CaptureCatalog.KindRecognize);
        recognize.Text = "第一行文字\r\n第二行";
        Check.Equal("第一行文字", CaptureCatalog.SummaryOf(recognize), "识别摘要 = 第一行");

        recognize.Text = "";
        recognize.State = "pending";
        Check.Equal("识别中…", CaptureCatalog.SummaryOf(recognize), "还没算完的文件说识别中");

        recognize.State = "done";
        Check.Equal("（没有识别到文字）", CaptureCatalog.SummaryOf(recognize), "算完但没字");

        // 长文本截断（列表一行放不下）
        recognize.Text = new string('长', 60);
        Check.True(CaptureCatalog.SummaryOf(recognize).EndsWith("…"), "长文本截断");
    }

    /// <summary>
    /// 贴图：缩放 / 不透明度步进与夹紧、滚轮缩放的光标锚点、出场落点、
    /// 局部显示的内容偏移与取景框尺寸、成组移动的位移夹取。
    /// **光标下的内容不动**是这里最容易被写成「左上角不动」的一条；
    /// 成组移动写成「各自夹」也看不出来，直到拖到屏幕边上组才散架。
    /// </summary>
}
