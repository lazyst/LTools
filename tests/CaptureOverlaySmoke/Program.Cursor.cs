using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 「显示光标」（照上游 <c>screenshot/capture_cursor</c>）：工具条那一格「显示光标」、
/// 快捷键 <c>toggle_cursor_visibility</c>、以及成品图里真的多出一块指针像素。
/// </summary>
/// <remarks>
/// 真窗口 + 真渲染：抓帧那一刻的光标取决于跑冒烟时的鼠标状态，所以这里**注入**一份已知的
/// 光标小图（<c>LoadCursorFromHistory</c>，与「重新编辑截图」同一条路），再看 <c>Render()</c>
/// 出来的成品在偏移处是不是那块颜色。这样断言与机器上有没有鼠标、光标是什么形状都无关。
/// </remarks>
internal static partial class Program
{
    private static void CursorSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var shotType = asm.GetType("LTools.Features.CapturePro.ScreenShot")!;
        var area = (Int32Rect)shotType.GetMethod("Bounds", Stat)!.Invoke(null, null)!;
        var monitor = new System.Drawing.Rectangle(area.X, area.Y, area.Width, area.Height);
        var shot = (BitmapSource)shotType.GetMethod("Capture", Stat)!
            .Invoke(null, new object[] { area, false })!;

        var overlay = NewOverlay(shot, monitor);
        overlay.Show();
        PumpFor(450);

