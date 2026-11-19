using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// P6-d 拼图转 PDF（照 snow-shot 的 <c>ImageStitchingPdfSettingsWidget</c> +
/// <c>ImageStitchingPdfPageGrid</c> + <c>ImageStitchingPdfTemplateWidget</c> +
/// <c>startPdfExport</c>）：静态 ↔ PDF 两套表单互斥 / 分页与模板两种画布 /
/// 表单可见性（自定义纸张、图像大小、间距列）/ 红字与「上一次有效设置」提示 /
/// 页面网格真鼠标选卡·旋转·拖动换序 / 模板画布真鼠标拖图·把手缩放·滚轮缩放·F 适应·
/// PageUp·PageDown·Delete / 真导出字节 / 导出取消与部分失败文案 / 设置落盘。
/// </summary>
/// <remarks>
/// 设置的读写都经临时配置目录（<c>_outDir/collagepdf-config</c>），本机
/// <c>configs/settings.json</c> 一个字节都不动。原生「保存 PDF / 选择目标文件夹」对话框
/// 在冒烟里没法点（模态、不属于本进程 WPF 树），所以窗口层只验导出状态机
/// （<c>FinishPdfExport</c> 的取消 / 部分失败 / 成功三条出口），真正写 PDF 的字节用
/// <c>CollagePdfExport.Export</c> 直接跑——窗口里跑的是同一个函数。
/// </remarks>
internal static partial class Program
{
    private static void CollagePdfSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { Dispatcher.CurrentDispatcher });

        var windowType = asm.GetType("LTools.Views.CapturePro.ImageCollageWindow")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.Collage.CollageStore")!;
        var settingsType = asm.GetType("LTools.Features.CapturePro.Collage.CollageSettings")!;
        var pdfSettingsType = asm.GetType("LTools.Features.CapturePro.Collage.CollagePdfSettings")!;
        var pdfExportType = asm.GetType("LTools.Features.CapturePro.Collage.CollagePdfExport")!;
        var pdfResultType = asm.GetType("LTools.Features.CapturePro.Collage.CollagePdfExportResult")!;

        // 设置走临时配置：别写进本机 configs/settings.json
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "collagepdf-config");
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        configPathSet.Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });

        try
        {
            // 上一轮冒烟留下的值不参与断言
            storeType.GetMethod("Save", Stat)!.Invoke(null,
                new[] { settingsType.GetProperty("Default", Stat)!.GetValue(null)! });
            storeType.GetMethod("SavePdf", Stat)!.Invoke(null,
                new[] { pdfSettingsType.GetProperty("Default", Stat)!.GetValue(null)! });

            string dir = Path.Combine(_outDir, "collagepdf");
            Directory.CreateDirectory(dir);

            string MakeSolid(string name, int width, int height, Color color)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, width, height));

                var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                image.Render(visual);
                image.Freeze();
                string path = Path.Combine(dir, name);
                Save(image, path);
                return path;
            }

            string fileA = MakeSolid("a-red.png", 120, 80, Colors.Red);
            string fileB = MakeSolid("b-green.png", 80, 120, Colors.Green);
            string fileC = MakeSolid("c-blue.png", 160, 90, Colors.Blue);

            // ============ 打开窗口并装三张图 ============
            var window = (Window)Activator.CreateInstance(windowType)!;
            window.Show();
            Pump(window);

            var total = (TextBlock)Field(window, "TotalText")!;
            var dims = (TextBlock)Field(window, "DimensionsText")!;
            var status = (TextBlock)Field(window, "StatusText")!;
            var saveBtn = (Button)Field(window, "SaveBtn")!;
            var cancelBtn = (Button)Field(window, "CancelBtn")!;
            var editBtn = (Button)Field(window, "EditBtn")!;
            var copyBtn = (Button)Field(window, "CopyBtn")!;
            var formPanel = (FrameworkElement)Field(window, "FormPanel")!;
            var staticSection = (FrameworkElement)Field(window, "StaticSection")!;
            var pdfSection = (FrameworkElement)Field(window, "PdfSection")!;
            var outStatic = (RadioButton)Field(window, "OutStatic")!;
            var outPdf = (RadioButton)Field(window, "OutPdf")!;
            var preview = (FrameworkElement)Field(window, "Preview")!;
            var emptyState = (FrameworkElement)Field(window, "EmptyState")!;
            var thumbnailRow = (FrameworkElement)Field(window, "ThumbnailRow")!;
            var canvasToolbar = (FrameworkElement)Field(window, "CanvasToolbar")!;
            var pdfGrid = (FrameworkElement)Field(window, "PdfGrid")!;
            var pdfGridHost = (FrameworkElement)Field(window, "PdfGridHost")!;
            var pdfGridEmpty = (FrameworkElement)Field(window, "PdfGridEmpty")!;
            var pdfGridToolbar = (FrameworkElement)Field(window, "PdfGridToolbar")!;
            var pdfGridNotice = (TextBlock)Field(window, "PdfGridNotice")!;
            var pdfCardToolbar = (Border)Field(window, "PdfCardToolbar")!;
            var pdfRotateCwBtn = (Button)Field(window, "PdfRotateCwBtn")!;
            var pdfTemplateHost = (FrameworkElement)Field(window, "PdfTemplateHost")!;
            var pdfTemplateCanvas = (FrameworkElement)Field(window, "PdfTemplateCanvas")!;
            var pdfTemplateEmpty = (FrameworkElement)Field(window, "PdfTemplateEmpty")!;
            var pdfTemplateToolbar = (FrameworkElement)Field(window, "PdfTemplateToolbar")!;
            var pdfTemplateSelectionToolbar = (Border)Field(window, "PdfTemplateSelectionToolbar")!;
            var pdfTemplateNotice = (TextBlock)Field(window, "PdfTemplateNotice")!;
            var pdfPageBar = (FrameworkElement)Field(window, "PdfPageBar")!;
            var pdfPageLabel = (TextBlock)Field(window, "PdfPageLabel")!;
            var pdfPrevPageBtn = (Button)Field(window, "PdfPrevPageBtn")!;
            var pdfNextPageBtn = (Button)Field(window, "PdfNextPageBtn")!;
            var pdfThumbRow = (FrameworkElement)Field(window, "PdfThumbRow")!;
            var pdfThumbHost = (Panel)Field(window, "PdfThumbHost")!;
            var pdfMethodPage = (RadioButton)Field(window, "PdfMethodPage")!;
            var pdfMethodTemplate = (RadioButton)Field(window, "PdfMethodTemplate")!;
            var pdfMethodError = (TextBlock)Field(window, "PdfMethodError")!;
            var pdfTemplateRow = (FrameworkElement)Field(window, "PdfTemplateRow")!;
            var pdfTemplateTwo = (Button)Field(window, "PdfTemplateTwo")!;
            var pdfTemplateFour = (Button)Field(window, "PdfTemplateFour")!;
            var pdfTemplateFitBtn = (Button)Field(window, "PdfTemplateFitBtn")!;
            var pdfPaperBox = (ComboBox)Field(window, "PdfPaperBox")!;
            var pdfPaperError = (TextBlock)Field(window, "PdfPaperError")!;
            var pdfPortrait = (RadioButton)Field(window, "PdfPortrait")!;
            var pdfLandscape = (RadioButton)Field(window, "PdfLandscape")!;
            var pdfOrientationRow = (FrameworkElement)Field(window, "PdfOrientationRow")!;
            var pdfCustomRow = (FrameworkElement)Field(window, "PdfCustomRow")!;
            var pdfWidthBox = (TextBox)Field(window, "PdfWidthBox")!;
            var pdfHeightBox = (TextBox)Field(window, "PdfHeightBox")!;
            var pdfWidthError = (TextBlock)Field(window, "PdfWidthError")!;
            var pdfSpacingRow = (FrameworkElement)Field(window, "PdfSpacingRow")!;
            var pdfSpacingColumn = (ColumnDefinition)Field(window, "PdfSpacingColumn")!;
            var pdfImageSpacingPanel = (FrameworkElement)Field(window, "PdfImageSpacingPanel")!;
            var pdfImageSpacingBox = (TextBox)Field(window, "PdfImageSpacingBox")!;
            var pdfExportCombined = (RadioButton)Field(window, "PdfExportCombined")!;
            var pdfExportPerPage = (RadioButton)Field(window, "PdfExportPerPage")!;
            var pdfQualitySlider = (Slider)Field(window, "PdfQualitySlider")!;

            var doc = Field(window, "_doc")!;
            var docType = doc.GetType();
            object Doc(string name) => docType.GetProperty(name)!.GetValue(doc)!;
            List<object> Items() =>
                ((IEnumerable)Doc("Items")!).Cast<object>().ToList();
            long Selected() => (long)Doc("SelectedId");
            object Pdf() => Doc("PdfSettings");
            object PdfValid() => Doc("LastValidPdfSettings");
            object PdfProp(string name) => Pdf().GetType().GetProperty(name)!.GetValue(Pdf())!;
            object ItemProp(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item)!;
            long IdOf(object item) => (long)ItemProp(item, "Id");
            object PlacementOf(object item) => ItemProp(item, "PdfPlacement");
            bool PlacementIsDefault(object item) =>
                (bool)PlacementOf(item).GetType().GetProperty("IsDefault")!.GetValue(PlacementOf(item))!;
            double? PlacementScale(object item) =>
                (double?)PlacementOf(item).GetType().GetProperty("MillimetersPerPixel")!
                    .GetValue(PlacementOf(item));
            double PlacementOffset(object item, string name) =>
                (double)PlacementOf(item).GetType().GetProperty(name)!.GetValue(PlacementOf(item))!;

            InvokeWith(window, "PresentFiles", (IReadOnlyList<string>)new[] { fileA, fileB, fileC });
            Check(WaitFor(window, () => total.Text == "共 3 张图像", 20000), "三张图读进来：" + total.Text);
            Pump(window);

            Check(staticSection.Visibility == Visibility.Visible && pdfSection.Visibility == Visibility.Collapsed,
                "默认是静态图像：静态表单露出、PDF 表单收起");
            Check(outStatic.IsChecked == true && outPdf.IsChecked != true, "输出类型默认选中「静态图像」");
            Check(saveBtn.Content?.ToString() == "保存", "窗口底部按钮文案永远是「保存」");
            Check(editBtn.Visibility == Visibility.Visible && copyBtn.Visibility == Visibility.Visible,
                "静态模式有「图像编辑 / 复制」");
            Check(dims.Text == "120 × 328 px", "静态模式页脚按像素：" + dims.Text);

            // ============ 切到 PDF ============
            outPdf.IsChecked = true;
            Pump(window);

            Check(Doc("OutputType").ToString() == "PdfDocument", "输出类型切到 PDF 文档");
            Check(staticSection.Visibility == Visibility.Collapsed && pdfSection.Visibility == Visibility.Visible,
                "PDF 表单露出、静态表单收起（照上游 refresh 两段互斥）");
            Check(preview.Visibility == Visibility.Collapsed && thumbnailRow.Visibility == Visibility.Collapsed &&
                  emptyState.Visibility == Visibility.Collapsed,
                "PDF 模式收起静态画布 / 缩略图条 / 空态");
            Check(canvasToolbar.Visibility == Visibility.Collapsed, "PDF 模式收起静态画布工具条");
            Check(pdfGridHost.Visibility == Visibility.Visible &&
                  pdfTemplateHost.Visibility == Visibility.Collapsed,
                "默认分页拼接：页面网格露出、模板画布收起");
            Check(pdfGridEmpty.Visibility == Visibility.Collapsed && pdfGridToolbar.Visibility == Visibility.Visible,
                "有图时不显示页面网格空态，底部工具条常驻");
            Check(pdfTemplateEmpty.Visibility == Visibility.Collapsed, "模板空态也收起");
            Check(editBtn.Visibility == Visibility.Collapsed && copyBtn.Visibility == Visibility.Collapsed,
                "PDF 模式隐藏「图像编辑 / 复制」");
            Check(saveBtn.IsEnabled && cancelBtn.Content?.ToString() == "取消", "PDF 模式「保存」可用");
            Check(total.Text == "共 3 页", "分页拼接一张一页：" + total.Text);
            Check(dims.Text == "210 × 297 毫米", "A4 纵向页脚：" + dims.Text);
            Check(pdfMethodPage.IsChecked == true && pdfTemplateRow.Visibility == Visibility.Collapsed,
                "默认「分页拼接」，模板按钮行收起");
            Check(pdfOrientationRow.Visibility == Visibility.Visible && pdfSpacingRow.Visibility == Visibility.Visible,
                "分页拼接显示纸张方向与页边距");
            Check(pdfImageSpacingPanel.Visibility == Visibility.Collapsed &&
                  pdfSpacingColumn.Width.Value == 0,
                "分页拼接不显示「图像间距」（第二列宽度归零）");
            Check(pdfCustomRow.Visibility == Visibility.Collapsed, "A4 不显示自定义宽高");
            Check(pdfWidthBox.Text == "210" && pdfHeightBox.Text == "297", "自定义宽高回显 210 / 297");
            Check(pdfQualitySlider.Value == 100 && pdfExportCombined.IsChecked == true, "默认合并导出、质量 100");

            Save(RenderHi(window), Path.Combine(dir, "pdf-page-grid.png"));

            // ============ 页面网格：真鼠标选卡 / 选中工具条 / 旋转 ============
            SetTopmost(window, true);
            ForceForeground(window);
            Pump(window);

            void MouseClick(Point screen)
            {
                SetCursorPos((int)screen.X, (int)screen.Y);
                Thread.Sleep(120);
                Pump(window);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(180);
                Pump(window);
            }

            void Drag(Point from, Point to, int steps = 6)
            {
                SetCursorPos((int)from.X, (int)from.Y);
                Thread.Sleep(160);
                Pump(window);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(80);
                for (int step = 1; step <= steps; step++)
                {
                    SetCursorPos((int)(from.X + (to.X - from.X) * step / (double)steps),
                        (int)(from.Y + (to.Y - from.Y) * step / (double)steps));
                    Thread.Sleep(45);
                }

                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(240);
                Pump(window);
            }

            Rect CardRect(int index) => (Rect)InvokeValue(pdfGrid, "CardRectAt", index)!;
            Point CardCenter(int index)
            {
                var rect = CardRect(index);
                return pdfGrid.PointToScreen(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            }

            Check(Items().Count == 3, "页面网格三张卡");
            var orderBefore = Items().Select(IdOf).ToList();
            MouseClick(CardCenter(1));            Check(Selected() == orderBefore[1], "点第二张卡就选中它");
            Check(pdfCardToolbar.Visibility == Visibility.Visible, "选中后浮动工具条露出来");
            Check(pdfRotateCwBtn.IsEnabled, "有选中项时旋转按钮可用");
            var cardBounds = new Rect(pdfGrid.RenderSize);
            Check(cardBounds.Contains(pdfCardToolbar.TranslatePoint(new Point(0, 0), pdfGrid)),
                "浮动工具条摆在网格内（不被裁掉）");

            string transformBefore = ItemProp(Items()[1], "Transform")!.ToString()!;
            pdfRotateCwBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(ItemProp(Items()[1], "Transform")!.ToString() != transformBefore, "工具条旋转真的转了这张图");
            Check(Selected() == orderBefore[1], "旋转不改选中项");

            // 键盘：Home / End / 方向键换选中
            pdfGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pdfGrid)!,
                0, Key.End) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(window);
            Check(Selected() == orderBefore[2], "End 选到最后一张");
            pdfGrid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pdfGrid)!,
                0, Key.Left) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(window);
            Check(Selected() == orderBefore[1], "← 选上一张");

            // 真鼠标拖动换序：第 1 张拖到第 3 张中间 → 落到最后
            Drag(CardCenter(0), CardCenter(2));
            var reordered = Items().Select(IdOf).ToList();
            Check(Items().Count == 3 && reordered[2] == orderBefore[0],
                "拖动换序：第 1 张落到最后（" + string.Join(",", reordered) + "）");
            Check(reordered.OrderBy(x => x).SequenceEqual(orderBefore.OrderBy(x => x)),
                "换序不丢图、不重复");

            // ============ 校验：页边距吃掉内容区 ============
            var marginBox = (TextBox)Field(window, "PdfMarginBox")!;
            var marginError = (TextBlock)Field(window, "PdfMarginError")!;
            marginBox.Text = "300";
            InvokeWith(window, "CommitPdfField", marginBox);
            Pump(window);
            Check(marginError.Text.Contains("页边距"), "页边距太大给出红字：" + marginError.Text);
            Check(pdfGridNotice.Visibility == Visibility.Visible, "无效设置时页面网格顶部挂提示");
            Check(!saveBtn.IsEnabled, "无效设置时「保存」置灰");
            Check(pdfPageBar.Visibility == Visibility.Collapsed, "分页拼接不显示模板页条");
            Save(RenderHi(window), Path.Combine(dir, "pdf-invalid.png"));

            marginBox.Text = "10";
            InvokeWith(window, "CommitPdfField", marginBox);
            Pump(window);
            Check(marginError.Text.Length == 0, "改回 10 毫米后红字消失");
            Check(pdfGridNotice.Visibility == Visibility.Collapsed && saveBtn.IsEnabled, "提示收起、保存恢复可用");

            // ============ 自定义纸张 / 图像大小纸张的可见性 ============
            ComboBoxItem Paper(string tag) =>
                pdfPaperBox.Items.OfType<ComboBoxItem>().First(i => (i.Tag as string) == tag);

            void Commit(TextBox box)
            {
                InvokeWith(window, "CommitPdfField", box);
                Pump(window);
            }

            pdfPaperBox.SelectedItem = Paper("custom");
            Pump(window);
            Check(pdfCustomRow.Visibility == Visibility.Visible, "自定义纸张露出宽高");
            Check(dims.Text == "210 × 297 毫米", "自定义 210×297 页脚不变：" + dims.Text);

            pdfWidthBox.Text = "abc";
            Commit(pdfWidthBox);
            Check(pdfWidthError.Text.Contains("正数"), "宽度写坏给红字：" + pdfWidthError.Text);
            Check(!saveBtn.IsEnabled, "宽度写坏时不能保存");

            pdfWidthBox.Text = "150";
            Commit(pdfWidthBox);
            Check(pdfWidthError.Text.Length == 0 && dims.Text == "150 × 297 毫米",
                "宽度 150 → 页脚 150 × 297：" + dims.Text);

            pdfLandscape.IsChecked = true;
            Pump(window);
            Check(dims.Text == "297 × 150 毫米", "横向把纸转过来：" + dims.Text);
            pdfPortrait.IsChecked = true;
            Pump(window);
            Check(dims.Text == "150 × 297 毫米", "切回纵向：" + dims.Text);

            pdfPaperBox.SelectedItem = Paper("a4");
            Pump(window);
            Check(pdfCustomRow.Visibility == Visibility.Collapsed && dims.Text == "210 × 297 毫米",
                "回到 A4、自定义宽高收起");

            pdfPaperBox.SelectedItem = Paper("image_size");
            Pump(window);
            Check(pdfOrientationRow.Visibility == Visibility.Collapsed &&
                  pdfSpacingRow.Visibility == Visibility.Collapsed,
                "图像大小 + 分页拼接：隐藏方向与页边距 / 间距");
            Check(dims.Text.Contains("21.16667") && dims.Text.Contains("31.75"),
                "图像大小按选中图 80×120 px 换成毫米：" + dims.Text);

            pdfPaperBox.SelectedItem = Paper("a4");
            Pump(window);
            Check(pdfOrientationRow.Visibility == Visibility.Visible &&
                  pdfSpacingRow.Visibility == Visibility.Visible && dims.Text == "210 × 297 毫米",
                "回到 A4：方向与页边距恢复");

            // ============ 模板拼接 ============
            pdfMethodTemplate.IsChecked = true;
            Pump(window);
            Check(Doc("OutputType").ToString() == "PdfDocument" && PdfValid().ToString()!.Contains("TemplateBased"),
                "拼接方式切到模板拼接");
            Check(pdfTemplateHost.Visibility == Visibility.Visible &&
                  pdfGridHost.Visibility == Visibility.Collapsed,
                "模板拼接换成模板画布");
            Check(pdfTemplateRow.Visibility == Visibility.Visible, "模板按钮行露出来");
            Check(pdfTemplateToolbar.Visibility == Visibility.Visible &&
                  pdfTemplateNotice.Visibility == Visibility.Collapsed,
                "模板底部工具条常驻、设置有效时不挂提示");
            Check(pdfMethodError.Text.Length == 0 && pdfPaperError.Text.Length == 0,
                "有效设置时拼接方式 / 纸张都没有红字");
            Check(pdfHeightBox.Text == "297" && pdfImageSpacingBox.Text == "5",
                "自定义高 / 图像间距回显 297 / 5");
            Check(pdfTemplateTwo.GetType().GetProperty("Selected")!.GetValue(pdfTemplateTwo) is true,
                "默认选中「两张图像」模板");
            Check(pdfImageSpacingPanel.Visibility == Visibility.Visible &&
                  pdfSpacingColumn.Width.IsStar,
                "模板拼接显示「图像间距」（第二列宽度拉满）");
            Check(pdfPageBar.Visibility == Visibility.Visible &&
                  pdfPageLabel.Text == "第 2 页，共 2 页",
                "3 张图 + 两张模板 → 2 页，选中项在最后一页：" + pdfPageLabel.Text);
            Check(pdfThumbRow.Visibility == Visibility.Visible && pdfThumbHost.Children.Count == 3,
                "模板模式露出缩略图条，3 张：" + pdfThumbHost.Children.Count);

            // 回到第 1 页再验翻页按钮（选中项在最后一页时会自动跟过去）
            pdfPrevPageBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(pdfPageLabel.Text == "第 1 页，共 2 页", "上一页按钮回到第 1 页：" + pdfPageLabel.Text);
            Check(pdfPrevPageBtn.IsEnabled == false && pdfNextPageBtn.IsEnabled,
                "第 1 页：上一页置灰、下一页可用");
            Check(pdfTemplateSelectionToolbar.Visibility == Visibility.Visible,
                "模板画布选中项工具条露出来");
            Check(total.Text == "共 2 页", "页脚改成「共 2 页」：" + total.Text);
            Check(dims.Text == "210 × 297 毫米", "模板模式页脚取合成页：" + dims.Text);

            Save(RenderHi(window), Path.Combine(dir, "pdf-template.png"));

            // 下一页按钮 / PageDown 键
            pdfNextPageBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(pdfPageLabel.Text == "第 2 页，共 2 页", "下一页按钮翻到第 2 页：" + pdfPageLabel.Text);
            Check(Selected() == IdOf(Items()[2]), "翻页把选中项带到这一页的第一张");
            Check(pdfPrevPageBtn.IsEnabled && !pdfNextPageBtn.IsEnabled, "第 2 页：下一页置灰、上一页可用");

            var canvasSource = PresentationSource.FromVisual(pdfTemplateCanvas)!;
            void CanvasKey(Key key) => pdfTemplateCanvas.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                canvasSource, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });

            CanvasKey(Key.PageUp);
            Pump(window);
            Check(pdfPageLabel.Text == "第 1 页，共 2 页", "PageUp 回到第 1 页：" + pdfPageLabel.Text);
            Check(Selected() == IdOf(Items()[0]), "PageUp 选中第 1 页的第一张");

            // ============ 模板画布：真鼠标拖图 / 把手缩放 / Esc 取消 ============
            var paper = (Rect)pdfTemplateCanvas.GetType().GetProperty("PaperRect")!.GetValue(pdfTemplateCanvas)!;
            Check(paper.Width > 0 && paper.Height > 0, "模板画布露出纸面");
            double viewScale = paper.Width / 210.0;   // A4 纵向宽 210 毫米
            Point OnPaper(double mmX, double mmY) =>
                pdfTemplateCanvas.PointToScreen(new Point(paper.X + mmX * viewScale, paper.Y + mmY * viewScale));

            // 两张模板的第 1 格 = 内容区 (10,10,190,136)，中心 (105, 78)
            var cellCenter = OnPaper(105, 78);
            long HoverCell(Point screen) => (long)pdfTemplateCanvas.GetType()
                .GetMethod("HitCell", Inst)!.Invoke(pdfTemplateCanvas,
                    new object[] { pdfTemplateCanvas.PointFromScreen(screen) })!;
            Check(HoverCell(cellCenter) == IdOf(Items()[0]), "第 1 格中心命中的是第 1 张");

            Drag(cellCenter, new Point(cellCenter.X + 30, cellCenter.Y + 20));
            var moved = Items()[0];
            Check(!PlacementIsDefault(moved), "在模板里拖动真的写下 PDF 手摆");
            Check(PlacementOffset(moved, "CenterOffsetXMm") > 0 &&
                  PlacementOffset(moved, "CenterOffsetYMm") > 0,
                $"往右下拖 → 偏移为正（{PlacementOffset(moved, "CenterOffsetXMm"):0.##}, " +
                $"{PlacementOffset(moved, "CenterOffsetYMm"):0.##}）");
            double offsetX = PlacementOffset(moved, "CenterOffsetXMm");
            double offsetY = PlacementOffset(moved, "CenterOffsetYMm");
            // 拖的是物理像素：先按 DPI 换 DIP，再按纸面比例换毫米
            double dpi = VisualTreeHelper.GetDpi(pdfTemplateCanvas).PixelsPerDip;
            Check(Math.Abs(offsetX - 30 / dpi / viewScale) < 1.5 &&
                  Math.Abs(offsetY - 20 / dpi / viewScale) < 1.5,
                $"偏移量按毫米算（{offsetX:0.##}, {offsetY:0.##} 毫米；30×20 物理像素、" +
                $"DPI {dpi:0.##}、{viewScale:0.###} DIP/毫米）");

            // 把手缩放：抓选中框右下角往外拖
            var frame = (Rect)pdfTemplateCanvas.GetType().GetProperty("SelectionFrame")!.GetValue(pdfTemplateCanvas)!;
            Check(!frame.IsEmpty, "选中框有矩形");
            double? scaleBefore = PlacementScale(Items()[0]);
            Drag(pdfTemplateCanvas.PointToScreen(frame.BottomRight),
                pdfTemplateCanvas.PointToScreen(new Point(frame.BottomRight.X + 26,
                    frame.BottomRight.Y + 20)));
            double? scaleAfter = PlacementScale(Items()[0]);
            Check(scaleAfter.HasValue && scaleBefore.HasValue && scaleAfter > scaleBefore * 1.02,
                $"拖右下把手放大（{scaleBefore:0.###} → {scaleAfter:0.###} 毫米 / 像素）");

            // 滚轮缩放 + F 适应
            double zoomBefore = (double)pdfTemplateCanvas.GetType()
                .GetProperty("Zoom", Inst)!.GetValue(pdfTemplateCanvas)!;
            pdfTemplateCanvas.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = UIElement.MouseWheelEvent });
            Pump(window);
            double zoomAfter = (double)pdfTemplateCanvas.GetType()
                .GetProperty("Zoom", Inst)!.GetValue(pdfTemplateCanvas)!;
            Check(zoomAfter > zoomBefore * 1.1, $"滚轮放大（{zoomBefore:0.###} → {zoomAfter:0.###}）");

            CanvasKey(Key.F);
            Pump(window);
            Check(Math.Abs((double)pdfTemplateCanvas.GetType()
                .GetProperty("Zoom", Inst)!.GetValue(pdfTemplateCanvas) - 1) < 1e-9, "F 适应回 100%");

            // Esc 取消正在拖的手势：松手后位置不变
            var placed = Items()[0].GetType().GetProperty("PdfPlacement")!.GetValue(Items()[0])!;
            string placementText = placed.ToString()!;
            var cancelFrom = OnPaper(105, 78);
            SetCursorPos((int)cancelFrom.X, (int)cancelFrom.Y);
            Thread.Sleep(120);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            SetCursorPos((int)(cancelFrom.X + 60), (int)(cancelFrom.Y + 50));
            Thread.Sleep(120);
            Pump(window);
            CanvasKey(Key.Escape);
            Pump(window);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(200);
            Pump(window);
            Check(Items()[0].GetType().GetProperty("PdfPlacement")!.GetValue(Items()[0])!.ToString() ==
                  placementText, "Esc 取消拖动，手摆不变");
            Check(!(bool)pdfTemplateCanvas.GetType().GetProperty("IsGesturing")!.GetValue(pdfTemplateCanvas)!,
                "Esc 之后手势已收干净");

            // 模板页脚尺寸换算（150×90 的图放进 190×136 格子）
            Check(Items().Count == 3, "三张图都还在");

            // ============ 模板缩略图：真鼠标拖到另一张 = 交换 ============
            var pdfCards = (IList)Field(window, "_pdfCards")!;
            Check(pdfCards.Count == 3, "PDF 缩略图三张：" + pdfCards.Count);
            Point CardCenter2(object card)
            {
                var element = (FrameworkElement)card;
                return element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            }

            var swapBefore = Items().Select(IdOf).ToList();
            Drag(CardCenter2(pdfCards[0]!), CardCenter2(pdfCards[2]!));
            Check(Items().Select(IdOf).SequenceEqual(new[] { swapBefore[2], swapBefore[1], swapBefore[0] }),
                "缩略图拖到另一张 = 交换两张：" + string.Join(",", Items().Select(IdOf)));

            // ============ 换模板：四张图像 ============
            pdfTemplateFour.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(PdfProp("Template").ToString() == "FourImages", "点「四张图像」模板");
            Check(pdfTemplateFour.GetType().GetProperty("Selected")!.GetValue(pdfTemplateFour) is true,
                "四张模板按钮变成选中态");
            Check(pdfPageLabel.Text == "第 1 页，共 1 页", "3 张图 + 四张模板 → 1 页：" + pdfPageLabel.Text);

            pdfTemplateFitBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(Math.Abs((double)pdfTemplateCanvas.GetType()
                .GetProperty("Zoom", Inst)!.GetValue(pdfTemplateCanvas) - 1) < 1e-9, "「适应」按钮也回到 100%");

            // ============ 真导出字节（窗口里跑的是同一个 Export） ============
            var exportMethod = pdfExportType.GetMethod("Export", Stat)!;
            object Export(string path)
            {
                return exportMethod.Invoke(null, new object?[]
                {
                    path, Doc("Items"), Pdf(), null, null,
                })!;
            }

            // 两张模板 → 3 张图 2 页（先切回两张模板）
            pdfTemplateTwo.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            if (pdfPrevPageBtn.IsEnabled)
            {
                pdfPrevPageBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(window);
            }

            Check(pdfPageLabel.Text == "第 1 页，共 2 页" && !pdfPrevPageBtn.IsEnabled,
                "切回两张模板 → 2 页、能停在 / 回到第 1 页（" + pdfPageLabel.Text + "）");
            string combined = Path.Combine(dir, "export-combined");
            var combinedResult = Export(combined);
            var succeeded = (bool)pdfResultType.GetProperty("Succeeded")!.GetValue(combinedResult)!;
            Check(succeeded, "合并导出成功");
            var paths = (IList)pdfResultType.GetProperty("Paths")!.GetValue(combinedResult)!;
            Check(paths.Count == 1 && File.Exists(paths[0]!.ToString()), "合并导出写一个 .pdf");
            string pdfText = Encoding.Latin1.GetString(File.ReadAllBytes(paths[0]!.ToString()!));
            Check(pdfText.StartsWith("%PDF-1.7", StringComparison.Ordinal), "文件头是 PDF 1.7");
            Check(pdfText.Contains("/Count 2", StringComparison.Ordinal), "两张模板 3 张图 → 2 页");
            Check(pdfText.Contains("/MediaBox [0 0 595.275591 841.889764]", StringComparison.Ordinal),
                "A4 的 MediaBox 按 72 点 / 英寸写");
            Check(pdfText.Contains("/Filter /FlateDecode", StringComparison.Ordinal) &&
                  !pdfText.Contains("/DCTDecode", StringComparison.Ordinal),
                "质量 100 走无损 Flate");

            // 逐页导出：001_page.pdf / 002_page.pdf
            pdfExportPerPage.IsChecked = true;
            Pump(window);
            string perPage = Path.Combine(dir, "export-pages");
            var perPageResult = Export(perPage);
            Check((bool)pdfResultType.GetProperty("Succeeded")!.GetValue(perPageResult)!, "逐页导出成功");
            Check(File.Exists(Path.Combine(perPage, "001_page.pdf")) &&
                  File.Exists(Path.Combine(perPage, "002_page.pdf")), "逐页导出按 NNN_page.pdf 命名");
            pdfExportCombined.IsChecked = true;
            Pump(window);

            // ============ 导出状态机（取消 / 部分失败 / 成功关窗） ============
            var pdfExportingField = windowType.GetField("_pdfExporting", Inst)!;
            object NewResult(int total, string error = "", bool cancelled = false, int[]? completed = null,
                string[]? files = null)
            {
                var result = Activator.CreateInstance(pdfResultType)!;
                pdfResultType.GetProperty("TotalCount")!.SetValue(result, total);
                pdfResultType.GetProperty("Error")!.SetValue(result, error);
                pdfResultType.GetProperty("Cancelled")!.SetValue(result, cancelled);
                var pages = (IList)pdfResultType.GetProperty("CompletedPages")!.GetValue(result)!;
                foreach (int page in completed ?? Array.Empty<int>()) pages.Add(page);
                var list = (IList)pdfResultType.GetProperty("Paths")!.GetValue(result)!;
                foreach (string file in files ?? Array.Empty<string>()) list.Add(file);
                return result;
            }

            pdfExportingField.SetValue(window, true);
            InvokeWith(window, "UpdateChrome");
            Pump(window);
            Check(cancelBtn.Content?.ToString() == "取消导出", "导出期间「取消」变成「取消导出」");
            Check(!saveBtn.IsEnabled && !formPanel.IsEnabled, "导出期间保存与表单都禁用");

            InvokeWith(window, "FinishPdfExport", NewResult(2, cancelled: true));
            Pump(window);
            Check(window.IsVisible, "取消导出后窗口留着");
            Check(status.Text == "PDF 导出已取消。您的图像和编辑已保留",
                "取消文案照上游原文：" + status.Text);
            Check(cancelBtn.Content?.ToString() == "取消" && saveBtn.IsEnabled, "取消后按钮恢复");

            pdfExportingField.SetValue(window, true);
            InvokeWith(window, "UpdateChrome");
            InvokeWith(window, "FinishPdfExport", NewResult(2, "无法导出 PDF，请重试",
                completed: new[] { 1 }, files: new[] { Path.Combine(dir, "partial.pdf") }));
            Pump(window);
            Check(status.Text.Contains("已保存页面：1") && status.Text.Contains("第 2 页未保存"),
                "部分失败时列出已保存页与未保存页：" + status.Text.Replace("\n", " / "));
            Check(window.IsVisible, "部分失败不关窗");

            pdfExportingField.SetValue(window, true);
            InvokeWith(window, "UpdateChrome");
            InvokeWith(window, "FinishPdfExport", NewResult(2,
                completed: new[] { 1, 2 }, files: new[] { Path.Combine(dir, "ok.pdf") }));
            bool closed = WaitFor(window, () => !window.IsVisible, 8000);
            Pump(window);
            Check(closed, "成功导出后照上游关窗（跳过放弃确认）");

            // ============ 设置落盘：PDF 段写在 settings.json 的 Collage 里 ============
            var loaded = storeType.GetMethod("LoadPdf", Stat)!.Invoke(null, null)!;
            string Field2(string name) => loaded.GetType().GetProperty(name)!.GetValue(loaded)!.ToString()!;
            Check(Field2("Method") == "TemplateBased", "落盘的拼接方式是模板拼接：" + Field2("Method"));
            Check(Field2("Template") == "TwoImages", "落盘的是最后选的两张模板：" + Field2("Template"));
            Check(Field2("Paper") == "A4" && Field2("Orientation") == "Portrait", "落盘 A4 纵向");
            Check(Math.Abs((double)loaded.GetType().GetProperty("ImageSpacingMm")!.GetValue(loaded)! - 5) < 1e-9,
                "落盘图像间距 5 毫米");
            Check(Math.Abs((double)loaded.GetType().GetProperty("MarginMm")!.GetValue(loaded)! - 10) < 1e-9,
                "落盘页边距 10 毫米");
            Check(Field2("ExportMode") == "Combined" && (int)loaded.GetType()
                    .GetProperty("ImageQuality")!.GetValue(loaded)! == 100,
                "落盘合并导出 + 质量 100");
        }
        finally
        {
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}
