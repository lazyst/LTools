using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 采集历史的「当时的整个屏幕」（P9-u，照上游 <c>CaptureHistoryDraft</c> 的 displays / selection）：
/// 截图时把整屏冻结画面一起存下来 → 历史卡片 / 大图都是「成品 + 每块屏幕」的轮播 → 还能用那张画面重新编辑截图。
/// </summary>
/// <remarks>
/// 这里直接喂夹具（不跑真截图会话），验的是存储形状、轮播数据（每块屏从整屏图上裁）、卡片与大图的翻张、
/// 重新编辑截图入口；裁图几何的逐像素规则在 <c>--selftest</c> 的「整屏底图 / 选区还原 / 屏幕预览」一组里断言。
/// 采集目录就是 <see cref="CaptureCatalog"/> 自己的 <c>captures/</c>（与采集历史翻页那段同一个做法）。
/// </remarks>
internal static partial class Program
{
    private static void HistoryDisplaySection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;
        var entryType = asm.GetType("LTools.Features.CapturePro.CaptureEntry")!;
        var dtoType = asm.GetType("LTools.Features.CapturePro.CaptureRegionDto")!;
        var slidesType = asm.GetType("LTools.Views.CapturePro.HistorySlides")!;
        var slideType = asm.GetType("LTools.Views.CapturePro.HistorySlide")!;
        var rowType = asm.GetType("LTools.Views.CapturePro.CaptureHistoryRow")!;

        catalogType.GetMethod("Clear")!.Invoke(null, null);

        // ============ 夹具：一张整屏（两块屏）+ 一张成品 + 两块选区 ============
        var display = SolidImage(400, 300, Colors.White);
        var product = SolidImage(100, 60, Colors.Teal);
        var screens = new[] { new Int32Rect(0, 0, 200, 300), new Int32Rect(200, 0, 200, 300) };

        object Region(string mode, double x, double y, double w, double h)
        {
            var dto = Activator.CreateInstance(dtoType)!;
            dtoType.GetProperty("Mode")!.SetValue(dto, mode);
            dtoType.GetProperty("X")!.SetValue(dto, x);
            dtoType.GetProperty("Y")!.SetValue(dto, y);
            dtoType.GetProperty("Width")!.SetValue(dto, w);
            dtoType.GetProperty("Height")!.SetValue(dto, h);
            return dto;
        }

        var entry = catalogType.GetMethod("Create")!.Invoke(null,
            new object?[] { 100, 60, "shot", "复制", null, new Int32Rect(0, 0, 400, 300), screens, null })!;
        catalogType.GetMethod("SaveAssets")!.Invoke(null, new[] { entry, product });
        catalogType.GetMethod("SaveDisplay")!.Invoke(null, new[] { entry, display });

        var regions = (IList)entryType.GetProperty("Regions")!.GetValue(entry)!;
        regions.Add(Region("add", 100, 80, 200, 120));
        regions.Add(Region("add", 260, 140, 60, 60));      // 与第一块重叠（多块）

        var stored = (IList)entryType.GetProperty("Screens")!.GetValue(entry)!;
        Check(stored.Count == 2, $"每块屏幕的矩形都进 JSON（{stored.Count} 块）");

        string id = (string)entryType.GetProperty("Id")!.GetValue(entry)!;
        string displayPath = (string)catalogType.GetMethod("DisplayPath", Stat)!
            .Invoke(null, new object[] { id })!;
        Check((bool)entryType.GetProperty("HasDisplay")!.GetValue(entry)!, "条目认得出自己有整屏底图");
        Check((Int32Rect)entryType.GetProperty("DisplayRect")!.GetValue(entry)! ==
              new Int32Rect(0, 0, 400, 300), "整屏矩形 = 记下的那一块");
        Check(File.Exists(displayPath), "整屏底图落盘在 captures/displays/");
        Check(JsonSerializer.Serialize(regions).Contains("\"add\""), "每一块选区都进 JSON");

        // ============ ① 轮播数据：第一张成品图，后面每块屏幕一张（从整屏图上裁） ============
        var loaded = (BitmapSource)catalogType.GetMethod("LoadImage", Stat)!
            .Invoke(null, new object?[] { displayPath, 0 })!;

