using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 重新截图（recapture）：照上游 <c>ScreenshotController::requestRecapture</c> 的**原位重抓**——
/// 只换底图，选区 / 标注 / 撤销栈都不动，而且覆盖层自己不出现在新抓的画面里。
/// </summary>
/// <remarks>
/// 「覆盖层没被截进去」用**压暗**判定：覆盖层把选区之外整块桌面压暗 40%，所以重抓之后
/// 选区外那一块的像素必须和第一次抓帧**基本一样**；要是把覆盖层也抓进去了，那块会明显变暗。
/// </remarks>
internal static partial class Program
{
    private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static void RecaptureSection()
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
            // 选区放在右下：左上那块（采样点）落在覆盖层的「压暗」区域里
            SetSelection(overlay,
                new Rect(Math.Max(40, overlay.ActualWidth - 420),
                    Math.Max(40, overlay.ActualHeight - 320), 300, 200));
            PumpFor(150);

            var doc = Field(overlay, "_doc")!;
            object Prop(string name) =>
                doc.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(doc)!;
            void Call(string name, params object[] args) =>
                doc.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(doc, args);
            int Marks() => ((System.Collections.IEnumerable)Prop("Marks")!).Cast<object>().Count();

            Int32Rect areaBefore = (Int32Rect)Prop("Area");
            int marksBefore = Marks();

            var markType = asm.GetType("LTools.Features.CapturePro.Mark+Shape")!;
            object mark = Activator.CreateInstance(markType, new object[]
            {
                new Point(10, 10), new Point(60, 40), Colors.Red, 2.0, false, false,
            })!;
            Call("Add", mark);
            PumpFor(100);
            Check(Marks() == marksBefore + 1, "重抓前先画一笔");

            // 入口照上游样式条：在选区信息条右端（「显示光标 / 重新截图」），不在完成动作那一叠里
            var cursorBtn = (FrameworkElement)Field(overlay, "SelCursorBtn");
            var recaptureBtn = (FrameworkElement)Field(overlay, "SelRecaptureBtn");
            Check(cursorBtn.ToolTip is string ctip && ctip.Contains("显示光标"),
                "信息条上有「显示光标」：" + cursorBtn.ToolTip);
            Check(recaptureBtn.ToolTip is string rtip && rtip.Contains("Alt+R"),
                "信息条上有「重新截图」，说明带快捷键：" + recaptureBtn.ToolTip);
            Check(recaptureBtn.Opacity == 1, "空闲时不是灰的");
            Check(((FrameworkElement)Field(overlay, "SelBar")).Visibility == Visibility.Visible,
                "有选区、「选择」工具下信息条可见");

            BitmapSource baseBefore = (BitmapSource)Prop("Base");
            double[] before = Block(baseBefore, 24, 24, 32);

            var task = (Task<bool>)InvokeValue(overlay, "RecaptureAsync");
            Check(WaitUntil(() => task.IsCompleted, 15000), "重新截图 15 秒内完成");
            Check(task.IsCompletedSuccessfully && task.Result, "重新截图成功");

            BitmapSource baseAfter = (BitmapSource)Prop("Base");
            Check(!ReferenceEquals(baseBefore, baseAfter), "底图换成了新抓的一张");
            Check(baseAfter.PixelWidth == baseBefore.PixelWidth &&
                  baseAfter.PixelHeight == baseBefore.PixelHeight, "底图尺寸不变");
            Check((Int32Rect)Prop("Area") == areaBefore, "选区不动");
            Check(Marks() == marksBefore + 1, "标注还在");
            Check((bool)Prop("CanUndo"), "撤销栈还在（重抓不算一次编辑）");
            Check(!(bool)Field(overlay, "_recaptureBusy"), "重抓结束、按钮恢复");
            Check(recaptureBtn.Opacity == 1, "重抓结束后「重新截图」又亮回来");

            // 选区外那块：和第一次抓帧一样（覆盖层压暗没有进画面）
            double[] after = Block(baseAfter, 24, 24, 32);
            double delta = Math.Max(Math.Max(Math.Abs(after[0] - before[0]),
                    Math.Abs(after[1] - before[1])), Math.Abs(after[2] - before[2]));
            Check(delta <= 12,
                $"重抓的是当前桌面、覆盖层没被截进去（选区外那块平均色差 {delta:0.0}；" +
                $"被压暗的话会差 60 以上）");

            Save(baseAfter, System.IO.Path.Combine(_outDir, "recapture-base.png"));
        }
        finally
        {
            try { if (overlay.IsVisible) overlay.Close(); } catch { /* 已经关了 */ }
            PumpFor(200);
            SetCursorPos(960, 540);
        }
    }

    /// <summary>一块区域的平均颜色（BGRA 顺序不关心，只要前后一致就能比）。</summary>
    private static double[] Block(BitmapSource image, int x, int y, int size)
    {
        int width = Math.Min(size, Math.Max(1, image.PixelWidth - x));
        int height = Math.Min(size, Math.Max(1, image.PixelHeight - y));
        var buffer = new byte[width * height * 4];
        image.CopyPixels(new Int32Rect(x, y, width, height), buffer, width * 4, 0);

        double b = 0, g = 0, r = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            b += buffer[i];
            g += buffer[i + 1];
            r += buffer[i + 2];
        }

        double count = buffer.Length / 4.0;
        return new[] { b / count, g / count, r / count };
    }
}
