using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// P6-c 图像拼接窗（照 snow-shot 的 <c>ImageStitchingWindow</c> + <c>ImageStitchingEditorWidget</c>）：
/// 空态 / 装图 / 表单（方向 / 尺寸方式 / 宫格 / 边距 px·% / 背景色与取色弹层）/
/// 真鼠标拖动·把手缩放·拖到另一张交换 / 缩略图交换 / 滚轮缩放·中键平移·F 适应 /
/// 复制到剪贴板 / 保存落盘逐像素 / 清空与关窗确认 / 会话复用。
/// </summary>
/// <remarks>
/// 设置写到临时目录（<c>_outDir/collage-config</c>），本机 <c>configs/settings.json</c> 一个字节都不动。
/// 真鼠标那几条必须先把窗口顶到前台并临时 Topmost：截图覆盖层也是 Topmost，同一层里谁最后
/// <c>SetWindowPos</c> 谁在前（与切图窗冒烟同一套做法）。
/// </remarks>
internal static partial class Program
{
    private static void CollageSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { Dispatcher.CurrentDispatcher });

        var windowType = asm.GetType("LTools.Views.CapturePro.ImageCollageWindow")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.Collage.CollageStore")!;
        var settingsType = asm.GetType("LTools.Features.CapturePro.Collage.CollageSettings")!;
        var sessionType = asm.GetType("LTools.Features.CapturePro.Collage.CollageSession")!;
        var clipboardType = asm.GetType("LTools.Native.NativeClipboard")!;

        // 设置走临时配置：别写进本机 configs/settings.json
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "collage-config");
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        configPathSet.Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });

        try
        {
            // 开窗读到默认设置（上一次冒烟留下的值不参与断言）
            storeType.GetMethod("Save", Stat)!.Invoke(null,
                new[] { settingsType.GetProperty("Default", Stat)!.GetValue(null)! });

            string dir = Path.Combine(_outDir, "collage");
            Directory.CreateDirectory(dir);

            // (R, G) = (x, y) 的渐变图：导出后逐像素认得出裁的是哪一块
            string MakeGradient(string name, int width, int height)
            {
                var pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int i = (y * width + x) * 4;
                        pixels[i] = (byte)(x % 256);
                        pixels[i + 1] = (byte)(y % 256);
                        pixels[i + 2] = 0;
                        pixels[i + 3] = 0xFF;
                    }
                }

                var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels,
                    width * 4);
                image.Freeze();
                string path = Path.Combine(dir, name);
                Save(image, path);
                return path;
            }

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

            string fileA = MakeGradient("a.png", 120, 80);
            string fileB = MakeGradient("b.png", 60, 120);
            string fileC = MakeGradient("c.png", 160, 40);

            // ============ 空态 ============
            var window = (Window)Activator.CreateInstance(windowType)!;
            window.Show();
            Pump(window);

            var preview = (FrameworkElement)Field(window, "Preview")!;
            var emptyState = (FrameworkElement)Field(window, "EmptyState")!;
            var thumbnailRow = (FrameworkElement)Field(window, "ThumbnailRow")!;
            var canvasToolbar = (FrameworkElement)Field(window, "CanvasToolbar")!;
            var selectionToolbar = (FrameworkElement)Field(window, "SelectionToolbar")!;
            var dims = (TextBlock)Field(window, "DimensionsText")!;
            var total = (TextBlock)Field(window, "TotalText")!;
            var status = (TextBlock)Field(window, "StatusText")!;
            var addBtn = (Button)Field(window, "AddBtn")!;
            var emptyAddBtn = (Button)Field(window, "EmptyAddBtn")!;
            var clearBtn = (Button)Field(window, "ClearBtn")!;
            var reverseBtn = (Button)Field(window, "ReverseBtn")!;
            var fitBtn = (Button)Field(window, "FitBtn")!;
            var cancelBtn = (Button)Field(window, "CancelBtn")!;
            var editBtn = (Button)Field(window, "EditBtn")!;
            var copyBtn = (Button)Field(window, "CopyBtn")!;
            var saveBtn = (Button)Field(window, "SaveBtn")!;

            Check(window.Title == "图像拼接", "拼图窗标题：" + window.Title);
            Check(emptyState.Visibility == Visibility.Visible, "没有图时显示空态");
            Check(preview.Visibility == Visibility.Collapsed, "没有图时不画画布");
            Check(thumbnailRow.Visibility == Visibility.Collapsed, "没有图时不摆缩略图条");
            Check(canvasToolbar.Visibility == Visibility.Visible, "空态时也能看见画布工具条");
            Check(selectionToolbar.Visibility == Visibility.Collapsed, "没有选中项时不摆选中工具条");
            Check(dims.Text.Length == 0 && total.Text == "共 0 张图像", "页脚：没有尺寸、0 张图像");
            Check(addBtn.IsEnabled && emptyAddBtn.IsEnabled, "「添加图像」两个入口都可用（工具条 + 空态）");
            Check(!clearBtn.IsEnabled && !reverseBtn.IsEnabled && !fitBtn.IsEnabled,
                "没有图时清空 / 反转 / 适应置灰");
            Check(!saveBtn.IsEnabled && !copyBtn.IsEnabled && !editBtn.IsEnabled,
                "没有图时保存 / 复制 / 图像编辑置灰");
            Check(cancelBtn.IsEnabled, "「取消」永远可用");
            Check(status.Text.Length == 0, "一开始没有状态文案");

            Save(RenderHi(window), Path.Combine(dir, "collage-empty.png"));

            // ============ 装图（真走文件解码）============
            InvokeWith(window, "PresentFiles", (IReadOnlyList<string>)new[] { fileA, fileB, fileC });
            Check(WaitFor(window, () => total.Text == "共 3 张图像", 20000),
                "三张图都读进来：" + total.Text);
            Pump(window);

            var doc = Field(window, "_doc")!;
            var docType = doc.GetType();
            object Doc(string name) => docType.GetProperty(name)!.GetValue(doc)!;
            object Layout() => Doc("Layout");
            var layoutType = Layout().GetType();
            int LW() => (int)layoutType.GetProperty("Width")!.GetValue(Layout())!;
            int LH() => (int)layoutType.GetProperty("Height")!.GetValue(Layout())!;
            List<object> Items() =>
                ((System.Collections.IEnumerable)Doc("Items")!).Cast<object>().ToList();
            List<Int32Rect> Rects() =>
                ((System.Collections.IEnumerable)layoutType.GetProperty("ImageRects")!.GetValue(Layout())!)
                .Cast<Int32Rect>().ToList();
            object Settings() => Doc("Settings");
            string Direction() => Settings().GetType().GetProperty("Direction")!.GetValue(Settings())!.ToString()!;
            string Sizing() => Settings().GetType().GetProperty("Sizing")!.GetValue(Settings())!.ToString()!;
            int IntSetting(string name) => (int)Settings().GetType().GetProperty(name)!.GetValue(Settings())!;
            Color Background() => (Color)Settings().GetType().GetProperty("Background")!.GetValue(Settings())!;
            long Selected() => (long)Doc("SelectedId");
            object ItemProp(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item)!;
            long IdOf(object item) => (long)ItemProp(item, "Id");
            object PlacementOf(object item) => ItemProp(item, "Placement");
            bool IsDefaultPlacement(object item) =>
                (bool)PlacementOf(item).GetType().GetProperty("IsDefault")!.GetValue(PlacementOf(item))!;
            double PlacementNumber(object item, string name) =>
                (double)PlacementOf(item).GetType().GetProperty(name)!.GetValue(PlacementOf(item))!;
            Rect View(Int32Rect rect) =>
                (Rect)InvokeValue(preview, "ToViewport", rect)!;

            Check(emptyState.Visibility == Visibility.Collapsed && preview.Visibility == Visibility.Visible,
                "装好图后空态收起、画布露出来");
            Check(thumbnailRow.Visibility == Visibility.Visible, "装好图后缩略图条露出来");
            Check(dims.Text == "120 × 350 px", "纵向统一尺寸的成品尺寸：" + dims.Text);
            Check(Math.Abs(LW() - 120) < 0.5 && LH() == 350, $"布局也是 120×350（实际 {LW()}×{LH()}）");
            Check(status.Text.Length == 0, "读图成功后状态行清空");

            var cards = (System.Collections.IList)Field(window, "_cards")!;
            Check(cards.Count == 3, $"三张缩略图（实际 {cards.Count}）");
            Check(cards.Cast<object>().Select(c => (long)ItemProp(c, "ItemId")).SequenceEqual(
                    Items().Select(IdOf)),
                "缩略图顺序与文档顺序一致");
            Check(cards.Cast<object>().All(c => ((FrameworkElement)c).Width == 108 &&
                                                ((FrameworkElement)c).Height == 84),
                "缩略图一格 108×84（照上游 kThumbnailSize）");
            Check(clearBtn.IsEnabled && reverseBtn.IsEnabled && fitBtn.IsEnabled && saveBtn.IsEnabled,
                "有图后清空 / 反转 / 适应 / 保存都可用");
            Check(Selected() == IdOf(Items()[0]), "装第一张时自动选中它");
            Check(selectionToolbar.Visibility == Visibility.Visible, "有选中项时选中工具条露出来");

            // ---- 反转顺序 ----
            var orderBefore = Items().Select(IdOf).ToList();
            reverseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(Items().Select(IdOf).SequenceEqual(orderBefore.AsEnumerable().Reverse()),
                "反转图像顺序");
            reverseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(Items().Select(IdOf).SequenceEqual(orderBefore), "再反转一次回到原顺序");

            // ============ 表单：外边距 / 间距（像素）============
            var dirVertical = (RadioButton)Field(window, "DirVertical")!;
            var dirHorizontal = (RadioButton)Field(window, "DirHorizontal")!;
            var dirCustom = (RadioButton)Field(window, "DirCustom")!;
            var gridRow = (FrameworkElement)Field(window, "GridRow")!;
            var sizeUniform = (RadioButton)Field(window, "SizeUniform")!;
            var sizeOriginal = (RadioButton)Field(window, "SizeOriginal")!;
            var rowsBox = (TextBox)Field(window, "RowsBox")!;
            var columnsBox = (TextBox)Field(window, "ColumnsBox")!;
            var borderBox = (TextBox)Field(window, "BorderBox")!;
            var borderSuffix = (TextBlock)Field(window, "BorderSuffix")!;
            var borderUnitPx = (RadioButton)Field(window, "BorderUnitPx")!;
            var borderUnitPct = (RadioButton)Field(window, "BorderUnitPct")!;
            var spacingBox = (TextBox)Field(window, "SpacingBox")!;
            var spacingSuffix = (TextBlock)Field(window, "SpacingSuffix")!;
            var spacingUnitPx = (RadioButton)Field(window, "SpacingUnitPx")!;
            var spacingUnitPct = (RadioButton)Field(window, "SpacingUnitPct")!;

            Check(Direction() == "Vertical" && Sizing() == "Uniform", "默认纵向 + 统一尺寸");
            Check(gridRow.Visibility == Visibility.Collapsed, "非宫格时收起行列");
            Check(borderSuffix.Text == "px" && spacingSuffix.Text == "px", "默认按像素输入");

            void Commit(TextBox box)
            {
                InvokeWith(window, "CommitField", box);
                Pump(window);
            }

            borderBox.Text = "10";
            Commit(borderBox);
            Check(IntSetting("Border") == 10, "外边距写进设置：" + IntSetting("Border"));
            Check(LW() == 140 && LH() == 370, $"外边距 10 → 140×370（实际 {LW()}×{LH()}）");

            spacingBox.Text = "6";
            Commit(spacingBox);
            Check(IntSetting("Spacing") == 6, "间距写进设置：" + IntSetting("Spacing"));
            Check(LH() == 382, $"间距 6（两段）→ 高 382（实际 {LH()}）");

            // ---- 百分比：参考长度 = 统一尺寸下的首图宽 120 ----
            borderUnitPct.IsChecked = true;
            Pump(window);
            Check(borderSuffix.Text == "%" && borderBox.Text == "8.33",
                $"切百分比：10 px 显示成 8.33%（实际 {borderBox.Text}{borderSuffix.Text}）");
            borderBox.Text = "25";
            Commit(borderBox);
            Check(IntSetting("Border") == 30, $"25% × 120 = 30 px（实际 {IntSetting("Border")}）");

            spacingUnitPct.IsChecked = true;
            Pump(window);
            Check(spacingSuffix.Text == "%" && spacingBox.Text == "5",
                $"间距 6 px = 5%（实际 {spacingBox.Text}{spacingSuffix.Text}）");
            spacingBox.Text = "10";
            Commit(spacingBox);
            Check(IntSetting("Spacing") == 12, $"10% × 120 = 12 px（实际 {IntSetting("Spacing")}）");

            borderBox.Text = "abc";
            Commit(borderBox);
            Check(IntSetting("Border") == 30 && borderBox.Text == "25", "填错就退回原值，不改设置");

            borderUnitPx.IsChecked = true;
            spacingUnitPx.IsChecked = true;
            Pump(window);
            Check(borderBox.Text == "30" && spacingBox.Text == "12" && borderSuffix.Text == "px",
                $"切回像素：30 / 12（实际 {borderBox.Text} / {spacingBox.Text}）");

            borderBox.Text = "0";
            Commit(borderBox);
            spacingBox.Text = "0";
            Commit(spacingBox);
            Check(LW() == 120 && LH() == 350, "边距 / 间距归零 → 120×350");

            // ============ 表单：方向与尺寸方式 ============
            dirHorizontal.IsChecked = true;
            Pump(window);
            Check(Direction() == "Horizontal", "切横向");
            Check(LW() == 480 && LH() == 80, $"横向统一尺寸：480×80（实际 {LW()}×{LH()}）");

            dirCustom.IsChecked = true;
            Pump(window);
            Check(Direction() == "Custom", "切宫格");
            Check(gridRow.Visibility == Visibility.Visible, "宫格时露出行列");
            Check(IntSetting("Rows") == 2 && IntSetting("Columns") == 2,
                $"3 张图自动定 2×2（实际 {IntSetting("Rows")}×{IntSetting("Columns")}）");
            Check(LW() == 240 && LH() == 160, $"宫格 2×2 格子 120×80 → 240×160（实际 {LW()}×{LH()}）");
            Check(rowsBox.Text == "2" && columnsBox.Text == "2", "行列输入框回显真值");

            sizeOriginal.IsChecked = true;
            Pump(window);
            Check(Sizing() == "Original", "切原始尺寸");
            Check(LW() == 320 && LH() == 240,
                $"原始尺寸宫格：格子取最大 160×120 → 320×240（实际 {LW()}×{LH()}）");

            // 列数装不下会被规整回去，输入框必须跟着拉回真值
            columnsBox.Text = "1";
            Commit(columnsBox);
            Check(IntSetting("Columns") == 2 && columnsBox.Text == "2",
                $"1 列装不下 3 张 → 自动补到 2 列（实际 {IntSetting("Columns")} / {columnsBox.Text}）");

            rowsBox.Text = "0";
            Commit(rowsBox);
            Check(IntSetting("Rows") == 2 && rowsBox.Text == "2", "行数 0 → 不进设置，输入框退回 2");

            sizeUniform.IsChecked = true;
            Pump(window);
            dirVertical.IsChecked = true;
            Pump(window);
            Check(Direction() == "Vertical" && Sizing() == "Uniform" && LW() == 120 && LH() == 350,
                "回到纵向 + 统一尺寸");

            // ============ 背景色（8 个预设 + 取色弹层）============
            var preset0 = (RadioButton)Field(window, "Preset0")!;
            var preset1 = (RadioButton)Field(window, "Preset1")!;
            var preset2 = (RadioButton)Field(window, "Preset2")!;
            var colorButton = (Button)Field(window, "ColorButton")!;
            var colorPopup = (Popup)Field(window, "ColorPopup")!;
            var colorHex = (TextBox)Field(window, "ColorHex")!;
            var alphaSlider = (Slider)Field(window, "AlphaSlider")!;

            Check(Background() == Colors.White && preset0.IsChecked == true, "默认白底，白预设选中");
            preset1.IsChecked = true;
            Pump(window);
            Check(Background() == Colors.Black, "点黑色预设 → 背景黑");

            colorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(colorPopup.IsOpen, "点「选择背景颜色」打开取色弹层");

            colorHex.Text = "#11223344";
            InvokeWith(window, "CommitColorHex");
            Pump(window);
            Check(Background() == Color.FromArgb(0x44, 0x11, 0x22, 0x33),
                $"手填 #RRGGBBAA 生效（实际 {Background()}）");

            alphaSlider.Value = 255;
            Pump(window);
            Check(Background().A == 255, "拖不透明度滑块 → 透明通道跟着变");
            Check(preset1.IsChecked != true && preset2.IsChecked != true, "半透明时没有预设算选中");

            // Esc 先关取色弹层，不是放弃整个拼接
            var keySource = PresentationSource.FromVisual(window)!;
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Pump(window);
            Check(!colorPopup.IsOpen && window.IsVisible, "Esc 只关取色弹层，窗口留着");

            preset2.IsChecked = true;
            Pump(window);
            Check(Background() == Color.FromRgb(0x8C, 0x8C, 0x8C), "灰色预设生效");
            preset0.IsChecked = true;
            Pump(window);
            Check(Background() == Colors.White, "白预设生效");

            Save(RenderHi(window), Path.Combine(dir, "collage-window.png"));

            // ============ 真鼠标：拖动 / 把手缩放 / 拖到另一张交换 ============
            SetTopmost(window, true);
            ForceForeground(window);
            Pump(window);

            void Drag(Point from, Point to)
            {
                SetCursorPos((int)from.X, (int)from.Y);
                Thread.Sleep(160);
                Pump(window);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(80);
                for (int step = 1; step <= 5; step++)
                {
                    SetCursorPos((int)(from.X + (to.X - from.X) * step / 5.0),
                        (int)(from.Y + (to.Y - from.Y) * step / 5.0));
                    Thread.Sleep(45);
                }

                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(240);
                Pump(window);
            }

            Point ScreenOf(Rect rect) => preview.PointToScreen(rect.TopLeft);

            var firstRect = View(Rects()[0]);
            var clickAt = ScreenOf(new Rect(firstRect.X + firstRect.Width / 2,
                firstRect.Y + firstRect.Height / 2, 0, 0));
            SetCursorPos((int)clickAt.X, (int)clickAt.Y);
            Thread.Sleep(120);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(220);
            Pump(window);

            Check(Selected() == IdOf(Items()[0]), "真鼠标点第一张 → 选中它");
            Check(selectionToolbar.Visibility == Visibility.Visible, "选中后工具条露出来");
            double toolbarTop = Canvas.GetTop(selectionToolbar);
            Check(toolbarTop >= 7.5 && toolbarTop < firstRect.Top,
                $"选中工具条摆在选中框上方（top {toolbarTop:0.#} < 框顶 {firstRect.Top:0.#}）");

            var rotateCw = (Button)Field(window, "RotateCwBtn")!;
            var rotateCcw = (Button)Field(window, "RotateCcwBtn")!;
            var flipH = (Button)Field(window, "FlipHBtn")!;
            var resetTransform = (Button)Field(window, "ResetTransformBtn")!;
            var deleteSelected = (Button)Field(window, "DeleteSelectedBtn")!;

            rotateCw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check((bool)ItemProp(Items()[0], "Transform").GetType().GetProperty("IsIdentity")!
                    .GetValue(ItemProp(Items()[0], "Transform"))! == false, "顺时针旋转不是单位矩阵");
            Check(LW() == 80 && LH() == 300,
                $"转 90° 后第一张 80×120 → 成品 80 宽、120+160+20 高（实际 {LW()}×{LH()}）");
            rotateCcw.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check((bool)ItemProp(Items()[0], "Transform").GetType().GetProperty("IsIdentity")!
                    .GetValue(ItemProp(Items()[0], "Transform"))!, "再逆时针转回来");

            flipH.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(!(bool)ItemProp(Items()[0], "Transform").GetType().GetProperty("IsIdentity")!
                    .GetValue(ItemProp(Items()[0], "Transform"))!, "水平翻转生效");
            resetTransform.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check((bool)ItemProp(Items()[0], "Transform").GetType().GetProperty("IsIdentity")!
                    .GetValue(ItemProp(Items()[0], "Transform"))!, "「重置变换」把旋转 / 翻转清掉");

            // ---- 拖动 = 手摆位置（中心偏移）----
            firstRect = View(Rects()[0]);
            var center = new Point(firstRect.X + firstRect.Width / 2, firstRect.Y + firstRect.Height / 2);
            double zoom = (double)preview.GetType().GetProperty("Zoom")!.GetValue(preview)!;
            Drag(ScreenOf(new Rect(center, new Size(0, 0))),
                preview.PointToScreen(new Point(center.X + 30, center.Y + 20)));

            Check(!IsDefaultPlacement(Items()[0]), "拖动后记下手摆位置");
            Check(Math.Abs(PlacementNumber(Items()[0], "OffsetX") - 30 / zoom) < 2.5 &&
                  Math.Abs(PlacementNumber(Items()[0], "OffsetY") - 20 / zoom) < 2.5,
                $"手摆偏移 ≈ (30, 20) DIP → ({PlacementNumber(Items()[0], "OffsetX"):0.#}, " +
                $"{PlacementNumber(Items()[0], "OffsetY"):0.#}) 输出像素");

            // ---- 把手缩放：右中把手往右拖 40 DIP ----
            var frame = (Rect)preview.GetType().GetProperty("SelectionFrame")!.GetValue(preview)!;
            var rightMiddle = new Point(frame.Right, frame.Top + frame.Height / 2);
            Drag(preview.PointToScreen(rightMiddle), preview.PointToScreen(new Point(rightMiddle.X + 40,
                rightMiddle.Y)));
            double scale = PlacementNumber(Items()[0], "Scale");
            Check(scale > 1.05, $"拖右中把手 → 以中心放大（scale {scale:0.##}）");

            resetTransform.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            Check(IsDefaultPlacement(Items()[0]), "「重置变换」也清手摆位置");

            // ---- 拖到另一张上 = 交换 ----
            var idsBefore = Items().Select(IdOf).ToList();
            var rect0 = View(Rects()[0]);
            var rect1 = View(Rects()[1]);
            Drag(preview.PointToScreen(new Point(rect0.X + rect0.Width / 2, rect0.Y + rect0.Height / 2)),
                preview.PointToScreen(new Point(rect1.X + rect1.Width / 2, rect1.Y + rect1.Height / 2)));
            var idsAfter = Items().Select(IdOf).ToList();
            Check(idsAfter.Count == 3 && idsAfter[0] == idsBefore[1] && idsAfter[1] == idsBefore[0],
                "拖到另一张上松手 = 交换两张（不是排序）");

            // ============ 缩略图拖动 = 交换 ============
            var card0 = (FrameworkElement)cards[0]!;
            var card2 = (FrameworkElement)cards[2]!;
            idsBefore = Items().Select(IdOf).ToList();
            Point CardCenter(FrameworkElement card) => card.PointToScreen(
                new Point(card.ActualWidth / 2, card.ActualHeight / 2));
            Drag(CardCenter(card0), CardCenter(card2));
            idsAfter = Items().Select(IdOf).ToList();
            Check(idsAfter[0] == idsBefore[2] && idsAfter[2] == idsBefore[0],
                "缩略图拖到另一格 = 交换（顺序换过去）");

            // ============ 滚轮缩放 / 中键平移 / F 适应 ============
            var wheelAt = preview.PointToScreen(new Point(preview.ActualWidth / 2, preview.ActualHeight / 2));
            SetCursorPos((int)wheelAt.X, (int)wheelAt.Y);
            Thread.Sleep(90);
            Pump(window);
            double zoomBefore = (double)preview.GetType().GetProperty("Zoom")!.GetValue(preview)!;
            preview.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = UIElement.MouseWheelEvent });
            Pump(window);
            double zoomAfter = (double)preview.GetType().GetProperty("Zoom")!.GetValue(preview)!;
            Check(Math.Abs(zoomAfter / zoomBefore - 1.15) < 0.02,
                $"滚轮一格 = 1.15 倍（{zoomBefore:0.###} → {zoomAfter:0.###}）");

            var offsetBefore = (Point)preview.GetType().GetProperty("Offset")!.GetValue(preview)!;
            mouse_event(0x0020 /* 中键按下 */, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            SetCursorPos((int)wheelAt.X + 25, (int)wheelAt.Y + 15);
            Thread.Sleep(80);
            mouse_event(0x0040 /* 中键抬起 */, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(150);
            Pump(window);
            var offsetAfter = (Point)preview.GetType().GetProperty("Offset")!.GetValue(preview)!;
            Check(offsetAfter != offsetBefore, "中键拖动 = 平移画布");

            preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(preview)!, 0,
                Key.F) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(window);
            Check(((Point)preview.GetType().GetProperty("Offset")!.GetValue(preview)!).X <= preview.ActualWidth,
                "F = 适应窗口（画布仍在视野内）");

            // ---- Delete 删掉选中的图 ----
            SetCursorPos((int)clickAt.X, (int)clickAt.Y);
            Thread.Sleep(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(200);
            Pump(window);
            SetTopmost(window, false);

            long selectedId = Selected();
            preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(preview)!, 0,
                Key.Delete) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump(window);
            Check(Items().Count == 2 && Selected() != selectedId, "Delete 删掉选中的那张");
            Check(deleteSelected.IsEnabled, "选中别的图后删除按钮仍可用");

            // ---- Ctrl+V：剪贴板里的位图 ----
            var pasteImage = MakeSolid("paste.png", 24, 16, Colors.Goldenrod);
            clipboardType.GetMethod("SetImage", Stat)!
                .Invoke(null, new object[] { BitmapFrame.Create(new Uri(pasteImage)) });
            InvokeWith(window, "Paste");
            Check(WaitFor(window, () => Items().Count == 3, 8000), "Ctrl+V 把剪贴板图片加进来");
            Pump(window);
            Check(Items().Last().GetType().GetProperty("Label")!.GetValue(Items().Last())!
                    .ToString()!.Contains("剪贴板"), "剪贴板图片标成「剪贴板图像」");

            // ============ 复制到剪贴板（成功照上游关窗）============
            int exportWidth = LW(), exportHeight = LH();
            copyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(WaitFor(window, () => !window.IsVisible, 40000), "复制跑完并关窗");
            Pump(window);

            var clipboardArgs = new object[] { null };
            bool copied = (bool)clipboardType.GetMethod("TryGetImage", Stat)!.Invoke(null, clipboardArgs)!;
            Check(copied, "剪贴板里拿到成品图");
            if (copied && clipboardArgs[0] is BitmapSource pasted)
            {
                Check(pasted.PixelWidth == exportWidth && pasted.PixelHeight == exportHeight,
                    $"剪贴板成品尺寸 {exportWidth}×{exportHeight}（实际 {pasted.PixelWidth}×{pasted.PixelHeight}）");
                var corner = PixelAt(pasted, 1, 1);
                Check(corner.R <= 3 && corner.G <= 3 && corner.A == 255, "左上角是第一张图的像素（不是背景）");
            }

            // ============ 保存（真「另存为」对话框 + 逐像素）============
            string red = MakeSolid("solid-red.png", 120, 80, Colors.Red);
            string blue = MakeSolid("solid-blue.png", 60, 120, Colors.Blue);

            var window2 = (Window)Activator.CreateInstance(windowType)!;
            window2.Show();
            Pump(window2);
            InvokeWith(window2, "PresentFiles", (IReadOnlyList<string>)new[] { red, blue });
            var total2 = (TextBlock)Field(window2, "TotalText")!;
            Check(WaitFor(window2, () => total2.Text == "共 2 张图像", 15000), "第二扇窗装好两张图");

            var verticalB = (RadioButton)Field(window2, "DirVertical")!;
            var uniformB = (RadioButton)Field(window2, "SizeUniform")!;
            var borderB = (TextBox)Field(window2, "BorderBox")!;
            var spacingB = (TextBox)Field(window2, "SpacingBox")!;
            verticalB.IsChecked = true;
            uniformB.IsChecked = true;
            borderB.Text = "0";
            InvokeWith(window2, "CommitField", borderB);
            spacingB.Text = "0";
            InvokeWith(window2, "CommitField", spacingB);
            Pump(window2);
            Check(((TextBlock)Field(window2, "DimensionsText")!).Text == "120 × 320 px",
                "红 120×80 + 蓝 60×120（等比拉到 120×240）→ 120×320");

            string saveDir = Path.Combine(dir, "save");
            if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
            Directory.CreateDirectory(saveDir);
            bool dialogFilled = false;
            var driver = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.GetType().Name == "CaptureSaveWindow");
                if (dialog == null) return;

                ((TextBox)Field(dialog, "FolderBox")!).Text = saveDir;
                ((TextBox)Field(dialog, "NameBox")!).Text = "collage";
                ((Button)Field(dialog, "SaveBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dialogFilled = true;
            }, window2.Dispatcher);
            driver.Start();

            ((Button)Field(window2, "SaveBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            bool savedClosed = WaitFor(window2, () => !window2.IsVisible, 40000);
            driver.Stop();
            Pump(window2);

            Check(dialogFilled, "「保存」弹的是统一的另存为对话框");
            Check(savedClosed, "保存成功后照上游关窗（不再问放弃）");
            string savedPath = Path.Combine(saveDir, "collage.png");
            Check(File.Exists(savedPath), "成品写到选定的位置：" + savedPath);
            if (File.Exists(savedPath))
            {
                var saved = BitmapFrame.Create(new Uri(savedPath));
                Check(saved.PixelWidth == 120 && saved.PixelHeight == 320,
                    $"落盘尺寸 120×320（实际 {saved.PixelWidth}×{saved.PixelHeight}）");
                var top = PixelAt(saved, 10, 10);
                var bottom = PixelAt(saved, 10, 310);
                Check(top.R > 250 && top.G < 5 && top.B < 5, $"上 80 像素是红图（实际 {top}）");
                Check(bottom.B > 250 && bottom.R < 5 && bottom.G < 5, $"下 240 像素是蓝图（实际 {bottom}）");
            }

            // ============ 清空 / 关窗确认 / 会话复用 ============
            var window3 = (Window)Activator.CreateInstance(windowType)!;
            window3.Show();
            Pump(window3);
            InvokeWith(window3, "PresentFiles", (IReadOnlyList<string>)new[] { fileA });
            var total3 = (TextBlock)Field(window3, "TotalText")!;
            Check(WaitFor(window3, () => total3.Text == "共 1 张图像", 15000), "第三扇窗装好一张图");

            bool clearConfirmed = false;
            var clearDriver = new DispatcherTimer(TimeSpan.FromMilliseconds(120),
                DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                    if (dialog == null) return;
                    clearConfirmed = ((TextBlock)Field(dialog, "MessageText")!).Text.Contains("移除全部图像");
                    ((Button)Field(dialog, "OkBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, window3.Dispatcher);
            clearDriver.Start();
            ((Button)Field(window3, "ClearBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            clearDriver.Stop();
            Pump(window3);
            Check(clearConfirmed, "清空前会确认「将从此图像拼接中移除全部图像」");
            Check(total3.Text == "共 0 张图像", "清空后 0 张：" + total3.Text);

            // 有图关窗：先问「放弃此图像拼接？」，选取消就留着
            InvokeWith(window3, "PresentFiles", (IReadOnlyList<string>)new[] { fileA });
            Check(WaitFor(window3, () => total3.Text == "共 1 张图像", 15000), "再装一张图");

            bool closeAsked = false, closeCancelled = false;
            var closeDriver = new DispatcherTimer(TimeSpan.FromMilliseconds(120),
                DispatcherPriority.Background, (_, _) =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                    if (dialog == null) return;
                    closeAsked = ((Button)Field(dialog, "OkBtn")).Content?.ToString()?.Contains("放弃") == true;
                    if (!closeCancelled)
                    {
                        closeCancelled = true;
                        ((Button)Field(dialog, "CancelBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        return;
                    }

                    ((Button)Field(dialog, "OkBtn")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }, window3.Dispatcher);
            closeDriver.Start();
            window3.Close();
            Pump(window3);
            Check(closeAsked && window3.IsVisible, "关窗先问「放弃此图像拼接？」，点取消就留着");

            window3.Close();
            Pump(window3);
            closeDriver.Stop();
            Check(!window3.IsVisible, "再关一次并确认 → 窗口关掉");

            // 会话复用：连点两次「图像拼接…」只留一个空窗
            var openList = (System.Collections.IList)sessionType
                .GetField("Open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            Check(openList.Count == 0, "关掉的窗口从登记表里摘掉");

            sessionType.GetMethod("OpenEmpty", Stat)!.Invoke(null, null);
            sessionType.GetMethod("OpenEmpty", Stat)!.Invoke(null, null);
            Pump(window);
            Check(openList.Count == 1, $"托盘「图像拼接…」连点两次只留一个空窗（实际 {openList.Count}）");

            // 不是图片的文件：不新建窗、也不把空窗关掉
            string text = Path.Combine(dir, "not-image.txt");
            File.WriteAllText(text, "x");
            sessionType.GetMethod("OpenFiles", Stat)!
                .Invoke(null, new object[] { (IReadOnlyList<string>)new[] { text } });
            Pump(window);
            Check(openList.Count == 1, "拖进不是图片的文件不会新开窗");

            // ============ 入口：贴图右键「拼接图像…」============
            var managerType = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
            var shot = BitmapFrame.Create(new Uri(fileC));
            var pin = (Window?)managerType.GetMethod("Pin", Stat)!.Invoke(null, new object[]
            {
                shot, new Int32Rect(120, 120, shot.PixelWidth, shot.PixelHeight), "screenshot", null,
            });
            Check(pin is { IsVisible: true }, "造一张贴图（用来验菜单入口）");
            if (pin != null)
            {
                var menu = (ContextMenu)pin.GetType()
                    .GetMethod("BuildMenu", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(pin, null)!;
                var entry = menu.Items.OfType<MenuItem>()
                    .FirstOrDefault(mi => (mi.Header?.ToString() ?? "").StartsWith("拼接图像"));
                Check(entry is { IsEnabled: true }, "贴图右键有「拼接图像…」且可点");

                if (entry != null)
                {
                    var target = (Window)openList[openList.Count - 1]!;
                    var targetTotal = (TextBlock)Field(target, "TotalText")!;
                    entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Check(WaitFor(pin, () => targetTotal.Text == "共 1 张图像", 15000),
                        "点「拼接图像…」把这张加进开着的拼接窗：" + targetTotal.Text);
                    var docOfTarget = Field(target, "_doc")!;
                    docOfTarget.GetType().GetMethod("Clear")!.Invoke(docOfTarget, null);
                    target.Close();
                    Pump(target);
                }

                pin.Close();
                Pump(pin);
            }

            foreach (var item in openList.Cast<object>().ToList()) ((Window)item).Close();
        }
        finally
        {
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}
