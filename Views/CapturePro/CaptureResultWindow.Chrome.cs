using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>CaptureResultWindow 的「最小化 / 最大化」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 最小化 / 最大化 ============

    private static readonly Geometry MaximizeGlyph = Geometry.Parse("M 1,1 L 9,1 L 9,9 L 1,9 Z");
    private static readonly Geometry RestoreGlyph = Geometry.Parse("M 1,1 L 6,1 L 6,6 L 1,6 Z M 4,4 L 9,4 L 9,9 L 4,9 Z");

    private Rect _normalBounds;
    private bool _maximized;

    /// <summary>现在是不是「最大化」态（冒烟读它）。</summary>
    internal bool IsMaximized => _maximized;

    /// <summary>最小化：窗口进任务栏，点一下回来（<c>ShowInTaskbar</c> 必须是 true，否则就再也找不到了）。</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// 最大化 / 还原。**铺满所在显示器的工作区**（不是整个屏幕）：无边框窗口走 <c>WindowState</c>
    /// 会连任务栏一起盖住，所以这里自己换位置尺寸，并把原来的矩形记下来还原。
    /// </summary>
    internal void ToggleMaximize()
    {
        if (_maximized)
        {
            _maximized = false;
            Rect normal = _normalBounds;
            if (normal.Width >= MinWidth && normal.Height >= MinHeight)
            {
                Left = normal.Left;
                Top = normal.Top;
                Width = normal.Width;
                Height = normal.Height;
            }

            UpdateMaximizeChrome();
            ReplanPreview();       // 还原后按小窗口重排原文栏
            return;
        }

        _normalBounds = new Rect(Left, Top, Math.Max(MinWidth, ActualWidth),
            Math.Max(MinHeight, ActualHeight));

        Rect work = WorkAreaOfWindow();
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;
        _maximized = true;
        UpdateMaximizeChrome();
        ReplanPreview();            // 铺满工作区之后多半就够 1:1 了
    }

    private void UpdateMaximizeChrome()
    {
        MaxIcon.Data = _maximized ? RestoreGlyph : MaximizeGlyph;
        MaxBtn.ToolTip = _maximized ? "还原（双击标题栏也行）" : "最大化 / 还原（双击标题栏也行）";
    }

    /// <summary>窗口当前所在显示器的工作区（DIP）；拿不到就退回捕获区域那块。</summary>
    private Rect WorkAreaOfWindow()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && Win32.GetWindowRect(hwnd, out var rect))
        {
            var screen = CaptureRecordWindow.ScreenOf(new Int32Rect(rect.Left, rect.Top,
                Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top)));
            if (screen is { Scale: > 0 })
            {
                return new Rect(screen.WorkLeft / screen.Scale, screen.WorkTop / screen.Scale,
                    screen.WorkWidth / screen.Scale, screen.WorkHeight / screen.Scale);
            }
        }

        return WorkArea();
    }

    /// <summary>从任务栏回来时，窗口可能已经落在屏幕外（换过分辨率 / 显示器）：夹回工作区。</summary>
    private void EnsureOnScreen()
    {
        Rect work = WorkAreaOfWindow();
        Rect bounds = new(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        if (bounds.Right > work.Left && bounds.Left < work.Right &&
            bounds.Bottom > work.Top && bounds.Top < work.Bottom)
        {
            return;
        }

        Place();
        UpdateZoom();
        ReplanPreview();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // 关窗要掐掉还在飞的翻译请求，并把画布切回原文（窗口已关，但画布对象可能还被引用）
        try
        {
            _translationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经结束
        }

        _translationCts = null;
        _translatedLines = null;
        ClosePreview();
    }
}
