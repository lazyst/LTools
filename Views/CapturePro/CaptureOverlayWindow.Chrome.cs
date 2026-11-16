using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 覆盖层的窗口层元素：把手、尺寸条、悬停标签、顶部提示、完成动作栏。
/// </summary>
/// <remarks>
/// 摆放一律按「选区在窗口里的 DIP 矩形」算，尺寸文字用物理像素（那才是最终存进配置的数字）。
/// 所有 <c>Measure</c> 都在 Canvas 里做：Canvas 用无限约束测量，<c>DesiredSize</c> 就是内容尺寸，
/// 才能把动作栏「右对齐选区、贴下方」算出来。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private const double GripSize = 9;     // 把手方块尺寸（DIP）
    private const double GripHit = 8;      // 把手命中半径（DIP）

    /// <summary>选区上的八个把手（框选模式下创建）。</summary>
    private void BuildGrips()
    {
        foreach (Handle h in new[]
                 {
                     Handle.TopLeft, Handle.TopRight, Handle.BottomLeft, Handle.BottomRight,
                     Handle.Top, Handle.Bottom, Handle.Left, Handle.Right,
                 })
        {
            var grip = new Rectangle
            {
                Width = GripSize,
                Height = GripSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = h,
            };
            _grips.Add(grip);
            Layer.Children.Add(grip);
        }
    }

    /// <summary>按当前选区重画画面、把手、尺寸条、悬停高亮、动作栏与顶部提示。</summary>
    private void UpdateChrome()
    {
        bool hasSelection = _sel is Rect current && IsValid(current);
        Rect sel = hasSelection ? _sel!.Value : Rect.Empty;

        // 悬停高亮只在「还没选区」时出现；有选区时用户在看边角，不需要再跳候选
        Rect hover = Rect.Empty;
        if (!hasSelection && _hoverTarget is SnapBox hb)
        {
            var hr = ToDipRect(hb.Bounds);
            if (hr.Width >= 2 && hr.Height >= 2) hover = hr;
        }

        Surface.Selection = sel;
        Surface.Hover = hover;
        Surface.InvalidateVisual();

        if (!hasSelection)
        {
            PositionGrips(sel, false);
            InfoBox.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
            if (hover != Rect.Empty) DrawDetect(hover, _hoverTarget!.Value.Label);
            else HideDetect();

            RefreshHint();
            PlaceHint();
            return;
        }

        HideDetect();
        PositionGrips(sel, true);

        // 拖动过程中先别跳出来，松手后再给按钮（否则动作栏会跟着手一起抖）
        bool showBar = _drag == DragMode.None;
        ActionBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        bool barAbove = showBar && PositionActionBar(sel);
        ShowSizeInfo(sel, barAbove);

        RefreshHint();
        PlaceHint();
    }

    private void HideDetect()
    {
        DetectRect.Visibility = Visibility.Collapsed;
        DetectLabel.Visibility = Visibility.Collapsed;
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
        DetectText.Text = $"{label}   {Math.Abs(x2 - x1) + 1} × {Math.Abs(y2 - y1) + 1}";

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

    /// <summary>尺寸提示（物理像素）；主流放在选区左上角外侧。</summary>
    private void ShowSizeInfo(Rect s, bool barAbove)
    {
        var (x1, y1) = ToImage(s.TopLeft);
        var (x2, y2) = ToImage(s.BottomRight);
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1, h = Math.Abs(y2 - y1) + 1;

        InfoText.Text = $"{w} × {h} px   {_monitor.X + x}, {_monitor.Y + y}";
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double iw = InfoBox.DesiredSize.Width;
        double ih = InfoBox.DesiredSize.Height;

        double left = Math.Clamp(s.X, 8, Math.Max(8, ActualWidth - iw - 8));
        double top = s.Y - ih - 6;                     // 选区上方
        if (top < 8 || barAbove) top = s.Y + 6;        // 上方没空间 / 被动作栏占了 → 放进选区左上角
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, Math.Clamp(top, 8, Math.Max(8, ActualHeight - ih - 8)));
    }

    /// <summary>动作栏贴到选区下方、与右边缘对齐；下方放不下就翻到上方（返回 true 表示放在了上方）。</summary>
    private bool PositionActionBar(Rect sel)
    {
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = ActionBar.DesiredSize.Width;
        double h = ActionBar.DesiredSize.Height;
        if (w <= 1 || h <= 1) return false;

        double left = Math.Clamp(sel.Right - w, 8, Math.Max(8, ActualWidth - w - 8));
        double below = sel.Bottom + 8;
        bool above = below + h > ActualHeight - 8;
        double top = above ? sel.Top - h - 8 : below;

        // 上下都放不下（选区几乎占满整屏）：贴着选区内部下方，至少不跑到屏幕外
        if (top < 8) top = Math.Max(8, sel.Bottom - h - 8);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, Math.Clamp(top, 8, Math.Max(8, ActualHeight - h - 8)));
        return above;
    }

    /// <summary>顶部提示居中。</summary>
    private void PlaceHint()
    {
        HintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = HintBox.DesiredSize.Width;
        Canvas.SetLeft(HintBox, Math.Max(12, (ActualWidth - w) / 2));
        Canvas.SetTop(HintBox, 26);
    }

    /// <summary>顶部提示：把「现在能做什么」和「识别到了没有」放在一句话里。</summary>
    private void RefreshHint()
    {
        if (_sel is Rect s && IsValid(s))
        {
            HintText.Text = "拖动边角微调 · 方向键 1px（Shift 10px）· Ctrl+A 整屏 · Enter / 双击 = 复制 · 右键 / Esc 取消";
            return;
        }

        if (_hoverTarget is SnapBox hover)
        {
            HintText.Text = $"单击选中「{hover.Label}」· 也可直接拖动框选 · 滚轮换层级 · 右键 / Esc 取消";
            return;
        }

        string text = _windowTargets.Count > 0
            ? "拖动框选区域 · 单击选窗口 / 控件 · 滚轮换层级 · 右键 / Esc 取消"
            : "拖动框选区域 · 右键 / Esc 取消";

        if (_uiaPending != IntPtr.Zero) text += " · 识别控件中…";
        else if (_uiaTargets.Count == 0 && _windowTargets.Count > 0) text += " · 未检测到控件，可直接拖拽框选";
        HintText.Text = text;
    }

    /// <summary>标注变了（撤销 / 重做 / 预览）：重画画面与界面。</summary>
    private void RefreshMarks()
    {
        Surface.Preview = null;
        Surface.InvalidateVisual();
        UpdateChrome();
    }

    /// <summary>
    /// 按物理像素把窗口钉死在显示器矩形上。
    /// 混合 DPI 下 WPF 的 DIP→像素换算可能差几个像素，用 Win32 再确认一次，
    /// 保证冻结画面与屏幕 1:1 对齐。
    /// </summary>
    private void PinToMonitor()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost,
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, Win32.SwpNoactivate);
    }
}