        var slides = (IList)slidesType.GetMethod("Load", Stat)!
            .Invoke(null, new object?[] { entry, 520, null })!;
        Check(slides.Count == 3, $"成品图 + 两块屏幕 = 3 张（实际 {slides.Count}）");

        string LabelOf(object slide) => (string)slideType.GetProperty("Label")!.GetValue(slide)!;
        BitmapSource? ImageOf(object slide) =>
            (BitmapSource?)slideType.GetProperty("Image")!.GetValue(slide);

        Check(LabelOf(slides[0]!) == "成品图", "第一张是成品图：" + LabelOf(slides[0]!));
        Check(ImageOf(slides[0]!)?.PixelWidth == 100, "第一张就是裁出来的成品");
        Check(LabelOf(slides[1]!) == "屏幕 1", "第二张是屏幕 1：" + LabelOf(slides[1]!));
        Check(LabelOf(slides[2]!) == "屏幕 2", "第三张是屏幕 2：" + LabelOf(slides[2]!));
        Check(ImageOf(slides[1]!)?.PixelWidth == 200 && ImageOf(slides[1]!)?.PixelHeight == 300,
            $"每块屏幕按自己的矩形裁（{ImageOf(slides[1]!)?.PixelWidth} × {ImageOf(slides[1]!)?.PixelHeight}）");

        // 屏幕是原始画面：不压暗（整张白），也不画选框
        var screenPixels = new byte[200 * 300 * 4];
        ImageOf(slides[1]!)!.CopyPixels(screenPixels, 200 * 4, 0);
        Check(screenPixels[(150 * 200 + 100) * 4 + 2] == 255,
            $"屏幕预览不压暗（R = {screenPixels[(150 * 200 + 100) * 4 + 2]}）");

        // ============ ② 卡片：翻张 + 角标（多张才露箭头） ============
        var row = Activator.CreateInstance(rowType, new object?[] { entry, null, 0L, slides })!;
        string Chip() => (string)rowType.GetProperty("SlideChipText")!.GetValue(row)!;
        BitmapSource? Thumb() => (BitmapSource?)rowType.GetProperty("Thumb")!.GetValue(row);

        Check((bool)rowType.GetProperty("HasMultipleSlides")!.GetValue(row)!, "多张预览时露箭头");
        Check(Chip() == "成品图  1/3", "卡片角标：成品图 1/3（" + Chip() + "）");
        Check(Thumb()?.PixelWidth == 100, "卡片一开始是成品图");

        rowType.GetMethod("NextSlide")!.Invoke(row, null);
        Check(Chip() == "屏幕 1  2/3", "翻到屏幕 1（" + Chip() + "）");
        Check(Thumb()?.PixelWidth == 200, "翻过去以后显示那块屏幕的裁图");

        rowType.GetMethod("NextSlide")!.Invoke(row, null);
        Check(Chip() == "屏幕 2  3/3", "再翻到屏幕 2（" + Chip() + "）");

        rowType.GetMethod("NextSlide")!.Invoke(row, null);
        Check(Chip() == "成品图  1/3", "翻到头回到第一张（循环）");

        rowType.GetMethod("PreviousSlide")!.Invoke(row, null);
        Check(Chip() == "屏幕 2  3/3", "往回翻从最后一张接上（循环）");

        // 单张预览（老记录没有整屏）：不露箭头，角标只写「成品图」
        var legacy = catalogType.GetMethod("Create")!.Invoke(null,
            new object?[] { 100, 60, "shot", "复制", null, null, null, null })!;
        catalogType.GetMethod("SaveAssets")!.Invoke(null, new[] { legacy, product });

        var legacySlides = (IList)slidesType.GetMethod("Load", Stat)!
            .Invoke(null, new object?[] { legacy, 520, null })!;
        var legacyRow = Activator.CreateInstance(rowType, new object?[] { legacy, null, 0L, legacySlides })!;
        Check(legacySlides.Count == 1, "没有整屏底图时只有一张预览");
        Check(!(bool)rowType.GetProperty("HasMultipleSlides")!.GetValue(legacyRow)!, "单张不露箭头");
        Check((string)rowType.GetProperty("SlideChipText")!.GetValue(legacyRow)! == "成品图",
            "单张时角标不写序号");