        try
        {
            // 整块屏都算选区：指针放哪儿都在成品里
            SetSelection(overlay, new Rect(0, 0,
                Math.Max(20, overlay.ActualWidth - 2), Math.Max(20, overlay.ActualHeight - 2)));
            Invoke(overlay, "SyncArea");     // 平时由「完成」那一步同步，这里要直接 Render
            PumpFor(150);

            var doc = Field(overlay, "_doc")!;
            object Prop(string name) => doc.GetType()
                .GetProperty(name, Private | BindingFlags.Public)!.GetValue(doc)!;
            object? Call(string name, params object[] args) => doc.GetType()
                .GetMethod(name, Private | BindingFlags.Public)!.Invoke(doc, args);

            bool available() => (bool)Prop("CursorAvailable");
            bool visible() => (bool)Prop("CursorVisible");

            // 入口在选区信息条右端（照上游样式条：显示光标 / 重新截图），不在「识别」那一叠里
            var toolbar = (FrameworkElement)Field(overlay, "Toolbar");
            var cursorBtn = (FrameworkElement)Field(overlay, "SelCursorBtn");
            var recaptureBtn = (FrameworkElement)Field(overlay, "SelRecaptureBtn");
            var cursorIcon = (System.Windows.Shapes.Path)Field(overlay, "SelCursorIcon");
            Check(cursorBtn.ToolTip is string tip && tip.Contains("显示光标"),
                "信息条上有「显示光标」：" + cursorBtn.ToolTip);
            Check(recaptureBtn.ToolTip is string rtip && rtip.Contains("Alt+R"),
                "信息条上有「重新截图」：" + recaptureBtn.ToolTip);
            Check(ByName(toolbar, "CaptureCursorBtn") == null,
                "工具条的完成动作里没有「显示光标」了（别再塞进识别那一叠）");

            Color fill() => (cursorIcon.Fill as SolidColorBrush)?.Color ?? Colors.Transparent;

            // 注入光标：偏移 (40, 30)，20 × 20 的纯红块
            var patch = Solid(20, 20, Colors.Red);
            overlay.GetType().GetMethod("LoadCursorFromHistory", Private)!
                .Invoke(overlay, new object[] { patch, new Point(40, 30), true });
            PumpFor(120);
            Check(available(), "注入之后有指针可画");
            Check(visible(), "默认按传进来的 visible 打开");
            Check(cursorBtn.Opacity == 1 && fill() == Color.FromRgb(0x16, 0x77, 0xFF),
                $"信息条上的「显示光标」跟着亮：opacity={cursorBtn.Opacity} fill={fill()}");

            // 成品：偏移 (40,30) 处应当是红块（不是红块说明没画进去）
            var rendered = (BitmapSource)Call("Render")!;
            Color at = PixelAt(rendered, 48, 38);
            Check(at.R > 200 && at.G < 60 && at.B < 60,
                $"成品里画了指针（(48,38) = #{at.R:X2}{at.G:X2}{at.B:X2}）");
            Color outside = PixelAt(rendered, 100, 100);
            Check(!(outside.R > 200 && outside.G < 60 && outside.B < 60),
                $"指针只画在自己那一块（(100,100) = #{outside.R:X2}{outside.G:X2}{outside.B:X2}）");

            // 信息条上点一下 = 关掉：成品里那块红没了（走的就是真点击那条处理）
            InvokeValue(overlay, "HandleSelectionBarClick", cursorBtn);
            PumpFor(120);

            Check(!visible(), "点一下就不再画");
            Check(fill() == Colors.White, $"关掉之后图标回到白色（{fill()}）");
            var without = (BitmapSource)Call("Render")!;
            Color gone = PixelAt(without, 48, 38);
            Check(!(gone.R > 200 && gone.G < 60 && gone.B < 60),
                $"关掉之后成品里没有指针（(48,38) = #{gone.R:X2}{gone.G:X2}{gone.B:X2}）");

            // 再点一下 = 打开；快捷键 toggle_cursor_visibility（`）也能切
            InvokeValue(overlay, "HandleSelectionBarClick", cursorBtn);
            PumpFor(80);
            Check(visible(), "再点一下又打开了");

            Invoke(overlay, "ToggleCursorVisibility");
            PumpFor(80);
            Check(!visible() && fill() == Colors.White, "快捷键切完信息条上的图标跟着灭");
            Invoke(overlay, "ToggleCursorVisibility");
            PumpFor(80);
            Check(visible() && fill() == Color.FromRgb(0x16, 0x77, 0xFF), "快捷键也能再打开");

            // 换底图（LoadBase，翻采集历史走的就是它）会把旧指针清掉
            Call("LoadBase", shot, new Int32Rect(area.X, area.Y, area.Width, area.Height), 1.0,
                new Point(0, 0));
            PumpFor(60);
            Check(!available(), "换底图之后旧指针不留着");

            // ===== 采集历史：光标快照跟着记录落盘，「重新编辑截图」照它摆回来 =====
            overlay.GetType().GetMethod("LoadCursorFromHistory", Private)!
                .Invoke(overlay, new object[] { patch, new Point(40, 30), true });

            var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;
            var editType = asm.GetType("LTools.Features.CapturePro.CaptureEditState")!;

            object edit = CallStatic(editType, "Of", doc, Prop("UndoStack"), Prop("RedoStack"))!;
            object? EditProp(string name) => editType.GetProperty(name)!.GetValue(edit);
            Check(EditProp("Cursor") is BitmapSource, "编辑现场里带着光标小图");
            Check(40 == (int)EditProp("CursorX")! && 30 == (int)EditProp("CursorY")!,
                $"偏移按图像像素记账（{EditProp("CursorX")},{EditProp("CursorY")}）");
            Check((bool)EditProp("CursorVisible")!, "开关状态也一起记住");

            object entry = CallStatic(catalogType, "Create", 4, 3, "shot", "smoke-cursor",
                null, null, null, edit)!;
            string id = (string)entry.GetType().GetProperty("Id")!.GetValue(entry)!;

            try
            {
                var saved = (BitmapSource)EditProp("Cursor")!;
                CallStatic(catalogType, "SaveCursor", entry, saved);

                Check((string)entry.GetType().GetProperty("Cursor")!.GetValue(entry)! ==
                      "cursor/" + id + ".png",
                    "条目里记的是 cursor/<id>.png");
                Check((bool)entry.GetType().GetProperty("CursorVisible")!.GetValue(entry)!,
                    "条目里也记着画不画");

                var back = (BitmapSource?)CallStatic(catalogType, "LoadCursor", entry);
                Check(back != null, "读得回来");
                if (back != null)
                {
                    Color pixel = PixelAt(back, 5, 5);
                    Check(pixel.R > 200 && pixel.G < 60 && pixel.B < 60,
                        $"读回来的就是那份指针（#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}）");
                }

                // 删掉这条：小图跟着删，不留孤儿
                string path = (string)CallStatic(catalogType, "CursorPath", id)!;
                CallStatic(catalogType, "Remove", id);
                Check(!System.IO.File.Exists(path), "删记录时把光标小图也删掉");
            }
            finally
            {
                CallStatic(catalogType, "Remove", id);
            }
        }
        finally
        {
            overlay.Close();
            PumpFor(120);
        }
    }

    /// <summary>按 <c>x:Name</c> 在可视树里找元素（模板 / 代码生成的按钮也能找到）。</summary>
    private static FrameworkElement? ByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = ByName(VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }

        return null;
    }

    /// <summary>一块纯色的小图（当注入的光标用）。</summary>
    private static BitmapSource Solid(int width, int height, Color color)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }
}
