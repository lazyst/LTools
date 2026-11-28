using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>CaptureResultWindow 的「拖边框改尺寸」部分。</summary>
/// <remarks>
/// 这扇窗是 <c>WindowStyle=None</c> + <c>AllowsTransparency</c> 的分层窗口，没有系统非客户区，
/// <c>ResizeMode=CanResize</c> 也拖不到边框。所以自己量 6 DIP 的边缘 / 角，按下开始拖、
/// 跟光标改 <c>Left/Top/Width/Height</c>——与自定义最大化 / 还原（<see cref="ToggleMaximize"/>）同一套几何，
/// 不依赖系统 sizing 框。拖完把「为预览撑大之前」的矩形清掉：用户亲手定的尺寸，收起预览时不该被缩回去。
/// </remarks>
public partial class CaptureResultWindow : Window
{
    /// <summary>边框多宽以内算「拖这里改尺寸」（DIP，和分隔条同一量级）。</summary>
    private const double ResizeBorder = 6;

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
        Bottom = 8,
    }

    private ResizeEdges _resizeEdge;
    private bool _resizing;
    private Win32.Point _resizeOrigin;
    private Rect _resizeBounds;
    private double _resizeDpi = 1;

    /// <summary>两个构造函数都要调一次（<see cref="WirePreview"/> 旁边）。</summary>
    private void WireResize()
    {
        PreviewMouseMove += Resize_MouseMove;
        PreviewMouseLeftButtonDown += Resize_MouseDown;
        PreviewMouseLeftButtonUp += Resize_MouseUp;
        LostMouseCapture += (_, _) => EndResize();
        Deactivated += (_, _) => EndResize();
    }

    /// <summary>光标在这个位置算拖哪几条边（窗口外 / 最大化态返回 <see cref="ResizeEdges.None"/>）。</summary>
    private ResizeEdges EdgesAt(Point point)
    {
        if (_maximized) return ResizeEdges.None;

        double width = ActualWidth;
        double height = ActualHeight;
        if (point.X < 0 || point.Y < 0 || point.X > width || point.Y > height) return ResizeEdges.None;

        ResizeEdges edges = ResizeEdges.None;
        if (point.X <= ResizeBorder) edges |= ResizeEdges.Left;
        if (point.X >= width - ResizeBorder) edges |= ResizeEdges.Right;
        if (point.Y <= ResizeBorder) edges |= ResizeEdges.Top;
        if (point.Y >= height - ResizeBorder) edges |= ResizeEdges.Bottom;
        return edges;
    }

    private void Resize_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            ContinueResize();
            e.Handled = true;
            return;
        }

        if (_maximized || e.LeftButton == MouseButtonState.Pressed) return;

        // 只在边缘改成缩放光标；离开边缘就还原（置 null，让子控件自己的 I 形 / 手形光标生效）
        Cursor = EdgesAt(e.GetPosition(this)) switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Cursors.SizeNS,
            ResizeEdges.Left | ResizeEdges.Top or ResizeEdges.Right | ResizeEdges.Bottom => Cursors.SizeNWSE,
            ResizeEdges.Right | ResizeEdges.Top or ResizeEdges.Left | ResizeEdges.Bottom => Cursors.SizeNESW,
            _ => null,
        };
    }

    private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
    {
        ResizeEdges edges = EdgesAt(e.GetPosition(this));
        if (edges == ResizeEdges.None) return;
        if (!Win32.GetCursorPos(out _resizeOrigin)) return;

        _resizeDpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_resizeDpi <= 0) _resizeDpi = 1;

        _resizeBounds = new Rect(Left, Top, Math.Max(MinWidth, ActualWidth),
            Math.Max(MinHeight, ActualHeight));
        _resizeEdge = edges;
        _resizing = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void Resize_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing) return;
        EndResize();
        e.Handled = true;
    }

    /// <summary>按光标相对按下那一点的位移改几何；对边固定，最小尺寸按 <c>MinWidth/MinHeight</c> 夹。</summary>
    private void ContinueResize()
    {
        if (!Win32.GetCursorPos(out var cursor)) return;

        double dx = (cursor.X - _resizeOrigin.X) / _resizeDpi;
        double dy = (cursor.Y - _resizeOrigin.Y) / _resizeDpi;

        Rect bounds = _resizeBounds;
        double left = bounds.Left;
        double top = bounds.Top;
        double width = bounds.Width;
        double height = bounds.Height;

        if (_resizeEdge.HasFlag(ResizeEdges.Left))
        {
            double next = Math.Max(MinWidth, bounds.Width - dx);
            left = bounds.Right - next;
            width = next;
        }
        else if (_resizeEdge.HasFlag(ResizeEdges.Right))
        {
            width = Math.Max(MinWidth, bounds.Width + dx);
        }

        if (_resizeEdge.HasFlag(ResizeEdges.Top))
        {
            double next = Math.Max(MinHeight, bounds.Height - dy);
            top = bounds.Bottom - next;
            height = next;
        }
        else if (_resizeEdge.HasFlag(ResizeEdges.Bottom))
        {
            height = Math.Max(MinHeight, bounds.Height + dy);
        }

        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void EndResize()
    {
        if (!_resizing) return;

        _resizing = false;
        _resizeEdge = ResizeEdges.None;
        Cursor = null;

        // 用户亲手定的尺寸：收起预览时不要再缩回「撑大之前」那个矩形
        _beforePreview = default;

        if (IsMouseCaptured) ReleaseMouseCapture();
    }
}