        // ============ ③ 大图查看器：同一组轮播（箭头 / 角标 / ←→ 翻张） ============
        var viewerType = asm.GetType("LTools.Views.CapturePro.CaptureViewerWindow")!;
        var viewer = (Window)Activator.CreateInstance(viewerType, true)!;
        viewer.Show();
        Pump(viewer);
        viewerType.GetMethod("Present", Inst)!.Invoke(viewer, new object[] { entry, 0 });
        Pump(viewer);

        var prevBtn = viewer.FindName("PrevSlideBtn") as Button;
        var nextBtn = viewer.FindName("NextSlideBtn") as Button;
        var chip = viewer.FindName("SlideChipText") as TextBlock;
        var reopenBtn = viewer.FindName("ReopenBtn") as Button;
        var picture = viewer.FindName("Picture") as Image;

        Check(prevBtn != null && prevBtn.Visibility == Visibility.Visible, "多张预览时露出左箭头");
        Check(nextBtn != null && nextBtn.Visibility == Visibility.Visible, "多张预览时露出右箭头");
        Check(reopenBtn != null && reopenBtn.Visibility == Visibility.Visible, "有整屏底图时给「重新编辑截图…」");
        Check(chip?.Text == "成品图  1/3", "大图角标：成品图 1/3（" + chip?.Text + "）");
        var first = picture?.Source as BitmapSource;
        Check(first != null && first.PixelWidth == 100, $"大图先显示成品图（宽 {first?.PixelWidth}）");

        nextBtn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(viewer);
        var second = picture?.Source as BitmapSource;
        Check(chip?.Text == "屏幕 1  2/3", "点右箭头翻到屏幕 1（" + chip?.Text + "）");
        Check(second != null && second.PixelWidth == 200 && second.PixelHeight == 300,
            $"大图里是那块屏幕（{second?.PixelWidth} × {second?.PixelHeight}）");

        prevBtn!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(viewer);
        Check(chip?.Text == "成品图  1/3", "点左箭头回到成品图（" + chip?.Text + "）");

