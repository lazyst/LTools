using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 长截图模式的界面层：蓝色描边、小面板的落点、顶部提示，其余地方全透明（鼠标与滚轮落到页面上）。
/// </summary>
public partial class CaptureOverlayWindow
{
    // ============ 界面（长截图模式的窗口层元素） ============

    /// <summary>
    /// 长截图模式的界面：冻结画面已经收起（见 <see cref="EnterScrollShell"/>），这里只摆
    /// 选区描边 + 小面板 + 顶部提示 —— 其余地方全透明，鼠标和滚轮直接落到页面上。
    /// </summary>
    private void ApplyScrollChrome()
    {
        Rect sel = _scrollRect;

        ShapeBar.Visibility = Visibility.Collapsed;
        InfoBox.Visibility = Visibility.Collapsed;
        Toolbar.Visibility = Visibility.Collapsed;
        TextEntry.Visibility = Visibility.Collapsed;
        HideDetect();
        HidePicker();
        PositionGrips(sel, false);

        // 蓝色描边：告诉用户「这一块是在长截图里」（只有描边像素不透明，挡不住页面）
        Canvas.SetLeft(ScrollFrame, sel.X - 1);
        Canvas.SetTop(ScrollFrame, sel.Y - 1);
        ScrollFrame.Width = Math.Max(1, sel.Width + 2);
        ScrollFrame.Height = Math.Max(1, sel.Height + 2);
        ScrollFrame.Visibility = Visibility.Visible;

        // 先让面板可见再量尺寸：Collapsed 的元素 DesiredSize 是 0，量出来会把面板摆到左上角
        ScrollPanel.Visibility = Visibility.Visible;
        UpdateScrollAutoChrome();
        PlaceScrollPanel(sel);
        HintText.Text = "长截图 · 直接滚页面（或空格自动滚），自动接成一张长图 · Enter 完成 · Esc 放弃";
        PlaceHint();
    }

    /// <summary>小面板：优选出选区外面（<see cref="ScrollLayout.PlaceIn"/>），实在没地方才压在选区上。</summary>
    private void PlaceScrollPanel(Rect sel)
    {
        ScrollPanel.InvalidateMeasure();
        ScrollPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Max(1, ScrollPanel.DesiredSize.Width);
        double h = Math.Max(1, ScrollPanel.DesiredSize.Height);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var at = ScrollLayout.PlaceIn(sel, new Size(w, h), new Size(ActualWidth, ActualHeight));
        Canvas.SetLeft(ScrollPanel, Math.Clamp(at.X, 8, Math.Max(8, ActualWidth - w - 8)));
        Canvas.SetTop(ScrollPanel, Math.Clamp(at.Y, 8, Math.Max(8, ActualHeight - h - 8)));
    }

    /// <summary>事件源是不是来自小面板（它的按钮要自己收点击，别当成「点页面」）。</summary>
    private bool IsOnScrollPanel(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ScrollPanel)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
