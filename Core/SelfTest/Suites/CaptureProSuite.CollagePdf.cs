using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Collage;

namespace LTools.Core.SelfTest;

/// <summary>
/// 拼图转 PDF 的纯逻辑（照上游 <c>collagedocument.cpp</c> 的 PDF 一节 +
/// <c>collagepdfexport.cpp</c>）：纸张 / 内容区 / 模板格 / 校验 / 页数 / 单图页面布局 /
/// 合成页 / 文档里的 PDF 设置与手摆，以及真正写出来的 PDF 字节。
/// </summary>
/// <remarks>
/// 窗口本体（PDF 表单可见性、页面网格手势、模板画布拖图、导出进度与取消）在仓库外冒烟里跑。
/// 这里的导出断言会把文件写进临时目录、读完就删。
/// </remarks>
internal static partial class CaptureProSuite
{
    // ============ 纸张 / 内容区 / 模板格 ============

    private static void CollagePdfPaperCases()
    {
        var defaults = CollagePdfSettings.Default;
        Check.Equal(new CollagePdfSize(210, 297), CollagePdfGeometry.PaperMillimeters(defaults),
            "A4 纵向是 210 × 297 毫米");
        Check.Equal(new CollagePdfSize(297, 420),
            CollagePdfGeometry.PaperMillimeters(defaults with { Paper = CollagePdfPaper.A3 }),
            "A3 纵向 297 × 420");
        Check.Equal(new CollagePdfSize(148, 210),
            CollagePdfGeometry.PaperMillimeters(defaults with { Paper = CollagePdfPaper.A5 }),
            "A5 纵向 148 × 210");
        Check.Equal(new CollagePdfSize(176, 250),
            CollagePdfGeometry.PaperMillimeters(defaults with { Paper = CollagePdfPaper.B5 }),
            "B5 纵向 176 × 250");
        Check.Equal(new CollagePdfSize(215.9, 279.4),
            CollagePdfGeometry.PaperMillimeters(defaults with { Paper = CollagePdfPaper.Letter }),
            "Letter 215.9 × 279.4");
        Check.Equal(new CollagePdfSize(215.9, 355.6),
            CollagePdfGeometry.PaperMillimeters(defaults with { Paper = CollagePdfPaper.Legal }),
            "Legal 215.9 × 355.6");
        Check.Equal(new CollagePdfSize(100, 200),
            CollagePdfGeometry.PaperMillimeters(defaults with
            {
                Paper = CollagePdfPaper.Custom,
                CustomWidthMm = 100,
                CustomHeightMm = 200,
            }), "自定义纸张读宽度 / 高度");
        Check.Equal(new CollagePdfSize(297, 210),
            CollagePdfGeometry.PaperMillimeters(defaults with
            {
                Orientation = CollagePdfOrientation.Landscape,
            }), "横向把纸转过来");
        Check.False(CollagePdfGeometry.PaperMillimeters(defaults with
        {
            Paper = CollagePdfPaper.ImageSize,
        }).IsValid, "图像大小 + 分页拼接没有固定纸张");
        Check.Equal(new CollagePdfSize(210, 297),
            CollagePdfGeometry.PaperMillimeters(defaults with
            {
                Paper = CollagePdfPaper.ImageSize,
                Method = CollagePdfMethod.TemplateBased,
            }), "图像大小 + 模板拼接仍按 A4 画布");

        Check.Equal(new CollagePdfRect(10, 10, 190, 277),
            CollagePdfGeometry.ContentMillimeters(defaults), "内容区 = 纸张减四边页边距");
        Check.Equal(new CollagePdfRect(5, 8, 287, 404),
            CollagePdfGeometry.ContentMillimeters(defaults with
            {
                Paper = CollagePdfPaper.A3,
                MarginTopMm = 8,
                MarginBottomMm = 8,
                MarginLeftMm = 5,
                MarginRightMm = 5,
            }), "四边页边距各算各的");

        // ---- 模板格：两张图像 = 一列两行，间距 5 ----
        var two = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
        });
        Check.Equal(2, two.Count, "两张图像模板有 2 格");
        Check.Equal(new CollagePdfRect(10, 10, 190, 136), two[0], "第 1 格贴内容区上边");
        Check.Equal(new CollagePdfRect(10, 151, 190, 136), two[1], "第 2 格让出 5 毫米间距");

        var noGap = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
            ImageSpacingMm = 0,
        });
        Check.Equal(138.5, noGap[0].Height, "间距 0 时两格平分内容区高度");
        Check.Equal(148.5, noGap[1].Y, "间距 0 时第 2 格紧贴第 1 格");

        var nine = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
            Template = CollagePdfTemplate.NineImages,
        });
        Check.Equal(9, nine.Count, "九张图像模板有 9 格");
        Check.Equal(60, nine[0].Width, "三列时每格宽 (190 - 10) / 3");
        Check.Equal(89, nine[0].Height, "三行时每格高 (277 - 10) / 3");
        Check.Equal(10, nine[0].X, "第 1 行第 1 列从内容区左边起");
        Check.Equal(75, nine[1].X, "第二列右移一格宽 + 间距");

        var landscape = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
            Orientation = CollagePdfOrientation.Landscape,
        });
        Check.Equal(2, landscape.Count, "横向两张图像也是 2 格");
        Check.Equal(new CollagePdfRect(10, 10, 136, 190), landscape[0], "横向时两格左右并排");
        Check.Equal(new CollagePdfRect(151, 10, 136, 190), landscape[1], "横向第二格在右边");

        var staggered = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
            Template = CollagePdfTemplate.FiveImages,
            Orientation = CollagePdfOrientation.Landscape,
        });
        Check.Equal(5, staggered.Count, "五张图像模板有 5 格");
        Check.True(staggered.All(cell => cell.IsValid), "错落版式每格都有效");
        Check.True(staggered.All(cell => cell.Right <= 287.001 && cell.Bottom <= 200.001),
            "错落版式都落在内容区里");

        // ---- 身份证：卡面比例 85.6 : 53.98 ----
        var idCard = CollagePdfGeometry.TemplateCells(defaults with
        {
            Method = CollagePdfMethod.TemplateBased,
            Template = CollagePdfTemplate.IdCard,
        });
        Check.Equal(2, idCard.Count, "身份证模板两张卡面");
        double ratio = idCard[0].Width / idCard[0].Height;
        Check.True(Math.Abs(ratio - 85.60 / 53.98) < 1e-9, "卡面保持 85.6 : 53.98");
        Check.True(idCard[0].Width <= 0.41 * 210 + 1e-9, "卡面宽不超过短边的 41%");
        Check.True(idCard[1].Y > idCard[0].Bottom, "两张卡面上下分开");

        // ---- 模板容量 / 预览格 ----
        Check.Equal(2, CollagePdfTemplates.Capacity(CollagePdfTemplate.TwoImages), "两张模板容量 2");
        Check.Equal(2, CollagePdfTemplates.Capacity(CollagePdfTemplate.IdCard), "身份证模板容量 2");
        Check.Equal(9, CollagePdfTemplates.Capacity(CollagePdfTemplate.NineImages), "九张模板容量 9");
        Check.Equal(0, CollagePdfTemplates.Capacity((CollagePdfTemplate)99), "认不出的模板容量 0");
        Check.Equal(9, CollagePdfTemplates.All.Count, "模板按钮一共 9 个");
        Check.Equal("身份证", CollagePdfTemplates.LabelOf(CollagePdfTemplate.IdCard), "身份证的中文名");
        Check.Equal("两张图像", CollagePdfTemplates.LabelOf(CollagePdfTemplate.TwoImages),
            "默认模板的中文名");

        var previewTwo = CollagePdfTemplates.PreviewCells(CollagePdfTemplate.TwoImages);
        Check.Equal(2, previewTwo.Count, "预览里两张模板是 2 格");
        Check.Equal(1.0, previewTwo[0].W, "预览格占满宽度");
        Check.True(Math.Abs(previewTwo[0].H - (1 - 0.065) / 2) < 1e-9, "预览格之间留 0.065 的缝");
        Check.Equal(0, CollagePdfTemplates.PreviewCells(CollagePdfTemplate.IdCard).Count,
            "身份证预览格由按钮自己画卡面");
        Check.Equal(9, CollagePdfTemplates.PreviewCells(CollagePdfTemplate.NineImages).Count,
            "九张模板预览 9 格");

        Check.Equal("210 × 297 毫米", CollagePdfGeometry.DescribeMillimeters(new CollagePdfSize(210, 297)),
            "毫米尺寸的写法");
        Check.Equal("", CollagePdfGeometry.DescribeMillimeters(default(CollagePdfSize)),
            "无效尺寸不给文字");
        Check.Equal("100 × 50 像素", CollagePdfGeometry.DescribePixels(100, 50), "像素尺寸的写法");
    }

    // ============ 页数 / 校验 / 单图布局 / 合成页 ============

    private static void CollagePdfGeometryCases()
    {
        var defaults = CollagePdfSettings.Default;
        var template = defaults with { Method = CollagePdfMethod.TemplateBased };

        Check.Equal(0, CollagePdfGeometry.PageCount(0, defaults), "没有图就没有页");
        Check.Equal(5, CollagePdfGeometry.PageCount(5, defaults), "分页拼接一张一页");
        Check.Equal(3, CollagePdfGeometry.PageCount(5, template), "两张模板 5 张图 → 3 页");
        Check.Equal(2, CollagePdfGeometry.PageCount(4, template), "两张模板 4 张图 → 2 页");
        Check.Equal(1, CollagePdfGeometry.PageCount(9, template with
        {
            Template = CollagePdfTemplate.NineImages,
        }), "九张模板 9 张图 → 1 页");
        Check.Equal(0, CollagePdfGeometry.PageCount(3, template with
        {
            Template = (CollagePdfTemplate)99,
        }), "模板认不出 → 0 页");

        // ---- 校验：默认没有错 ----
        Check.Equal(0, CollagePdfGeometry.Validate(defaults).Count, "默认设置校验通过");

        var negative = CollagePdfGeometry.Validate(defaults with { MarginTopMm = -1 });
        Check.Equal("请输入非负的页边距", negative.GetValueOrDefault("margin_top_mm"),
            "负页边距给出提示");

        var eaten = CollagePdfGeometry.Validate(defaults.WithMargin(300));
        Check.Equal("页边距必须留出可用的内容区域", eaten.GetValueOrDefault("margin_left_mm"),
            "页边距吃掉宽度也说清楚");
        Check.True(eaten.ContainsKey("margin_top_mm"), "页边距吃掉高度也标出来");

        var zeroWidth = CollagePdfGeometry.Validate(defaults with
        {
            Paper = CollagePdfPaper.Custom,
            CustomWidthMm = 0,
        });
        Check.Equal("请输入正数的纸张尺寸", zeroWidth.GetValueOrDefault("custom_width_mm"),
            "自定义宽度必须为正");

        var oversized = CollagePdfGeometry.Validate(defaults with
        {
            Paper = CollagePdfPaper.Custom,
            CustomWidthMm = CollagePdfGeometry.MaximumPdfDimensionMm + 1,
        });
        Check.Equal("纸张尺寸超过了 PDF 大小限制", oversized.GetValueOrDefault("custom_width_mm"),
            "超过 PDF 上限要拦下");

        var spacing = CollagePdfGeometry.Validate(template with { ImageSpacingMm = 1000 });
        Check.Equal("图像间距必须为模板区域留出可用空间",
            spacing.GetValueOrDefault("image_spacing_mm"), "间距太大挤没模板区域");

        Check.True(CollagePdfGeometry.Validate(defaults with { ImageQuality = 101 })
            .ContainsKey("image_quality"), "质量超出 0–100 要提示");
        Check.True(CollagePdfGeometry.Validate(defaults with
        {
            Paper = (CollagePdfPaper)99,
        }).ContainsKey("paper_size"), "认不出的纸张要提示");
        Check.True(CollagePdfGeometry.Validate(defaults with
        {
            Orientation = (CollagePdfOrientation)99,
        }).ContainsKey("orientation"), "认不出的方向要提示");
        Check.True(CollagePdfGeometry.Validate(defaults with
        {
            Method = (CollagePdfMethod)99,
        }).ContainsKey("stitching_method"), "认不出的拼接方式要提示");

        Check.Equal(0, CollagePdfGeometry.Validate(defaults with
        {
            Paper = CollagePdfPaper.ImageSize,
            MarginTopMm = double.NaN,
            CustomWidthMm = double.NaN,
            ImageSpacingMm = double.NaN,
        }).Count, "图像大小 + 分页拼接不看页边距 / 自定义尺寸 / 间距");

        // ---- 单图页面布局：等比放进内容区并居中 ----
        var layout = CollagePdfGeometry.PageLayout((1000, 500), defaults);
        Check.True(layout.IsValid, "一张 1000×500 能排出 A4 页面");
        Check.Equal(new CollagePdfSize(210, 297), layout.PageMillimeters, "纸张仍是 A4");
        Check.Equal(new CollagePdfRect(10, 10, 190, 277), layout.ContentMillimeters, "内容区不变");
        Check.True(Math.Abs(layout.ImageMillimeters.Width - 190) < 1e-9, "宽度顶满内容区");
        Check.True(Math.Abs(layout.ImageMillimeters.Height - 95) < 1e-9, "高度按原比例 95");
        Check.True(Math.Abs(layout.ImageMillimeters.Y - 101) < 1e-9, "垂直居中 (277 - 95) / 2 + 10");

        var imageSize = CollagePdfGeometry.PageLayout((1000, 500), defaults with
        {
            Paper = CollagePdfPaper.ImageSize,
        });
        Check.True(imageSize.IsValid, "图像大小纸张有效");
        Check.True(Math.Abs(imageSize.PageMillimeters.Width - 1000 * 25.4 / 96) < 1e-9,
            "宽按 96 DPI 换成毫米");
        Check.True(Math.Abs(imageSize.PageMillimeters.Height - 500 * 25.4 / 96) < 1e-9,
            "高按 96 DPI 换成毫米");
        Check.Equal(imageSize.PageMillimeters.Width, imageSize.ImageMillimeters.Width,
            "图像铺满整张纸的宽");
        Check.Equal(imageSize.PageMillimeters.Height, imageSize.ImageMillimeters.Height,
            "图像铺满整张纸的高");

        Check.Equal("无法准备图像以供导出为 PDF",
            CollagePdfGeometry.PageLayout((0, 500), defaults).Error, "没有像素就明说准备不了");
        var broken = CollagePdfGeometry.PageLayout((100, 100), defaults with { MarginTopMm = -1 });
        Check.Equal("PDF 页面设置无效", broken.Error, "设置不通过时不排版");
        Check.True(broken.FieldErrors.ContainsKey("margin_top_mm"), "排不了也带回字段提示");

        // ---- 合成页：分页拼接 ----
        var snapshot = new List<CollagePdfItem>
        {
            new(11, 0, 1000, 500, default),
            new(12, 1, 500, 1000, default),
        };

        var page0 = CollagePdfGeometry.ComposedPage(snapshot, defaults, 0);
        Check.True(page0.IsValid, "第 1 页有效");
        Check.Equal(1, page0.Cells.Count, "分页拼接一页一格");
        Check.Equal(11L, page0.Cells[0].ItemId, "第 1 页放第 1 张");
        Check.Equal(0, page0.Cells[0].ItemIndex, "第 1 页的图下标是 0");
        Check.Equal(page0.ContentMillimeters, page0.Cells[0].ClipMillimeters, "分页拼接直接裁到内容区");

        var page1 = CollagePdfGeometry.ComposedPage(snapshot, defaults, 1);
        Check.Equal(12L, page1.Cells[0].ItemId, "第 2 页放第 2 张");
        Check.False(CollagePdfGeometry.ComposedPage(snapshot, defaults, 2).IsValid,
            "越界的页无效");
        Check.False(CollagePdfGeometry.ComposedPage(Array.Empty<CollagePdfItem>(), defaults, 0).IsValid,
            "没有图时合成页无效");

        // ---- 合成页：模板拼接 ----
        var four = new List<CollagePdfItem>
        {
            new(21, 0, 200, 100, default),
            new(22, 1, 200, 100, default),
            new(23, 2, 200, 100, default),
            new(24, 3, 200, 100, default),
        };

        var firstPage = CollagePdfGeometry.ComposedPage(four, template, 0);
        Check.True(firstPage.IsValid, "模板第 1 页有效");
        Check.Equal(2, firstPage.Cells.Count, "两张模板一页两格");
        Check.Equal(21L, firstPage.Cells[0].ItemId, "第 1 格放第 1 张");
        Check.Equal(22L, firstPage.Cells[1].ItemId, "第 2 格放第 2 张");
        Check.True(Math.Abs(firstPage.Cells[0].ImageMillimeters.Width - 190) < 1e-9,
            "200×100 等比放进 190×136 的格子 → 宽 190");
        Check.True(Math.Abs(firstPage.Cells[0].ImageMillimeters.Height - 95) < 1e-9,
            "高度跟着比例 95");

        var secondPage = CollagePdfGeometry.ComposedPage(four, template, 1);
        Check.Equal(23L, secondPage.Cells[0].ItemId, "第 2 页从第 3 张起");
        Check.Equal(24L, secondPage.Cells[1].ItemId, "第 2 页第 2 格是第 4 张");

        var half = new List<CollagePdfItem> { new(31, 0, 100, 100, default) };
        var partial = CollagePdfGeometry.ComposedPage(half, template, 0);
        Check.Equal(-1, partial.Cells[1].ItemIndex, "没图的格子记 -1");
        Check.Equal(0L, partial.Cells[1].ItemId, "空格的 id 是 0");

        var moved = new List<CollagePdfItem>
        {
            new(41, 0, 100, 100, new CollagePdfPlacement(10, 5, 0.5)),
        };
        var movedPage = CollagePdfGeometry.ComposedPage(moved, template, 0);
        Check.True(Math.Abs(movedPage.Cells[0].ImageMillimeters.Width - 50) < 1e-9,
            "显式比例 0.5 → 100 像素宽变成 50 毫米");
        Check.True(Math.Abs(movedPage.Cells[0].ImageMillimeters.CenterX -
                            (movedPage.Cells[0].ClipMillimeters.CenterX + 10)) < 1e-9,
            "手摆偏移把图中心右移 10 毫米");
        Check.True(Math.Abs(movedPage.Cells[0].ImageMillimeters.CenterY -
                            (movedPage.Cells[0].ClipMillimeters.CenterY + 5)) < 1e-9,
            "手摆偏移把图中心下移 5 毫米");

        var badPlacement = new List<CollagePdfItem>
        {
            new(42, 0, 100, 100, new CollagePdfPlacement(double.NaN, 0, null)),
        };
        Check.False(CollagePdfGeometry.ComposedPage(badPlacement, template, 0).IsValid,
            "手摆非有限 → 这一页无效");

        // ---- 文档里的 PDF 设置 / 手摆 ----
        CollagePdfDocumentCases();
    }

    private static void CollagePdfDocumentCases()
    {
        var template = CollagePdfSettings.Default with { Method = CollagePdfMethod.TemplateBased };
        var document = new CollageDocument();
        document.SetPdfSettings(template);
        long first = document.Append(CollageImage(100, 50, (_, _) => Colors.Red), "a");
        long second = document.Append(CollageImage(100, 50, (_, _) => Colors.Blue), "b");
        long third = document.Append(CollageImage(100, 50, (_, _) => Colors.Green), "c");

        Check.Equal(CollageOutputType.StaticImage, document.OutputType, "默认输出静态图像");
        Check.True(document.SetOutputType(CollageOutputType.PdfDocument), "切到 PDF 文档");
        Check.Equal(CollageOutputType.PdfDocument, document.OutputType, "输出类型记下来了");
        Check.False(document.SetOutputType(CollageOutputType.PdfDocument), "再切一次不动");
        Check.False(document.SetOutputType((CollageOutputType)99), "认不出的输出类型拒绝");

        // ---- 只有校验通过的设置才更新「上一次有效」 ----
        Check.Equal(template, document.LastValidPdfSettings, "设置有效就立刻生效");
        var broken = template with { ImageSpacingMm = double.NaN };
        Check.True(document.SetPdfSettings(broken), "填错的东西也记下来（界面要显示哪一项错）");
        Check.Equal(broken, document.PdfSettings, "当前设置是用户填的那份");
        Check.Equal(template, document.LastValidPdfSettings, "校验不过不动上一次有效的设置");

        // ---- 手摆：比例 / 偏移都得有限且大于 0 ----
        Check.True(document.SetPdfPlacement(first, new CollagePdfPlacement(6, -4, 1.5)),
            "记下手的摆放");
        Check.Equal(new CollagePdfPlacement(6, -4, 1.5), document.Items[0].PdfPlacement,
            "手摆写进图里");
        Check.False(document.SetPdfPlacement(first, new CollagePdfPlacement(double.NaN, 0, null)),
            "非有限的偏移拒绝");
        Check.False(document.SetPdfPlacement(first, new CollagePdfPlacement(0, 0, 0)),
            "比例必须大于 0");
        Check.False(document.SetPdfPlacement(first, new CollagePdfPlacement(0, 0, double.MaxValue)),
            "比例乘像素溢出拒绝");
        Check.False(document.SetPdfPlacement(9999, new CollagePdfPlacement(0, 0, 1)),
            "不存在的图拒绝");

        // ---- 只改导出模式 / 质量保住手摆，几何变了才清 ----
        Check.True(document.SetPdfSettings(template with { ImageQuality = 60 }), "只改质量");
        Check.False(document.Items[0].PdfPlacement.IsDefault, "只改质量不动手摆");
        Check.True(document.SetPdfSettings(template with { Paper = CollagePdfPaper.A3 }), "换纸张");
        Check.True(document.Items[0].PdfPlacement.IsDefault, "几何变了清空手摆");
        Check.True(document.SetPdfSettings(template), "换回 A4");
        Check.True(document.SetPdfPlacement(first, new CollagePdfPlacement(6, -4, 1.5)), "再摆一次");

        // ---- 重置变换：旋转 / 翻转 / 手摆一起清 ----
        document.Rotate(first, true);
        Check.True(document.ResetTransform(first), "重置变换");
        Check.True(document.Items[0].PdfPlacement.IsDefault, "重置把 PDF 手摆也清掉");

        // ---- 转 / 翻前固定模板比例（照上游 freezePdfScale） ----
        Check.True(document.Items[0].PdfPlacement.IsDefault, "还没手摆过");
        Check.True(document.Rotate(first, true), "顺时针转 90°");
        var frozen = document.Items[0].PdfPlacement;
        Check.True(frozen.MillimetersPerPixel.HasValue, "转之前固定了毫米 / 像素比例");
        Check.True(Math.Abs(frozen.MillimetersPerPixel!.Value - 1.9) < 1e-9,
            "100×50 放进 190×136 的格子 → 1.9 毫米 / 像素");
        Check.True(frozen.CenterOffsetXMm == 0 && frozen.CenterOffsetYMm == 0, "只固定比例不改偏移");

        document.Flip(first, true);
        Check.True(Math.Abs(document.Items[0].PdfPlacement.MillimetersPerPixel!.Value - 1.9) < 1e-9,
            "翻转沿用同一比例（不重新按格子算）");

        // ---- 分页拼接不固定比例 ----
        var pageDocument = new CollageDocument();
        pageDocument.Append(CollageImage(100, 50, (_, _) => Colors.Red), "a");
        pageDocument.Rotate(pageDocument.Items[0].Id, true);
        Check.True(pageDocument.Items[0].PdfPlacement.IsDefault,
            "分页拼接没有模板格，转的时候不固定比例");

        // ---- 交换 / 反转 / 删除都清手摆（照上游 resetPlacements） ----
        void Place(long id, double x) =>
            document.SetPdfPlacement(id, new CollagePdfPlacement(x, 0, 1));

        Place(first, 1);
        Place(second, 2);
        Place(third, 3);
        document.Swap(first, third);
        Check.True(document.Find(first)!.PdfPlacement.IsDefault, "交换后第一张的手摆清掉");
        Check.True(document.Find(third)!.PdfPlacement.IsDefault, "交换后第三张的手摆清掉");
        Check.False(document.Find(second)!.PdfPlacement.IsDefault, "没交换的第二张保住手摆");

        document.Reverse();
        Check.True(document.Items[0].PdfPlacement.IsDefault, "反转清掉首端的手摆");
        Check.True(document.Items[^1].PdfPlacement.IsDefault, "反转清掉末端的手摆");

        document.Remove(document.Items[0].Id);
        Check.True(document.Items.All(item => item.PdfPlacement.IsDefault),
            "删除之后（含被删的那张之后）手摆全清");
    }

    // ============ 导出的真实字节 ============

    private static void CollagePdfExportCases()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "ltools-selftest-collagepdf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var red = new CollageItem(1, CollageImage(100, 50, (_, _) => Colors.Red), "a<b>:c");
            var blue = new CollageItem(2, CollageImage(100, 50, (_, _) => Colors.Blue), "b");
            var items = new List<CollageItem> { red, blue };
            var defaults = CollagePdfSettings.Default;

            // ---- 合并导出：一个文件两页 ----
            string combined = Path.Combine(directory, "result");
            var result = CollagePdfExport.Export(combined, items, defaults);
            Check.True(result.Succeeded, "合并导出成功");
            Check.Equal(2, result.TotalCount, "两张图两页");
            Check.Equal("1,2", string.Join(",", result.CompletedPages), "两页都写完");
            Check.Equal(1, result.Paths.Count, "合并导出只写一个文件");
            Check.True(result.Paths[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase),
                "没写扩展名也补上 .pdf");
            Check.True(File.Exists(result.Paths[0]), "文件真的在");

            string text = Encoding.Latin1.GetString(File.ReadAllBytes(result.Paths[0]));
            Check.True(text.StartsWith("%PDF-1.7", StringComparison.Ordinal), "PDF 1.7 文件头");
            Check.True(text.Contains("/Count 2", StringComparison.Ordinal), "页树说有两页");
            Check.Equal(2, CountOf(text, "/Type /Page /Parent"), "两个页面对象");
            Check.True(text.Contains("/MediaBox [0 0 595.275591 841.889764]", StringComparison.Ordinal),
                "A4 的 MediaBox 按 72 点 / 英寸换算");
            Check.True(text.Contains("/UserUnit 1", StringComparison.Ordinal), "普通纸张写 UserUnit 1");
            Check.True(text.Contains("/Filter /FlateDecode", StringComparison.Ordinal),
                "质量 100 走无损 Flate");
            Check.False(text.Contains("/DCTDecode", StringComparison.Ordinal), "质量 100 不写 JPEG");
            Check.True(text.Contains("/Title <FEFF", StringComparison.Ordinal), "标题按 UTF-16 写");
            Check.True(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "文件以 %%EOF 收尾");
            Check.Equal(0, Directory.GetFiles(directory, "*.tmp").Length, "没留下临时文件");

            // ---- 逐页导出：001_名称.pdf，重名追加 _2 ----
            string pages = Path.Combine(directory, "pages");
            var three = new List<CollageItem>
            {
                new(1, CollageImage(100, 50, (_, _) => Colors.Red), "a<b>:c"),
                new(2, CollageImage(100, 50, (_, _) => Colors.Blue), "b"),
                new(3, CollageImage(100, 50, (_, _) => Colors.Green), "c"),
            };
            var perPage = CollagePdfExport.Export(pages, three, defaults with
            {
                ExportMode = CollagePdfExportMode.PerPage,
            });
            Check.True(perPage.Succeeded, "逐页导出成功");
            Check.Equal(3, perPage.CompletedPages.Count, "逐页导出三页");
            Check.True(File.Exists(Path.Combine(pages, "001_a_b__c.pdf")), "001 用清洗过的文件名");
            Check.True(File.Exists(Path.Combine(pages, "002_b.pdf")), "002 按顺序编号");
            Check.True(File.Exists(Path.Combine(pages, "003_c.pdf")), "003 按顺序编号");

            var again = CollagePdfExport.Export(pages, three, defaults with
            {
                ExportMode = CollagePdfExportMode.PerPage,
            });
            Check.True(again.Succeeded, "同一目录再导一次也成功");
            Check.True(File.Exists(Path.Combine(pages, "001_a_b__c_2.pdf")), "重名追加 _2");

            // ---- 模板拼接：两页合成页，逐页文件名用 page ----
            var five = new List<CollageItem>
            {
                new(1, CollageImage(100, 50, (_, _) => Colors.Red), "a"),
                new(2, CollageImage(100, 50, (_, _) => Colors.Blue), "b"),
                new(3, CollageImage(100, 50, (_, _) => Colors.Green), "c"),
                new(4, CollageImage(100, 50, (_, _) => Colors.White), "d"),
                new(5, CollageImage(100, 50, (_, _) => Colors.Black), "e"),
            };
            var templateSettings = defaults with { Method = CollagePdfMethod.TemplateBased };
            string templateFile = Path.Combine(directory, "template.pdf");
            var templated = CollagePdfExport.Export(templateFile, five, templateSettings);
            Check.True(templated.Succeeded, "模板拼接导出成功");
            Check.Equal(3, templated.CompletedPages.Count, "5 张图两张模板 → 3 页");
            Check.True(File.Exists(templateFile), "模板拼接合并写一个文件");
            string templateText = Encoding.Latin1.GetString(File.ReadAllBytes(templateFile));
            Check.True(templateText.Contains("/Count 3", StringComparison.Ordinal),
                "模板拼接写 3 页");

            string templatePages = Path.Combine(directory, "template-pages");
            CollagePdfExport.Export(templatePages, five, templateSettings with
            {
                ExportMode = CollagePdfExportMode.PerPage,
            });
            Check.True(File.Exists(Path.Combine(templatePages, "001_page.pdf")),
                "模板拼接的逐页文件名用 page");

            // ---- 质量 60 走 JPEG；带透明的图补 SMask ----
            string jpegFile = Path.Combine(directory, "jpeg.pdf");
            CollagePdfExport.Export(jpegFile, items, defaults with { ImageQuality = 60 });
            string jpegText = Encoding.Latin1.GetString(File.ReadAllBytes(jpegFile));
            Check.True(jpegText.Contains("/DCTDecode", StringComparison.Ordinal), "质量 60 走 JPEG");
            Check.False(jpegText.Contains("/FlateDecode", StringComparison.Ordinal),
                "质量 60 不写无损图");

            string alphaFile = Path.Combine(directory, "alpha.pdf");
            CollagePdfExport.Export(alphaFile, new List<CollageItem>
            {
                new(1, CollageAlphaImage(20, 20, 128), "alpha"),
            }, defaults);
            string alphaText = Encoding.Latin1.GetString(File.ReadAllBytes(alphaFile));
            Check.True(alphaText.Contains("/SMask", StringComparison.Ordinal),
                "半透明的图写灰阶 SMask");
            Check.True(alphaText.Contains("/ColorSpace /DeviceGray", StringComparison.Ordinal),
                "SMask 是灰阶图");

            // ---- 图像大小纸张：页面直接按 96 DPI 换算（100×50 px → 75×37.5 点）----
            string imageFile = Path.Combine(directory, "imagesize.pdf");
            CollagePdfExport.Export(imageFile, new List<CollageItem> { red }, defaults with
            {
                Paper = CollagePdfPaper.ImageSize,
            });
            string imageText = Encoding.Latin1.GetString(File.ReadAllBytes(imageFile));
            Check.True(imageText.Contains("/MediaBox [0 0 75 37.5]", StringComparison.Ordinal),
                "图像大小纸张 = 像素 × 0.75 点");

            // ---- 超大纸张走 /UserUnit 缩放 ----
            string hugeFile = Path.Combine(directory, "huge.pdf");
            var huge = CollagePdfExport.Export(hugeFile, new List<CollageItem> { red }, defaults with
            {
                Paper = CollagePdfPaper.Custom,
                CustomWidthMm = 1_000_000,
                CustomHeightMm = 100_000,
            });
            Check.True(huge.Succeeded, "超大纸张也能导出");
            string hugeText = Encoding.Latin1.GetString(File.ReadAllBytes(hugeFile));
            Check.True(hugeText.Contains("/UserUnit 197", StringComparison.Ordinal),
                "超过 14400 点时按整数倍写 UserUnit");

            // ---- 取消：一个文件也不留 ----
            string cancelledFile = Path.Combine(directory, "cancelled.pdf");
            var cancelled = CollagePdfExport.Export(cancelledFile, items, defaults, () => true);
            Check.True(cancelled.Cancelled, "取消要标出来");
            Check.False(cancelled.Succeeded, "取消不算成功");
            Check.False(File.Exists(cancelledFile), "取消不写目标文件");

            // ---- 设置无效：不假装成功 ----
            var invalid = CollagePdfExport.Export(Path.Combine(directory, "invalid.pdf"), items,
                defaults.WithMargin(500));
            Check.False(invalid.Succeeded, "设置无效时导出失败");
            Check.Equal("PDF 页面设置无效", invalid.Error, "给出设置无效的原因");
            Check.False(File.Exists(Path.Combine(directory, "invalid.pdf")), "无效设置一个字节也不写");

            Check.True(CollagePdfExport.DefaultName.StartsWith("SnowShot_", StringComparison.Ordinal),
                "默认文件名沿用 SnowShot_ 前缀");
            Check.Equal(28, CollagePdfExport.DefaultName.Length, "默认文件名带日期时间");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 清不干净不影响断言。
            }
        }
    }

    // ============ 像素准备 / 页面绘制 ============

    private static void CollagePdfRenderCases()
    {
        var split = CollageImage(10, 4, (x, _) => x < 5 ? Colors.Red : Colors.Blue);
        var rotated = new CollageItem(1, split, "split") { Transform = CollageTransform.Clockwise };

        var raster = CollagePdfRender.TransformedPixels(rotated);
        Check.True(raster != null, "旋过的图能取到直通像素");
        Check.Equal(4, raster!.Width, "10×4 转 90° 后宽 4");
        Check.Equal(10, raster.Height, "10×4 转 90° 后高 10");
        Check.Equal(Colors.Red, CollageRasterPixel(raster, 3, 0), "源图左半转到右上");
        Check.Equal(Colors.Blue, CollageRasterPixel(raster, 0, 9), "源图右半转到左下");

        var flipped = new CollageItem(1, split, "split")
        {
            Transform = CollageTransform.MirrorHorizontal,
        };
        var mirror = CollagePdfRender.TransformedPixels(flipped);
        Check.Equal(Colors.Blue, CollageRasterPixel(mirror!, 0, 0), "水平翻转后左上变蓝");
        Check.Equal(Colors.Red, CollageRasterPixel(mirror!, 9, 0), "水平翻转后右上变红");

        var empty = new CollageItem(1, CollageImage(1, 1, (_, _) => Colors.Red), "tiny");
        Check.True(CollagePdfRender.TransformedPixels(empty) != null, "1×1 的图也能取到像素");

        // ---- 绘制一页：20×10 毫米的纸，一张两色图正好铺满 ----
        var settings = CollagePdfSettings.Default with
        {
            Paper = CollagePdfPaper.Custom,
            CustomWidthMm = 20,
            CustomHeightMm = 10,
            MarginTopMm = 0,
            MarginBottomMm = 0,
            MarginLeftMm = 0,
            MarginRightMm = 0,
        };
        var twoTone = new CollageItem(1,
            CollageImage(100, 50, (x, _) => x < 50 ? Colors.Red : Colors.Blue), "tone");
        var items = new List<CollageItem> { twoTone };
        var page = CollagePdfGeometry.ComposedPage(CollagePdfRender.Snapshot(items), settings, 0);
        Check.True(page.IsValid, "20×10 的一张图能合成一页");

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            CollagePdfRender.DrawPage(dc, page, items, 4);
        }

        var painted = new RenderTargetBitmap(80, 40, 96, 96, PixelFormats.Pbgra32);
        painted.Render(visual);
        Check.Equal(Colors.Red, CollagePixelAt(painted, 10, 20), "页面左上按原图左半画成红");
        Check.Equal(Colors.Blue, CollagePixelAt(painted, 70, 20), "页面右上按原图右半画成蓝");

        // ---- 模板格子外的部分被裁掉 ----
        var template = settings with { Method = CollagePdfMethod.TemplateBased, ImageSpacingMm = 0 };
        var oversized = new CollageItem(1,
            CollageImage(100, 100, (_, _) => Colors.Red), "big");
        var templateItems = new List<CollageItem> { oversized };
        var templatePage = CollagePdfGeometry.ComposedPage(CollagePdfRender.Snapshot(templateItems),
            template, 0);
        Check.True(templatePage.IsValid, "模板页有效");
        var templateVisual = new DrawingVisual();
        using (var dc = templateVisual.RenderOpen())
        {
            CollagePdfRender.DrawPage(dc, templatePage, templateItems, 4);
        }

        var templatePainted = new RenderTargetBitmap(80, 40, 96, 96, PixelFormats.Pbgra32);
        templatePainted.Render(templateVisual);
        Check.Equal(Colors.White, CollagePixelAt(templatePainted, 79, 39),
            "纸面右下角留白（图只画在格子里）");
        Check.Equal(Colors.Red, CollagePixelAt(templatePainted, 40, 10), "格子里画的是这张图");
    }

    // ============ 小工具 ============

    private static Color CollageRasterPixel(CollagePdfRaster raster, int x, int y)
    {
        int index = (y * raster.Width + x) * 4;
        return Color.FromArgb(raster.Pixels[index + 3], raster.Pixels[index + 2],
            raster.Pixels[index + 1], raster.Pixels[index]);
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int at = 0; ;)
        {
            int found = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (found < 0) return count;
            count++;
            at = found + needle.Length;
        }
    }

    private static BitmapSource CollageAlphaImage(int width, int height, byte alpha)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x00;
            pixels[i + 1] = 0x00;
            pixels[i + 2] = 0xFF;
            pixels[i + 3] = alpha;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels,
            width * 4);
        image.Freeze();
        return image;
    }
}
