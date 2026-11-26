using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「框选」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 框选 ============

    private void ConfirmRegion(Rect dipRect)
    {
        var (x1, y1) = ToImage(dipRect.TopLeft);
        var (x2, y2) = ToImage(dipRect.BottomRight);
        int x = Math.Min(x1, x2);
        int y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1;
        int h = Math.Abs(y2 - y1) + 1;

        // 至少 2×2：OpenCV 模板再小就没意义了。
        w = Math.Min(w, _shot.PixelWidth - x);
        h = Math.Min(h, _shot.PixelHeight - y);
        if (w < 2 || h < 2) return;

        var crop = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
        crop.Freeze();

        Crop = crop;
        Area = new Int32Rect(_monitor.X + x, _monitor.Y + y, w, h);
        Confirmed = true;
        Close();
    }

    /// <summary>按当前选区重画压暗层、选区框、悬停高亮、把手、尺寸提示与确认栏。</summary>
    private void UpdateChrome()
    {
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        bool hasSelection = _sel is Rect ss && ss.Width >= 0.5 && ss.Height >= 0.5;
        Rect sel = hasSelection ? _sel!.Value : default;

        // 悬停高亮只在「还没选区」时出现；有选区时用户在看边角，不需要再跳候选。
        Rect? hover = null;
        if (!hasSelection && _hoverTarget is SnapBox hb)
        {
            var hr = ToDipRect(hb.Bounds);
            if (hr.Width >= 2 && hr.Height >= 2 && full.IntersectsWith(hr)) hover = hr;
        }

        // 压暗层用 EvenOdd 挖洞：选区（或悬停命中的窗口 / 控件）保持原亮度。
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        if (hasSelection) geo.Children.Add(new RectangleGeometry(sel));
        else if (hover is Rect hh) geo.Children.Add(new RectangleGeometry(hh));
        DimPath.Data = geo;
        DimPath.Visibility = Visibility.Visible;

        // 选区
        Selection.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SelectionShadow.Visibility = Selection.Visibility;
        if (hasSelection)
        {
            Canvas.SetLeft(Selection, sel.X);
            Canvas.SetTop(Selection, sel.Y);
            Selection.Width = sel.Width;
            Selection.Height = sel.Height;
            Canvas.SetLeft(SelectionShadow, sel.X);
            Canvas.SetTop(SelectionShadow, sel.Y);
            SelectionShadow.Width = sel.Width;
            SelectionShadow.Height = sel.Height;
        }

        // 悬停候选
        if (hover is Rect hr2) DrawDetect(hr2, _hoverTarget!.Value.Label);
        else
        {
            DetectRect.Visibility = Visibility.Collapsed;
            DetectLabel.Visibility = Visibility.Collapsed;
        }

        if (!hasSelection)
        {
            foreach (var grip in _grips) grip.Visibility = Visibility.Collapsed;
            ConfirmBtn.IsEnabled = false;
            ActionBar.Visibility = Visibility.Collapsed;   // 没选区时不挡任何拖动（提示在顶部不拦鼠标）
            InfoBox.Visibility = Visibility.Collapsed;
            ActionHint.Text = "拖动边角微调，方向键逐像素";
            RefreshHint();
            return;
        }

        bool valid = IsValid(sel);
        bool showBar = valid && _drag == DragMode.None;   // 拖动过程中先别跳出来，松手后再给按钮
        PositionGrips(sel, valid);
        ConfirmBtn.IsEnabled = showBar;
        ActionBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = valid ? "拖动边角微调，方向键逐像素" : "继续拖大一点";

        bool barAbove = showBar && PositionActionBar(sel);
        ShowSizeInfo(sel, barAbove);
        RefreshHint();
    }

    /// <summary>
    /// 取点 / 取色时顶部那句提示。调用方给了提示文字（「屏幕取点」的 <c>hint</c> 参数）就放在最前面，
    /// 后面仍跟着「怎么操作」——只写自定义那句话会让人不知道怎么确认 / 取消。
    /// </summary>
    private string PointHint() => _wantColor
        ? "单击取色 · 右键 / Esc 取消"
        : _pickHint.Length > 0
            ? _pickHint + " · 单击取点 · 右键 / Esc 取消"
            : "单击取点 · 右键 / Esc 取消";

    /// <summary>
    /// 顶部提示行：把「当前能做什么」和「控件识别到了没有」放在一句话里，
    /// 识别中 / 识别不到都要说出来——静默降级会让人以为这程序不支持吸附。
    /// </summary>
    private void RefreshHint()
    {
        if (_pointMode)
        {
            HintText.Text = PointHint();
            return;
        }

        if (_notice.Length > 0)
        {
            HintText.Text = _notice;
            return;
        }

        if (_sel is Rect s && IsValid(s))
        {
            HintText.Text = "拖动边角微调 · 方向键 1px（Shift 10px）· Ctrl+A 整屏 · P 复制坐标 · 双击 / Enter 确定";
            return;
        }

        string text = _windowTargets.Count > 0
            ? "移动选窗口 / 控件 · 拖动自由框选 · 滚轮换层级 · 右键 / Esc 取消"
            : "拖动框选范围 · 右键 / Esc 取消";

        if (_uiaPending != IntPtr.Zero)
            text += " · 识别控件中…";
        else if (_uiaStop == SnapStop.timeout)
            text += " · 读控件超时，可直接拖拽框选";
        else if (_uiaStop == SnapStop.failed)
            text += " · 这个窗口不暴露控件，可直接拖拽框选";

        HintText.Text = text;
    }

    /// <summary>
    /// 把确认栏贴到选区下方、与选区右边缘对齐（主流截图工具的摆法：Snipaste / PixPin / QQ 都是
    /// 「选框外下方、靠右」）。下方放不下就翻到选区上方；返回 true 表示放在了上方。
    /// </summary>
    private bool PositionActionBar(Rect sel)
    {
        // 确认栏在 Canvas 里，按内容尺寸测量；量不到就先不动（下一帧再排）。
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = ActionBar.DesiredSize.Width;
        double h = ActionBar.DesiredSize.Height;
        if (w <= 1 || h <= 1) return false;

        double left = Math.Clamp(sel.Right - w, 8, Math.Max(8, ActualWidth - w - 8));
        double below = sel.Bottom + 8;
        bool above = below + h > ActualHeight - 8;
        double top = above ? sel.Top - h - 8 : below;

        // 上下都放不下（选区几乎占满整屏）：贴着选区内部下方，至少不跑到屏幕外。
        if (top < 8) top = Math.Max(8, sel.Bottom - h - 8);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, Math.Clamp(top, 8, Math.Max(8, ActualHeight - h - 8)));
        return above;
    }

    /// <summary>悬停高亮：琥珀色框 + 左上角标签（控件类型 · 名称 · 尺寸）。</summary>
    private void DrawDetect(Rect r, string label)
    {
        DetectRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(DetectRect, r.X);
        Canvas.SetTop(DetectRect, r.Y);
        DetectRect.Width = r.Width;
        DetectRect.Height = r.Height;

        var (x1, y1) = ToImage(r.TopLeft);
        var (x2, y2) = ToImage(r.BottomRight);
        int w = Math.Abs(x2 - x1) + 1;
        int h = Math.Abs(y2 - y1) + 1;
        DetectText.Text = $"{label}   {w} × {h}";

        DetectLabel.Visibility = Visibility.Visible;
        DetectLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = DetectLabel.DesiredSize.Width;
        double lh = DetectLabel.DesiredSize.Height;

        double left = Math.Clamp(r.X, 8, Math.Max(8, ActualWidth - lw - 8));
        double top = r.Y - lh - 6;
        if (top < 8) top = r.Y + 6;                    // 上方贴边就放进框内侧
        if (top + lh > ActualHeight - 8) top = r.Bottom - lh - 6;
        Canvas.SetLeft(DetectLabel, left);
        Canvas.SetTop(DetectLabel, Math.Clamp(top, 8, Math.Max(8, ActualHeight - lh - 8)));
    }

    private void PositionGrips(Rect s, bool visible)
    {
        if (_grips.Count == 0) return;

        foreach (var grip in _grips)
        {
            if (!visible || grip.Tag is not Handle h)
            {
                grip.Visibility = Visibility.Collapsed;
                continue;
            }

            Point at = GripPoint(s, h);
            Canvas.SetLeft(grip, at.X - GripSize / 2);
            Canvas.SetTop(grip, at.Y - GripSize / 2);
            grip.Visibility = Visibility.Visible;
        }
    }

    /// <summary>尺寸提示（物理像素，就是最终会存进配置的数字）；主流放在选区左上角外侧。</summary>
    private void ShowSizeInfo(Rect s, bool barAbove)
    {
        var (x1, y1) = ToImage(new Point(s.X, s.Y));
        var (x2, y2) = ToImage(new Point(s.X + s.Width, s.Y + s.Height));
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1, h = Math.Abs(y2 - y1) + 1;

        InfoText.Text = $"{w} × {h} px   {_monitor.X + x}, {_monitor.Y + y}";
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double iw = InfoBox.DesiredSize.Width;
        double ih = InfoBox.DesiredSize.Height;

        double left = Math.Clamp(s.X, 8, Math.Max(8, ActualWidth - iw - 8));
        double top = s.Y - ih - 6;                 // 选区上方
        if (top < 8 || barAbove) top = s.Y + 6;    // 上方没空间 / 被确认栏占了 → 放进选区左上角
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, Math.Clamp(top, 8, Math.Max(8, ActualHeight - ih - 8)));
    }

}
