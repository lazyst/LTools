using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 工具栏「每一项都要能用」的端到端点名：逐个工具真鼠标画一笔，断言落下来的标注类型。
/// </summary>
/// <remarks>
/// 上游每个工具都是一条独立的手势实现（<c>snow_draw_engine_*</c>），参数条只是给它换样式；
/// 所以这里按「工具 → 期望的标注类型」逐条点名，漏一个、错一个都会红。工具清单与
/// <c>ToolCatalog.All</c> 对齐，新增工具时这里也要加一行。
/// </remarks>
internal static partial class Program
{
    private static void ToolSweepSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var setTool = typeof(CaptureOverlayWindow).GetMethod("SetTool", Inst)!;

        var shot = BuildShot(900, 700);
        var monitor = new System.Drawing.Rectangle(0, 0, 1200, 800);

        var window = NewOverlay(shot, monitor);
        window.Show();
        window.Activate();
        Pump(window);
        SetSelection(window, new Rect(20, 20, 800, 620));
        typeof(CaptureOverlayWindow).GetMethod("SyncArea", Inst)!.Invoke(window, null);
        Pump(window);

        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Inst)!.GetValue(window)!;
        var marks = (IList)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
        var clearMarks = doc.GetType().GetMethod("ClearMarks", Inst)!;

        void Clear()
        {
            clearMarks.Invoke(doc, null);
            Pump(window);
        }

        void PointAt(Point dip, int settle = 140)
        {
            Point screen = window.PointToScreen(dip);
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            Thread.Sleep(settle);
            Pump(window);
        }

        void Drag(Point from, Point to)
        {
            PointAt(from);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(70);
            PointAt(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), 60);
            PointAt(to, 90);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(160);
            Pump(window);
        }

        void Click(Point at)
        {
            PointAt(at);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(70);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(160);
            Pump(window);
        }

        string LastType() => marks.Count == 0 ? "(无)" : marks[marks.Count - 1]!.GetType().Name;

        void Sweep(string tool, Action gesture, string expected)
        {
            Clear();
            setTool.Invoke(window, new[] { Enum.Parse(toolType, tool) });
            Pump(window);
            gesture();

            Check(marks.Count == 1, $"{tool}：操作一次就落一条标注（实际 {marks.Count} 条）");
            if (marks.Count == 1)
                Check(LastType() == expected, $"{tool}：落的是 {expected}（实际 {LastType()}）");
        }

        // ---- 拖一下就成形的工具 ----
        Sweep("Shape", () => Drag(new Point(120, 120), new Point(320, 260)), "Shape");
        Sweep("SpeechBubble", () => Drag(new Point(120, 120), new Point(320, 260)), "SpeechBubble");
        Sweep("Arrow", () => Drag(new Point(120, 120), new Point(320, 260)), "Arrow");
        Sweep("Line", () => Drag(new Point(120, 120), new Point(320, 260)), "Line");
        Sweep("Distance", () => Drag(new Point(120, 120), new Point(320, 260)), "Distance");
        Sweep("Bracket", () => Drag(new Point(120, 120), new Point(320, 260)), "Bracket");
        Sweep("Pen", () => Drag(new Point(120, 120), new Point(320, 260)), "Stroke");
        Sweep("Highlighter", () => Drag(new Point(120, 120), new Point(320, 260)), "Stroke");
        Sweep("HighlightRect", () => Drag(new Point(120, 120), new Point(320, 260)), "Highlight");
        Sweep("Spotlight", () => Drag(new Point(120, 120), new Point(320, 260)), "Spotlight");
        Sweep("Magnifier", () => Drag(new Point(120, 120), new Point(320, 260)), "Magnifier");
        Sweep("Mosaic", () => Drag(new Point(120, 120), new Point(320, 260)), "Mosaic");
        Sweep("Filter", () => Drag(new Point(120, 120), new Point(320, 260)), "Filter");
        Sweep("BrushFilter", () => Drag(new Point(120, 120), new Point(320, 260)), "BrushFilter");

        // ---- 多点工具 ----
        // 测角：拖出第一条臂 → 松手；再从顶点拖第二条臂 → 松手才落一个角
        Sweep("Angle", () =>
        {
            Drag(new Point(120, 200), new Point(120, 120));
            Drag(new Point(120, 200), new Point(300, 200));
        }, "Angle");

        // 折线：连点几下，最后双击结束
        Sweep("Polyline", () =>
        {
            Click(new Point(120, 260));
            Click(new Point(240, 140));
            Click(new Point(360, 260));
            Click(new Point(360, 260));
        }, "Polyline");

        // ---- 单击工具 ----
        Sweep("Serial", () => Click(new Point(200, 200)), "Serial");
        Sweep("Watermark", () => Click(new Point(200, 200)), "Watermark");

        // 取色：点一下把颜色写进剪贴板，不落标注
        Clear();
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Picker") });
        Pump(window);
        System.Windows.Clipboard.Clear();
        Click(new Point(220, 220));
        Check(marks.Count == 0, $"Picker：取色不落标注（实际 {marks.Count} 条）");
        string picked = "";
        try { picked = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : ""; }
        catch (Exception) { }
        Check(picked.Length > 0, "Picker：点一下就把颜色写进剪贴板（实际为空）");

        // ---- 橡皮：画一条 → 擦掉（落的是「恢复原图」块，不是新标注）----
        void EraseSweep(string tool, Action gesture)
        {
            Clear();
            setTool.Invoke(window, new[] { Enum.Parse(toolType, "Shape") });
            Pump(window);
            Drag(new Point(120, 120), new Point(320, 260));
            Check(marks.Count == 1, $"{tool}：先画一条形状当靶子（实际 {marks.Count} 条）");

            setTool.Invoke(window, new[] { Enum.Parse(toolType, tool) });
            Pump(window);
            gesture();

            // 上游橡皮落一条 RestoreBackground：靶子还在，但被恢复块盖住（渲染时还原原图像素）
            Check(marks.Count == 2 && LastType() == "Restore",
                $"{tool}：落一条「恢复原图」块（实际 {marks.Count} 条，最后一条 {LastType()}）");
        }

        EraseSweep("RectEraser", () => Drag(new Point(100, 100), new Point(340, 280)));
        EraseSweep("BrushEraser", () => Drag(new Point(130, 140), new Point(300, 240)));

        // 点选橡皮：点中靶子 = 按对象删掉
        Clear();
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Shape") });
        Pump(window);
        Drag(new Point(120, 120), new Point(320, 260));
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Eraser") });
        Pump(window);
        Click(new Point(220, 120));
        Check(marks.Count == 0, $"Eraser：点中形状按对象删掉（实际还剩 {marks.Count} 条）");

        // 复原：清空标注，别把靶子留给后面的段
        Clear();
        window.Close();
        Pump(window);
    }
}
