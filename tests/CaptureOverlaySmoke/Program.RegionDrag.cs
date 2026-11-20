using System;
using System.Collections;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 多块选区里单独拖一块（P9-t，照上游 <c>ScreenshotSelectionModel::beginMoveDrag</c>）：
/// 多块编辑默认只挪**命中的那一块**，按住「移动整个选区」（默认 <c>Space</c>）才整体挪；
/// 拖动中途按住 / 松开 Space 会换口径（<c>rebaseMoveDrag</c>），画面不跳。
/// </summary>
/// <remarks>
/// 纯几何（命中哪一块 / 重叠取最上层 / 别的区域的吸附参考边）在 <c>--selftest</c> 的
/// 「多区域：加区 / 减区」一组里断言，这里只验真窗口 + 真鼠标 + 真按键。
/// </remarks>
internal static partial class Program
{
    private static void RegionDragSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var shot = BuildShot(900, 700);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, 900, 700));

        try
        {
            window.Show();
            window.Activate();
            Pump(window);

            // 这一段的真 Shift 会打到系统输入法上（中文 IME 里 Shift = 中英切换），
            // 切过去之后后面整段真键盘都会变成 ImeProcessed；本段用不到输入法，直接关掉。
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

            var regionsField = typeof(CaptureOverlayWindow).GetField("_regions", Inst)!;
            var selField = typeof(CaptureOverlayWindow).GetField("_sel", Inst)!;

            IList Regions() => (IList)regionsField.GetValue(window)!;
            Rect BoundsOf(int i) => (Rect)Regions()[i]!.GetType().GetProperty("Bounds")!
                .GetValue(Regions()[i])!;
            Rect Sel() => selField.GetValue(window) as Rect? ?? Rect.Empty;

            Point Screen(double x, double y) => window.PointToScreen(new Point(x, y));

            void Move(Point at, int settle = 70)
            {
                SetCursorPos((int)Math.Round(at.X), (int)Math.Round(at.Y));
                Thread.Sleep(settle);
                Pump(window);
            }

            void PressAt(Point dip)
            {
                Move(Screen(dip.X, dip.Y), 110);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(60);
                Pump(window);
            }

            void MoveTo(Point dip, int settle = 22)
            {
                SetCursorPos((int)Math.Round(Screen(dip.X, dip.Y).X),
                    (int)Math.Round(Screen(dip.X, dip.Y).Y));
                Thread.Sleep(settle);
                Pump(window);
            }

            void ReleaseAt(Point dip)
            {
                MoveTo(dip, 40);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(180);
                Pump(window);
            }

            void Drag(Point from, Point to, int steps = 14)
            {
                PressAt(from);
                for (int i = 1; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    MoveTo(new Point(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t), 18);
                }

                ReleaseAt(to);
            }

            /// <summary>真按键（<c>keybd_event</c>）：Shift 的加区、Space 的整体移动都读实时键盘状态。</summary>
            void Key(byte vk, bool down)
            {
                keybd_event(vk, 0, down ? 0u : 2u, UIntPtr.Zero);
                Thread.Sleep(70);
                Pump(window);
            }

            string DragMode() => Field(window, "_drag")!.ToString()!;
            int DragRegion() => (int)Field(window, "_dragRegion")!;

            // ============ ① 真鼠标拖出第一块 ============
            Drag(new Point(120, 120), new Point(320, 260));
            Check(Regions().Count == 1, $"第一块拖出来了（实际 {Regions().Count} 块）");
            Rect first = BoundsOf(0);
            Check(Math.Abs(first.Width - 200) <= 2 && Math.Abs(first.Height - 140) <= 2,
                $"第一块就是拖出来的那一块：{first}");

            // ============ ② Shift + 拖第二块（与第一块不相交） ============
            Key(0x10, true);    // Shift
            Drag(new Point(560, 380), new Point(740, 560));
            Key(0x10, false);

            Check(Regions().Count == 2, $"Shift 加区得到第二块（实际 {Regions().Count} 块）");
            Rect second = BoundsOf(1);
            Check(!first.IntersectsWith(second), $"两块不相交：{first} / {second}");

            // ============ ③ 拖第一块：只动这一块（上游「多块拖动默认打命中区」） ============
            PressAt(new Point(first.X + 40, first.Y + 30));
            Check(DragMode() == "MoveRegion", "多块里按在区域上：进入「只挪命中那一块」" + DragMode());
            Check(DragRegion() == 0, $"命中的是第 1 块（实际 {DragRegion()}）");

            for (int i = 1; i <= 12; i++)
                MoveTo(new Point(first.X + 40 + 7.5 * i, first.Y + 30 + 5 * i), 18);
            ReleaseAt(new Point(first.X + 130, first.Y + 90));

            Check(Regions().Count == 2, $"拖完还是两块（没合并、没重框，实际 {Regions().Count} 块）");
            Check(BoundsOf(1) == second, $"拖第一块时第二块一点没动（{BoundsOf(1)}）");

            Rect movedFirst = BoundsOf(0);
            Check(Math.Abs(movedFirst.X - (first.X + 90)) <= 8 && Math.Abs(movedFirst.Y - (first.Y + 60)) <= 8,
                $"第一块跟着指针走了 +90 / +60：{first} → {movedFirst}");
            Check(Math.Abs(Sel().Right - second.Right) <= 8,
                $"外接框按并集重算（右边缘还在第二块那里：{Sel()}）");

            // ============ ④ 按住 Space 拖第二块：两块一起走 ============
            Rect beforeFirst = BoundsOf(0), beforeSecond = BoundsOf(1);
            Key(0x20, true);    // Space = 移动整个选区

            Point grip = new(beforeSecond.X + 40, beforeSecond.Y + 30);
            PressAt(grip);
            Check(DragMode() == "Move", "按住 Space 按在区域上：整体移动" + DragMode());

            for (int i = 1; i <= 12; i++)
                MoveTo(new Point(grip.X - 5 * i, grip.Y - 3.3 * i), 18);
            ReleaseAt(new Point(grip.X - 60, grip.Y - 40));
            Key(0x20, false);

            Rect afterFirst = BoundsOf(0), afterSecond = BoundsOf(1);
            Vector shiftFirst = afterFirst.TopLeft - beforeFirst.TopLeft;
            Vector shiftSecond = afterSecond.TopLeft - beforeSecond.TopLeft;
            Check(Math.Abs(shiftSecond.X + 60) <= 8 && Math.Abs(shiftSecond.Y + 40) <= 8,
                $"第二块按指针走了 -60 / -40（实际 {shiftSecond}）");
            Check(Math.Abs(shiftFirst.X - shiftSecond.X) <= 1 && Math.Abs(shiftFirst.Y - shiftSecond.Y) <= 1,
                $"两块走同样的位移（{shiftFirst} / {shiftSecond}）");

            // ============ ⑤ 松开 Space 再拖：又回到只挪命中那一块 ============
            Rect holdFirst = BoundsOf(0), holdSecond = BoundsOf(1);
            Point grip2 = new(holdSecond.X + 40, holdSecond.Y + 30);
            PressAt(grip2);
            Check(DragMode() == "MoveRegion" && DragRegion() == 1,
                $"Space 松开后又只挪命中那一块（{DragMode()} / {DragRegion()}）");
            for (int i = 1; i <= 10; i++) MoveTo(new Point(grip2.X - 3 * i, grip2.Y), 18);
            ReleaseAt(new Point(grip2.X - 30, grip2.Y));
            Check(BoundsOf(0) == holdFirst, $"这一下第一块没动（{BoundsOf(0)}）");
            Check(BoundsOf(1).X < holdSecond.X - 20, $"第二块往左挪了（{holdSecond} → {BoundsOf(1)}）");

            // ============ ⑥ 拖动中途按住 Space：换口径（上游 rebaseMoveDrag，画面不跳） ============
            Rect liveFirst = BoundsOf(0), liveSecond = BoundsOf(1);
            Point grip3 = new(liveFirst.X + 40, liveFirst.Y + 30);
            PressAt(grip3);
            Check(DragMode() == "MoveRegion", "起手只挪第 1 块" + DragMode());

            for (int i = 1; i <= 6; i++) MoveTo(new Point(grip3.X + 4 * i, grip3.Y + 2 * i), 18);
            Rect draggedOnly = BoundsOf(0);
            Check(draggedOnly.X > liveFirst.X + 10, $"先只挪了第 1 块（{liveFirst} → {draggedOnly}）");
            Check(BoundsOf(1) == liveSecond, "这时第 2 块还没动");

            Key(0x20, true);    // 拖到一半按住 Space
            for (int i = 7; i <= 12; i++) MoveTo(new Point(grip3.X + 4 * i, grip3.Y + 2 * i), 18);
            Check(DragMode() == "Move", "中途按住 Space：换成整体移动" + DragMode());
            ReleaseAt(new Point(grip3.X + 48, grip3.Y + 24));
            Key(0x20, false);

            Check(BoundsOf(1) != liveSecond, $"换口径之后第 2 块也跟着走了（{liveSecond} → {BoundsOf(1)}）");
            Check(Math.Abs((BoundsOf(0).TopLeft - draggedOnly.TopLeft).X -
                           (BoundsOf(1).TopLeft - liveSecond.TopLeft).X) <= 1,
                "后半段两块走同样的位移");

            Save(Render(window), System.IO.Path.Combine(_outDir, "region-drag.png"));

            Check(System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None,
                $"跑完没有残留的修饰键（{System.Windows.Input.Keyboard.Modifiers}）");
            Check(System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.Space) == false, "Space 没有卡住");
        }
        finally
        {
            try { window.Close(); } catch { /* 已关 */ }
        }
    }
}
