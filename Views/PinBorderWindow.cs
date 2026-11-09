using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 置顶窗口的提示边框：一条 2px 蓝色描边，盖在目标窗口外侧。
/// </summary>
/// <remarks>
/// <para>
/// 点击穿透（<c>WS_EX_TRANSPARENT</c>）+ 不抢焦点（<c>WS_EX_NOACTIVATE</c> / <c>ShowActivated=false</c>），
/// 所以它只是「看得见」的提示，不影响目标窗口的鼠标与键盘。
/// </para>
/// <para>
/// 位置一律用 <see cref="Win32.SetWindowPos"/> 按**物理像素**摆放：WPF 的 Left / Top 是 DIP，
/// 多显示器不同缩放时会算偏，物理坐标直接给窗口管理器最省事。
/// </para>
/// <para>
/// 颜色固定蓝色（不跟主题主色走）：置顶提示要一眼看出来，主题色是灰阶时描边会看不出来。
/// </para>
/// </remarks>
internal sealed class PinBorderWindow : Window
{
    private const int GwlExstyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoactivate = 0x08000000;
    private const long WsExToolwindow = 0x00000080;

    /// <summary>描边画在目标窗口外侧，避免盖住窗口自己的内容。</summary>
    private const int Pad = 2;

    /// <summary>置顶边框的颜色（DodgerBlue）。冻结后跨窗口复用，拖动时不做新对象。</summary>
    private static readonly Brush FrameBrush = CreateFrameBrush();

    private static Brush CreateFrameBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF));
        brush.Freeze();
        return brush;
    }

    public PinBorderWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsTabStop = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        IsHitTestVisible = false;

        Content = new Border
        {
            BorderBrush = FrameBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
        };

        SourceInitialized += (_, _) => MakeClickThrough();
    }

    /// <summary>把边框摆到目标窗口的可见矩形外侧；<paramref name="raise"/> 时顺带提到置顶层最前面。</summary>
    public void PlaceOver(int x, int y, int width, int height, bool raise)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        uint flags = Win32.SwpNoactivate;
        if (!raise) flags |= Win32.SwpNozorder;

        Win32.SetWindowPos(handle, raise ? Win32.HwndTopmost : IntPtr.Zero,
            x - Pad, y - Pad, width + Pad * 2, height + Pad * 2, flags);
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        long ex = GetWindowLongPtr(handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(handle, GwlExstyle,
            (IntPtr)(ex | WsExTransparent | WsExNoactivate | WsExToolwindow));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
