using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 选区形状（P9-m，照上游 <c>screenshot_selection/region_type</c> 的四档）：
/// 形状条四档 / 跨会话记住 / 真鼠标画曲线·自由绘制·折线·矩形 /
/// 导出按路径裁（形状外透明）/ 拖动整条。
/// </summary>
/// <remarks>
/// 纯几何（id 映射 / 化简 / 向心 Catmull-Rom / 命中）在 <c>--selftest</c> 的
/// 「选区形状」一组里逐条断言，这里只验真窗口与真鼠标。
/// </remarks>
internal static partial class Program
{
    private static void RegionTypeSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var selectionStore = asm.GetType("LTools.Features.CapturePro.SelectionStore")!;
        var flush = selectionStore.GetMethod("Flush", Stat)!;
        var forget = selectionStore.GetMethod("Forget", Stat)!;

        // 临时配置锚点：这一段会写 ui.json 的 CaptureSelection.RegionType（形状跨会话记住）
        string root = Path.Combine(_outDir, "regiontype-config");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "configs"));
        string uiFile = Path.Combine(root, "configs", "ui.json");

        configPathSet.Invoke(null, new object[] { Path.Combine(root, "LTools.json") });
        forget.Invoke(null, null);

        var shapes = asm.GetType("LTools.Features.CapturePro.SelectionMode")!;
        var shot = BuildShot(900, 700);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, 900, 700));

        try
        {
            window.Show();
            window.Activate();
            Pump(window);

            var regionsField = typeof(CaptureOverlayWindow).GetField("_regions", Inst)!;
            var selField = typeof(CaptureOverlayWindow).GetField("_sel", Inst)!;
            var surface = Field(window, "Surface")!;
            var doc = Field(window, "_doc")!;

            System.Collections.IList Regions() => (System.Collections.IList)regionsField.GetValue(window)!;
            Rect? Sel() => selField.GetValue(window) as Rect?;

            // ============ 真鼠标 ============
            Point Screen(double x, double y) => window.PointToScreen(new Point(x, y));

            void Move(Point at, int settle = 70)
            {
                SetCursorPos((int)Math.Round(at.X), (int)Math.Round(at.Y));
                Thread.Sleep(settle);
                Pump(window);
            }

            void Click(Point at)
            {
                Move(at, 110);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(140);
                Pump(window);
            }

            void Drag(Point from, Point to, int steps = 14)
            {
                Move(from, 110);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                Pump(window);
                for (int i = 1; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    SetCursorPos((int)Math.Round(from.X + (to.X - from.X) * t),
                        (int)Math.Round(from.Y + (to.Y - from.Y) * t));
                    Thread.Sleep(18);
                    Pump(window);
                }

                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(180);
                Pump(window);
            }

            /// <summary>按住走一条折线（自由绘制：随手画一圈，松手自动闭合）。</summary>
            void Stroke(params Point[] dip)
            {
                Point[] screen = dip.Select(p => Screen(p.X, p.Y)).ToArray();
                Move(screen[0], 110);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(45);
                for (int i = 1; i < screen.Length; i++)
                {
                    for (int step = 1; step <= 6; step++)
                    {
                        double t = step / 6.0;
                        SetCursorPos(
                            (int)Math.Round(screen[i - 1].X + (screen[i].X - screen[i - 1].X) * t),
                            (int)Math.Round(screen[i - 1].Y + (screen[i].Y - screen[i - 1].Y) * t));
                        Thread.Sleep(18);
                        Pump(window);
                    }
                }

                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(200);
                Pump(window);
            }

            void Press(Key key)
            {
                var source = PresentationSource.FromVisual(window)!;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump(window);
            }

            void Shape(string name)
            {
                Invoke(window, "ClearSelection");
                InvokeWith(window, "SetSelectionShape", Enum.Parse(shapes, name));
                Pump(window);
                flush.Invoke(null, null);
            }

            BitmapSource Export(out Int32Rect area)
            {
                Invoke(window, "SyncArea");
                Pump(window);
                area = (Int32Rect)doc.GetType().GetProperty("Area")!.GetValue(doc)!;
                return (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
            }

            string SavedRegionType()
            {
                if (!File.Exists(uiFile)) return "";
                using var json = JsonDocument.Parse(File.ReadAllText(uiFile));
                if (!json.RootElement.TryGetProperty("CaptureSelection", out var section)) return "";
                return section.TryGetProperty("RegionType", out var value)
                    ? value.GetString() ?? ""
                    : "";
            }

            // ============ ① 形状条：四档 + 顺序（照上游 ScreenshotRegionTypeControl） ============
            var shapeBar = (Border)Field(window, "ShapeBar")!;
            var labels = ((StackPanel)shapeBar.Child).Children.OfType<RadioButton>()
                .Select(r => r.Content?.ToString() ?? "").ToList();
            Check(labels.SequenceEqual(new[] { "矩形", "折线", "曲线", "自由绘制" }),
                "形状条是矩形 / 折线 / 曲线 / 自由绘制：" + string.Join("/", labels));
            Check(((RadioButton)Field(window, "ShapeCurve")!).GroupName == "capShape",
                "曲线按钮与其它三档同一组（互斥）");
            if (shapeBar.Visibility == Visibility.Visible)
                Save(CropElement(window, shapeBar), Path.Combine(_outDir, "shapebar-4.png"));

            // ============ ② 曲线：真鼠标逐点单击 + Enter 收尾 ============
            Shape("Curve");
            Check(Field(window, "_shape")!.ToString() == "Curve", "切到曲线");
            Check(((RadioButton)Field(window, "ShapeCurve")!).IsChecked == true, "「曲线」按钮点亮");
            Check(SavedRegionType() == "curve", "曲线落盘成 region_type = curve（实际 " + SavedRegionType() + "）");

            Click(Screen(200, 150));
            Click(Screen(700, 150));
            Click(Screen(700, 500));
            Check((bool)Field(window, "_selPolyActive")!, "真鼠标点了三个点：曲线正在画");
            var pending = surface.GetType().GetProperty("PendingPolygon")!.GetValue(surface) as Point[];
            Check(pending is { Length: >= 3 }, "画面上有那条跟手虚线");
            Check((bool)surface.GetType().GetProperty("PendingSmooth")!.GetValue(surface)!,
                "曲线的跟手虚线是平滑的");

            Click(Screen(200, 500));
            Press(Key.Enter);
            Pump(window);

            Check(Regions().Count == 1, $"曲线收尾得到 1 块选区（实际 {Regions().Count}）");
            var curveRegion = Regions()[0]!;
            Check((bool)curveRegion.GetType().GetProperty("Smooth")!.GetValue(curveRegion)!,
                "曲线选区记下了「平滑」");
            var curvePoints = curveRegion.GetType().GetProperty("Points")!.GetValue(curveRegion) as Point[];
            Check(curvePoints is { Length: 4 }, $"四个点都进去了（实际 {curvePoints?.Length}）");

            var curveBounds = (Rect)curveRegion.GetType().GetProperty("Bounds")!.GetValue(curveRegion)!;
            Check(curveBounds.Width >= 500 && curveBounds.Height >= 350,
                $"外接框按点列算：{curveBounds.Width:0.#} × {curveBounds.Height:0.#}");
            Check(curveBounds.Width > 500.5 || curveBounds.Height > 350.5,
                $"外接框算上了拐角外凸（Catmull-Rom）：{curveBounds.Width:0.##} × {curveBounds.Height:0.##}");

            var curveGeometry = surface.GetType().GetProperty("SelectionGeometry")!.GetValue(surface) as Geometry;
            Check(curveGeometry != null, "画面拿到曲线的几何");
            Check(curveGeometry?.FillContains(new Point(450, 325)) == true, "曲线中间在形状里");
            Check(curveGeometry?.FillContains(new Point(100, 325)) == false, "选区左边一大截在形状外");
            Save(Render(window), Path.Combine(_outDir, "regiontype-curve.png"));

            var curveShot = Export(out var curveArea);
            Check(curveArea.Width >= 501 && curveArea.Height >= 351,
                $"导出尺寸跟曲线外接框走：{curveArea.Width} × {curveArea.Height}");
            Save(curveShot, Path.Combine(_outDir, "regiontype-curve-export.png"));
            Check(AlphaAt(curveShot, 0, 0) < 40,
                $"曲线导出：左上角透明（alpha {AlphaAt(curveShot, 0, 0)}）");
            Check(AlphaAt(curveShot, curveShot.PixelWidth - 1, curveShot.PixelHeight - 1) < 40,
                "曲线导出：右下角透明");
            Check(AlphaAt(curveShot, curveShot.PixelWidth / 2, curveShot.PixelHeight / 2) > 200,
                "曲线导出：中间保留底图");

            // ============ ③ 拖动整条：平滑标记与形状都不丢 ============
            // 往左上拖：曲线选区的外接框比窗口还宽一截，往右下会被 MoveTo 的「留在窗口里」夹住。
            // 按住 Ctrl 拖动 = 关掉吸附，位移才是精确的 50。
            var before = Sel();
            keybd_event(0x11 /* VK_CONTROL */, 0, 0, UIntPtr.Zero);
            try
            {
                Drag(Screen(450, 325), Screen(400, 275));
            }
            finally
            {
                keybd_event(0x11, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
            }

            var after = Sel();
            Check(before is Rect b0 && after is Rect b1 &&
                  Math.Abs(b1.X - b0.X + 50) <= 2 && Math.Abs(b1.Y - b0.Y + 50) <= 2,
                $"拖选区内 = 整条平移 -50,-50（{before} → {after}）");
            var moved = Regions()[0]!;
            Check((bool)moved.GetType().GetProperty("Smooth")!.GetValue(moved)!,
                "平移之后还是平滑曲线");
            Check(moved.GetType().GetProperty("Points")!.GetValue(moved) is Point[] mp && mp.Length == 4,
                "平移不改点数");

            // ============ ④ 自由绘制：按住拖一圈（真鼠标） ============
            Shape("Lasso");
            Check(SavedRegionType() == "freehand", "自由绘制落盘成 freehand");
            Stroke(new Point(250, 200), new Point(650, 200), new Point(650, 520),
                new Point(250, 520), new Point(255, 210));
            Check(Regions().Count == 1, $"自由绘制松手得到 1 块选区（实际 {Regions().Count}）");
            var free = Regions()[0]!;
            Check((bool)free.GetType().GetProperty("Smooth")!.GetValue(free)!,
                "自由绘制也平滑成曲线");
            var freePoints = free.GetType().GetProperty("Points")!.GetValue(free) as Point[];
            Check(freePoints is { Length: >= 3 }, $"自由绘制的点够用（实际 {freePoints?.Length}）");
            Save(Render(window), Path.Combine(_outDir, "regiontype-freehand.png"));

            var freeShot = Export(out _);
            Check(AlphaAt(freeShot, 0, 0) < 40,
                $"自由绘制导出：左上角透明（alpha {AlphaAt(freeShot, 0, 0)}）");
            Check(AlphaAt(freeShot, freeShot.PixelWidth / 2, freeShot.PixelHeight / 2) > 200,
                "自由绘制导出：中间保留底图");

            // ============ ⑤ 折线：逐点单击（不平滑） ============
            Shape("Polyline");
            Check(SavedRegionType() == "polyline", "折线落盘成 polyline");
            Click(Screen(300, 200));
            Click(Screen(700, 260));
            Click(Screen(600, 480));
            Press(Key.Enter);
            Pump(window);
            Check(Regions().Count == 1, $"折线收尾得到 1 块选区（实际 {Regions().Count}）");
            var poly = Regions()[0]!;
            Check(!(bool)poly.GetType().GetProperty("Smooth")!.GetValue(poly)!,
                "折线不平滑（还是一段一段的直线）");
            var polyShot = Export(out _);
            Check(AlphaAt(polyShot, 0, polyShot.PixelHeight - 1) < 40, "折线导出：左下角透明");
            Check(AlphaAt(polyShot, polyShot.PixelWidth / 2, polyShot.PixelHeight / 2) > 200,
                "折线导出：中间保留底图");

            // ============ ⑥ 矩形：拖框（真鼠标），导出四角不透明 ============
            Shape("Rect");
            Check(SavedRegionType() == "rectangle", "矩形落盘成 rectangle");
            Drag(Screen(250, 200), Screen(650, 500));
            Check(Regions().Count == 1, $"矩形拖框得到 1 块选区（实际 {Regions().Count}）");
            var rectRegion = Regions()[0]!;
            Check(!(bool)rectRegion.GetType().GetProperty("IsPolygon")!.GetValue(rectRegion)!,
                "矩形不是点列块（圆角 / 八把手照旧）");
            var rectShot = Export(out var rectArea);
            Check(rectArea.Width >= 400 && rectArea.Height >= 300,
                $"矩形导出尺寸 = 选区：{rectArea.Width} × {rectArea.Height}");
            Check(AlphaAt(rectShot, 0, 0) > 200, "矩形导出：左上角不透明（没有按路径裁）");

            // ============ ⑦ 右键取消选区 ============
            Invoke(window, "ClearSelection");
            Pump(window);
            Check(Regions().Count == 0 && Sel() == null, "取消选区之后什么都没留下");
            Check(shapeBar.Visibility == Visibility.Visible, "没选区时形状条又出现");

            // ============ ⑧ 设置页回显：上次用的形状 ============
            InvokeWith(window, "SetSelectionShape", Enum.Parse(shapes, "Curve"));
            flush.Invoke(null, null);

            var settings = new LTools.Views.ConfigHelperWindow(Path.Combine(root, "LTools.json"));
            try
            {
                settings.Show();
                Pump(settings);
                typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!
                    .SetValue(settings, false);
                typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateOverlayUi", Inst)!
                    .Invoke(settings, null);

                var combo = (ComboBox)settings.FindName("CaptureRegionTypeCombo")!;
                Check(combo.SelectedIndex == 2,
                    $"设置页回显上次的曲线（实际第 {combo.SelectedIndex} 项）");
                var items = combo.Items.Cast<ComboBoxItem>().Select(i => i.Content?.ToString() ?? "").ToList();
                Check(items.Count == 4 && items[0].StartsWith("矩形") && items[2].StartsWith("曲线"),
                    "设置页四档顺序：矩形 / 折线 / 曲线 / 自由绘制");
            }
            finally
            {
                settings.Close();
            }
        }
        finally
        {
            window.Close();
            forget.Invoke(null, null);          // 把这一段写的 ui.json 落进临时锚点，再换回原锚点
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}
