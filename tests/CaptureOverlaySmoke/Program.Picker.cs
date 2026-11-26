using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views;

namespace CaptureOverlaySmoke;

/// <summary>
/// 屏幕取点 / 取色窗口（<see cref="ScreenPickerWindow"/>）。
/// </summary>
/// <remarks>
/// 区域框选已经并入截图 Pro（`selectionOnly`），这里只剩单击取点 + 放大镜取色：
/// 断言坐标换算、色值、确认 / 取消，以及「框选那一套已经不在这个窗口里」。
/// </remarks>
internal static partial class Program
{
    private static void PickerSection()
    {
        const int width = 200;
        const int height = 100;
        var shot = PickerShot(width, height, Colors.Red);
        var monitor = new System.Drawing.Rectangle(0, 0, width, height);

        var picker = new ScreenPickerWindow(shot, monitor, wantColor: false);
        picker.Show();
        PumpFor(400);

        try
        {
            var magBox = (FrameworkElement)Field(picker, "MagBox")!;
            Check(magBox.Visibility == Visibility.Collapsed, "取点不显示放大镜（会挡住要点的目标）");
            var hint = (System.Windows.Controls.TextBlock)Field(picker, "HintText")!;
            Check(hint.Text.Contains("单击取点"), "提示写明「单击取点」：" + hint.Text);

            // 画面中心点一下：坐标换算成「显示器左上角 + 图像像素」
            double cx = picker.ActualWidth / 2, cy = picker.ActualHeight / 2;
            InvokeValue(picker, "UpdateCrosshair", new Point(cx, cy));
            PumpFor(80);

            Check(Math.Abs(picker.PointX - width / 2) <= 2 && Math.Abs(picker.PointY - height / 2) <= 2,
                $"取点坐标按物理像素换算（{picker.PointX},{picker.PointY}）");
            Check(picker.Hex == "#FF0000", $"取到的颜色是画面上的那一点（{picker.Hex}）");
            Check(!picker.Confirmed, "还没确认");

            Invoke(picker, "ConfirmPoint");
            PumpFor(150);
            Check(picker.Confirmed, "单击 = 确认");
            Check(!picker.IsVisible, "确认后关窗");
        }
        finally
        {
            try { if (picker.IsVisible) picker.Close(); } catch { /* 已经关了 */ }
        }

        // 取色：放大镜出现，色值进信息行
        var colorPicker = new ScreenPickerWindow(shot, monitor, wantColor: true);
        colorPicker.Show();
        PumpFor(400);

        try
        {
            var magBox = (FrameworkElement)Field(colorPicker, "MagBox")!;
            Check(magBox.Visibility == Visibility.Visible, "取色才显示放大镜");

            InvokeValue(colorPicker, "UpdateCrosshair",
                new Point(colorPicker.ActualWidth / 2, colorPicker.ActualHeight / 2));
            PumpFor(80);
            Check(colorPicker.Hex == "#FF0000", $"取色拿到色值（{colorPicker.Hex}）");

            var info = (System.Windows.Controls.TextBlock)Field(colorPicker, "InfoText")!;
            Check(info.Text.StartsWith("#FF0000"), "信息行把色值放在最前面：" + info.Text);

            Invoke(colorPicker, "Cancel");
            PumpFor(120);
            Check(!colorPicker.Confirmed, "右键 / Esc 取消不算确认");
        }
        finally
        {
            try { if (colorPicker.IsVisible) colorPicker.Close(); } catch { /* 已经关了 */ }
        }

        // 框选那一套已经从这个窗口里删掉（别再长回来第二套遮罩）
        Check(typeof(ScreenPickerWindow).GetField("Selection", Private) == null,
            "窗口里没有框选图层了");
        Check(typeof(ScreenPickerWindow).GetField("_sel", Private) == null,
            "窗口里没有选区状态了");

        SelectionOnlyPickBar();
    }