        // ← → 键也能翻（在图上操作时不用去够箭头）
        viewer.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(viewer)!, 0, Key.Right)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        Pump(viewer);
        Check(chip?.Text == "屏幕 1  2/3", "按 → 翻到下一张（" + chip?.Text + "）");

        // 卡片停在第几张，点开就停在第几张
        viewerType.GetMethod("Present", Inst)!.Invoke(viewer, new object[] { entry, 2 });
        Pump(viewer);
        Check(chip?.Text == "屏幕 2  3/3", "从卡片第 3 张点开就停在屏幕 2（" + chip?.Text + "）");

        Save(Render(viewer), Path.Combine(_outDir, "history-display.png"));
        viewer.Close();

        // ============ ③b 重新编辑截图：整屏 + 选区 + 外观 + 标注（连撤销栈）都能摆回覆盖层 ============
        // 夹具：三条标注（形状 / 文字 / 马赛克）——正好覆盖「枚举按名字」「text → Content」
        // 「吃底图像素」三种落盘口径。
        const string annotationsJson = """
            [
              {"t":"Shape","a":{"a":"10,10","b":"80,60","spec":{"t":"ShapeSpec","a":{
                "kind":"Ellipse","stroke":"#FF2B7CF0","width":5,"strokeStyle":"Dotted",
                "rendering":"Artist","fill":"#00000000","fillStyle":"None","radius":0,"seed":9}}}},
              {"t":"Text","a":{"origin":"12,90","text":"历史","style":{"t":"TextSpec","a":{
                "fontFamily":"Microsoft YaHei UI","fontSize":18,"bold":false,"italic":false,
                "align":"Left","color":"#FF2B7CF0","strokeWidth":0,"stroke":"#00000000",
                "fill":"#00000000","fillStyle":"None","cornerRadius":0}}}},
              {"t":"Mosaic","a":{"a":"120,70","b":"200,120","block":8}}
            ]
            """;

        var codecType = asm.GetType("LTools.Features.CapturePro.MarkCodec")!;
        var markType = asm.GetType("LTools.Features.CapturePro.Mark")!;
        var marks = codecType.GetMethod("Read", Stat)!
            .Invoke(null, new object?[] { JsonNode.Parse(annotationsJson) })!;
        var markCount = ((IEnumerable)marks).Cast<object>().Count();
        Check(markCount == 3, $"夹具里的三条标注都读得回来（{markCount}）");

        var undo = Array.CreateInstance(markType.MakeArrayType(), 1);
        var firstTwo = Array.CreateInstance(markType, 2);
        var markList = ((IEnumerable)marks).Cast<object>().ToArray();
        firstTwo.SetValue(markList[0], 0);
        firstTwo.SetValue(markList[1], 1);
        undo.SetValue(firstTwo, 0);
        var redo = Array.CreateInstance(markType.MakeArrayType(), 0);

        // 选区外观：走落盘形状（CaptureStyleDto，含版面压缩），这才是「重新编辑」读的那一份
        var styleType = asm.GetType("LTools.Features.CapturePro.SelectionStyle")!;
        var styleDtoType = asm.GetType("LTools.Features.CapturePro.CaptureStyleDto")!;
        var styleDto = Activator.CreateInstance(styleDtoType)!;
        styleDtoType.GetProperty("CornerRadius")!.SetValue(styleDto, 12.0);
        styleDtoType.GetProperty("ShadowWidth")!.SetValue(styleDto, 6.0);
        styleDtoType.GetProperty("BorderWidth")!.SetValue(styleDto, 3.0);
        styleDtoType.GetProperty("BackgroundOpacity")!.SetValue(styleDto, 40);
        styleDtoType.GetProperty("CompactLayout")!.SetValue(styleDto, true);
        styleDtoType.GetProperty("CompactSpacing")!.SetValue(styleDto, 18);

        var reopen = NewOverlay(loaded, new System.Drawing.Rectangle(0, 0, 400, 300));
        try
        {
            reopen.Show();
            reopen.Activate();
            Pump(reopen);

            // 覆盖层会按屏幕 DPI 重算文档比例（物理像素 ÷ DIP）：重新编辑截图就用这个比例把
            // 落盘的物理像素换算成 DIP（生产代码里用的是同一份 MonitorScale）
            var reopenDoc = Field(reopen, "_doc")!;
            double scale = (double)reopenDoc.GetType().GetProperty("Scale")!.GetValue(reopenDoc)!;
            Check(scale > 0, $"重新编辑截图时文档比例可用（{scale}）");

            var dipRegions = (IList)Activator.CreateInstance(
                typeof(System.Collections.Generic.List<>).MakeGenericType(
                    asm.GetType("LTools.Features.CapturePro.SelectionRegion")!))!;
            foreach (var item in regions)
            {
                var dip = dtoType.GetMethod("ToDip")!.Invoke(item, new object[] { 0, 0, scale })!;
                dipRegions.Add(dip);
            }

            typeof(CaptureOverlayWindow).GetMethod("LoadSelection", Inst)!
                .Invoke(reopen, new object[] { dipRegions });
            Pump(reopen);

            var reopenRegions = (IList)typeof(CaptureOverlayWindow)
                .GetField("_regions", Inst)!.GetValue(reopen)!;
            var reopenSel = (Rect?)typeof(CaptureOverlayWindow)
                .GetField("_sel", Inst)!.GetValue(reopen);
            Check(reopenRegions.Count == 2, $"重新编辑截图时两块选区都摆回来了（实际 {reopenRegions.Count}）");
            Check(reopenSel is { } box &&
                  Math.Abs(box.X - 100 / scale) < 0.6 && Math.Abs(box.Y - 80 / scale) < 0.6 &&
                  Math.Abs(box.Width - 220 / scale) < 0.6 && Math.Abs(box.Height - 120 / scale) < 0.6,
                $"外接框就是当初那两块并起来（按 {scale} 换算）：{reopenSel}");

            Invoke(reopen, "SyncArea");
            var reopenArea = (Int32Rect)reopenDoc.GetType().GetProperty("Area")!.GetValue(reopenDoc)!;
            Check(Math.Abs(reopenArea.X - 100) <= 2 && Math.Abs(reopenArea.Y - 80) <= 2 &&
                  Math.Abs(reopenArea.Width - 220) <= 2 && Math.Abs(reopenArea.Height - 120) <= 2,
                $"重新编辑截图的成品区域跟当初一致（物理像素）：{reopenArea}");

            // 外观与标注（含撤销栈）
            typeof(CaptureOverlayWindow).GetMethod("LoadCaptureStyle", Inst)!
                .Invoke(reopen, new[] { styleDto });
            typeof(CaptureOverlayWindow).GetMethod("LoadAnnotations", Inst)!
                .Invoke(reopen, new object[] { marks, undo, redo });
            Pump(reopen);

            var reopenDocument = typeof(CaptureOverlayWindow).GetProperty("Document", Inst)!
                .GetValue(reopen)!;
            var restored = (IList)reopenDocument.GetType().GetProperty("Marks")!.GetValue(reopenDocument)!;
            Check(restored.Count == 3, $"标注也摆回来了（{restored.Count} 条）");
            Check((bool)reopenDocument.GetType().GetProperty("CanUndo")!.GetValue(reopenDocument)!,
                "撤销栈跟着回来（能 Ctrl+Z 退回当初）");
            Check(!(bool)reopenDocument.GetType().GetProperty("CanRedo")!.GetValue(reopenDocument)!,
                "重做栈是空的（当初也没 redo）");

            var restoredStyle = reopenDocument.GetType().GetProperty("Style")!.GetValue(reopenDocument)!;
            Check((double)styleType.GetProperty("CornerRadius")!.GetValue(restoredStyle)! == 12.0,
                "选区外观（圆角 12）也摆回来了");
            Check((int)styleType.GetProperty("BackgroundOpacity")!.GetValue(restoredStyle)! == 40,
                "选区外观（背景不透明度 40）也摆回来了");
            Check((bool)reopenDocument.GetType().GetProperty("CompactLayout")!.GetValue(reopenDocument)!,
                "版面压缩开关也摆回来了");
            Check((int)reopenDocument.GetType().GetProperty("CompactSpacing")!.GetValue(reopenDocument)! == 18,
                "压缩留白也摆回来了");

            // ============ ③c 采集条目：标注 / 撤销栈 / 外观跟着落盘 ============
            var editType = asm.GetType("LTools.Features.CapturePro.CaptureEditState")!;
            var edit = editType.GetMethod("Of", Stat)!
                .Invoke(null, new object?[] { reopenDocument, undo, redo })!;
            var editEntry = catalogType.GetMethod("Create")!.Invoke(null,
                new object?[] { 100, 60, "shot", "复制", null, new Int32Rect(0, 0, 400, 300), screens, edit })!;
            catalogType.GetMethod("SaveAssets")!.Invoke(null, new[] { editEntry, product });
            catalogType.GetMethod("SaveDisplay")!.Invoke(null, new[] { editEntry, display });

            string editId = (string)entryType.GetProperty("Id")!.GetValue(editEntry)!;
            var reloaded = ((IEnumerable)catalogType.GetMethod("Load", Stat)!.Invoke(null, null)!)
                .Cast<object>()
                .First(e => (string)entryType.GetProperty("Id")!.GetValue(e)! == editId);

            Check((bool)entryType.GetProperty("HasAnnotations")!.GetValue(reloaded)!,
                "catalog.json 读回来还带着标注");
            Check(((JsonArray)entryType.GetProperty("Annotations")!.GetValue(reloaded)!).Count == 3,
                "三条标注落盘（JsonArray 存进条目）");
            Check(entryType.GetProperty("UndoMarks")!.GetValue(reloaded) != null, "撤销栈也落盘");
            Check(entryType.GetProperty("Style")!.GetValue(reloaded) != null, "选区外观也落盘");

            // 「重新编辑截图」读的就是这几样：从落盘条目再解一遍，标注与外观都得能还原
            var fromDisk = codecType.GetMethod("Read", Stat)!
                .Invoke(null, new object?[] { entryType.GetProperty("Annotations")!.GetValue(reloaded) })!;
            Check(((IEnumerable)fromDisk).Cast<object>().Count() == 3, "落盘的标注能再解出 3 条");
            var fromDiskUndo = codecType.GetMethod("ReadHistory", Stat)!
                .Invoke(null, new object?[] { entryType.GetProperty("UndoMarks")!.GetValue(reloaded) })!;
            Check(((IEnumerable)fromDiskUndo).Cast<object>().Count() == 1, "落盘的撤销栈能再解出 1 份快照");

            var storedStyle = entryType.GetProperty("Style")!.GetValue(reloaded)!;
            var styleBack = storedStyle.GetType().GetMethod("ToStyle")!.Invoke(storedStyle, null)!;
            Check((double)styleType.GetProperty("CornerRadius")!.GetValue(styleBack)! == 12.0,
                "落盘的选区外观能再解成圆角 12");
            Check((bool)storedStyle.GetType().GetProperty("CompactLayout")!.GetValue(storedStyle)!,
                "落盘的选区外观带着版面压缩开关");
        }
        finally
        {
            try { reopen.Close(); } catch { /* 已关 */ }
        }

        // ============ ④ 没有底图的老记录：箭头 / 重新编辑截图都收起 ============
        var legacyWindow = (Window)Activator.CreateInstance(viewerType, true)!;
        legacyWindow.Show();
        Pump(legacyWindow);
        viewerType.GetMethod("Present", Inst)!.Invoke(legacyWindow, new object[] { legacy, 0 });
        Pump(legacyWindow);

        Check((legacyWindow.FindName("PrevSlideBtn") as Button)?.Visibility == Visibility.Collapsed,
            "老记录只有一张：不摆左箭头");
        Check((legacyWindow.FindName("NextSlideBtn") as Button)?.Visibility == Visibility.Collapsed,
            "老记录只有一张：不摆右箭头");
        Check((legacyWindow.FindName("SlideChipText") as TextBlock)?.Text == "成品图",
            "老记录角标只写「成品图」");
        Check((legacyWindow.FindName("ReopenBtn") as Button)?.Visibility == Visibility.Collapsed,
            "老记录没有整屏：不摆「重新编辑截图」");
        Check((legacyWindow.FindName("Picture") as Image)?.Source is BitmapSource legacyShot &&
              legacyShot.PixelWidth == 100, "老记录照旧显示成品图");
        legacyWindow.Close();

        // ============ ⑤ 卡片模板：箭头与角标都在（多张时才显示） ============
        var historyType = asm.GetType("LTools.Views.CapturePro.CaptureHistoryWindow")!;
        var history = (Window)Activator.CreateInstance(historyType)!;
        history.Show();
        Pump(history);

        Save(Render(history), Path.Combine(_outDir, "history-carousel.png"));

        var template = (DataTemplate)history.FindResource("CaptureCard");
        var card = (FrameworkElement)template.LoadContent();
        Check(FindNamed<Button>(card, "PrevSlideBtn") != null, "卡片模板里有左箭头");
        Check(FindNamed<Button>(card, "NextSlideBtn") != null, "卡片模板里有右箭头");
        Check(FindNamed<TextBlock>(card, "SlideChipText") != null, "卡片模板里有角标");

        var menu = historyType.GetMethod("BuildCardMenu", Inst)!.Invoke(history, new[] { entry }) as ContextMenu;
        var reopenItem = menu?.Items.OfType<MenuItem>().FirstOrDefault(item =>
            (item.Header?.ToString() ?? "").StartsWith("重新编辑截图", StringComparison.Ordinal));
        Check(reopenItem != null, "卡片菜单里有「重新编辑截图…」");
        Check(reopenItem?.IsEnabled == true, "有整屏底图时「重新编辑截图…」可用");

        var legacyMenu = historyType.GetMethod("BuildCardMenu", Inst)!
            .Invoke(history, new[] { legacy }) as ContextMenu;
        var legacyItem = legacyMenu?.Items.OfType<MenuItem>().FirstOrDefault(item =>
            (item.Header?.ToString() ?? "").StartsWith("重新编辑截图", StringComparison.Ordinal));
        Check(legacyItem != null && legacyItem.IsEnabled == false,
            "老记录没有整屏时「重新编辑截图…」置灰（不是点了没反应）");
        history.Close();

        catalogType.GetMethod("Clear")!.Invoke(null, null);
    }

    /// <summary>按名字在模板里找控件（DataTemplate 还没挂到可视树时 <c>FindName</c> 不一定有名字域）。</summary>
    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && match.Name == name) return match;

        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject node && FindNamed<T>(node, name) is { } found) return found;
        }

        return null;
    }

    /// <summary>纯色位图（夹具用）。</summary>
    private static BitmapSource SolidImage(int width, int height, Color color)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (int i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = color.B;
            bytes[i + 1] = color.G;
            bytes[i + 2] = color.R;
            bytes[i + 3] = color.A;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