    /// <summary>
    /// 「只取选区」模式（参数框选）：确认 / 取消要给纯鼠标用户留按钮。
    /// 按钮长在浮动工具条右端（跟着工具条走），所以这里验「按钮在、跟着选区启用、真点一下能交结果」。
    /// </summary>
    private static void SelectionOnlyPickBar()
    {
        const int width = 400;
        const int height = 300;
        var shot = PickerShot(width, height, Colors.Teal);
        var monitor = new System.Drawing.Rectangle(0, 0, width, height);

        var overlay = new LTools.Views.CapturePro.CaptureOverlayWindow(shot, monitor, 1.0, 1.0,
            selectionOnly: true);
        // 关掉智能选区：否则窗口一打开光标下的窗口就是预选，`_sel` 直接有值，验不了「还没框选时按钮是灰的」
        var smart = Field(overlay, "_smart")!;
        var modeType = typeof(LTools.Views.CapturePro.CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.CapturePro.SmartSelectionMode")!;
        smart.GetType().GetMethod("BeginSession")!
            .Invoke(smart, new[] { Enum.Parse(modeType, "Disabled") });
        overlay.Show();
        PumpFor(400);

        try
        {
            // 确认条不再是一扇独立浮层（那样会被工具条盖住）：按钮长在工具条里
            Check(typeof(LTools.Views.CapturePro.CaptureOverlayWindow).GetField("PickBar", Private) == null,
                "「只取选区」不再有自己的浮层");

            var toolbar = Field(overlay, "Toolbar")!;
            var confirm = (System.Windows.Controls.Button)Field(toolbar, "_pickConfirmBtn")!;
            var parts = (System.Collections.IEnumerable)Field(toolbar, "_pickParts")!;
            int partCount = parts.Cast<object>().Count();

            Check(partCount == 3, "工具条上有「使用这块区域 / 取消」这一组（竖线 + 两个按钮）：" + partCount);
            Check(confirm.Content as string == "使用这块区域", "有「使用这块区域」按钮：" + confirm.Content);
            Check(confirm.Visibility == Visibility.Visible, "参数框选时这组按钮露出来");
            Check(!confirm.IsEnabled, "还没框选时按钮是灰的");

            SetSelection(overlay, new Rect(20, 20,
                Math.Max(40, overlay.ActualWidth - 120), Math.Max(40, overlay.ActualHeight - 100)));
            Invoke(overlay, "SyncArea");     // 平时由「完成」那一步同步，这里要直接看按钮状态
            Invoke(overlay, "UpdateChrome");
            PumpFor(200);
            Check(confirm.IsEnabled, "框好之后按钮可用");

            // 点「使用这块区域」= 和 Enter 一样交结果。
            // **必须用真鼠标**：直接调 Click 处理函数测不出「按钮被别的图层盖住 / 按下被当成开始框选」。
            Point center = confirm.TransformToAncestor(overlay)
                .Transform(new Point(confirm.ActualWidth / 2, confirm.ActualHeight / 2));
            var hit = overlay.InputHitTest(center) as DependencyObject;
            Check(Inside(hit, confirm),
                "确认按钮在最上面（点得到；该点命中 " + Describe(hit) + "，按钮中心在窗口 "
                + center.X.ToString("F0") + "," + center.Y.ToString("F0") + "）");

            Point at = confirm.PointToScreen(new Point(confirm.ActualWidth / 2, confirm.ActualHeight / 2));
            SetCursorPos((int)Math.Round(at.X), (int)Math.Round(at.Y));
            Thread.Sleep(200);
            Pump(overlay);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(90);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(320);
            Pump(overlay);

            Check(!overlay.IsVisible, "点确认之后关窗");
            object? result = overlay.GetType()
                .GetProperty("ResultImage", Private | BindingFlags.Public)!.GetValue(overlay);
            Check(result != null, "结果图交回调用方（和 Enter 同一条出口）");
        }
        finally
        {
            try { if (overlay.IsVisible) overlay.Close(); } catch { /* 已经关了 */ }
            PumpFor(100);
        }

        // ② 模态打开（`ScreenPicker.TryPickRegion` 就是这么调的）：确认后 ShowDialog 必须回 true，
        //    否则「已经确认」会被当成取消——用户点完按钮回到编辑器，区域没填上，看着还是没反应。
        ModalConfirm(modeType);
    }

    /// <summary>模态打开 → 框选 → 确认：<c>ShowDialog()</c> 必须回 <c>true</c>。</summary>
    private static void ModalConfirm(Type modeType)
    {
        const int width = 400;
        const int height = 300;
        var shot = PickerShot(width, height, Colors.Teal);

        var modal = new LTools.Views.CapturePro.CaptureOverlayWindow(shot,
            new System.Drawing.Rectangle(0, 0, width, height), 1.0, 1.0, selectionOnly: true);
        var smart = Field(modal, "_smart")!;
        smart.GetType().GetMethod("BeginSession")!
            .Invoke(smart, new[] { Enum.Parse(modeType, "Disabled") });

        System.Windows.Threading.DispatcherTimer? confirmTimer = null;
        System.Windows.Threading.DispatcherTimer? guard = null;
        try
        {
            confirmTimer = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(500),
                System.Windows.Threading.DispatcherPriority.Background,
                (_, _) =>
                {
                    confirmTimer?.Stop();
                    SetSelection(modal, new Rect(10, 10, 120, 90));
                    Invoke(modal, "SyncArea");
                    Invoke(modal, "UpdateChrome");
                    var toolbar = Field(modal, "Toolbar")!;
                    var confirm = (System.Windows.Controls.Button)Field(toolbar, "_pickConfirmBtn")!;
                    confirm.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                },
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            confirmTimer.Start();

            // 兜底：模态框卡住就把测试一起拖死，5 秒后自己关掉
            guard = new System.Windows.Threading.DispatcherTimer(
                TimeSpan.FromMilliseconds(5000),
                System.Windows.Threading.DispatcherPriority.Send,
                (_, _) =>
                {
                    guard?.Stop();
                    try { if (modal.IsVisible) modal.Close(); } catch { /* 已经关了 */ }
                },
                System.Windows.Threading.Dispatcher.CurrentDispatcher);
            guard.Start();

            bool? ok = modal.ShowDialog();
            Check(ok == true, "模态打开时点确认 → ShowDialog 回 true（否则 TryPickRegion 当成取消）");
        }
        finally
        {
            confirmTimer?.Stop();
            guard?.Stop();
            try { if (modal.IsVisible) modal.Close(); } catch { /* 已经关了 */ }
            PumpFor(100);
        }
    }

    /// <summary>视觉树上这个节点是不是在 <paramref name="container"/> 里面（打印命中结果用）。</summary>
    private static bool Inside(DependencyObject? node, DependencyObject container)
    {
        for (int guard = 0; node != null && guard < 64; guard++)
        {
            if (ReferenceEquals(node, container)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>命中到的控件叫什么（失败时写进断言里，省得再猜一遍）。</summary>
    private static string Describe(DependencyObject? node)
    {
        var names = new System.Text.StringBuilder();
        for (int guard = 0; node != null && guard < 6; guard++)
        {
            if (names.Length > 0) names.Append(" < ");
            names.Append(node.GetType().Name);
            if (node is FrameworkElement { Name.Length: > 0 } fe) names.Append('#').Append(fe.Name);
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return names.Length == 0 ? "（空）" : names.ToString();
    }

    /// <summary>一整块纯色画面（取点 / 取色断言用）。</summary>
    private static BitmapSource PickerShot(int width, int height, Color color)
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
