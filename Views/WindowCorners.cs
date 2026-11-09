using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 窗口圆角：交给 Win11 的 DWM 去画（只对非分层窗口有效），
/// 自绘的 1px 细边框跟着改成同一个半径，否则系统裁掉四角后边框会「断在拐角」。
/// </summary>
/// <remarks>
/// <para>
/// 挂法是在 <see cref="Attach"/> 里注册一次 <see cref="Window"/> 的类处理器，
/// 之后新建的窗口（包括以后加的对话框）自动生效，不用每个窗口各写一遍 Loaded。
/// </para>
/// <para>
/// <see cref="Window.AllowsTransparency"/> 的窗口（悬浮窗 / Toast / 鼠标提示）自己画圆角卡片，
/// DWM 对分层窗口不生效，直接跳过；<see cref="Win32.TryEnableRoundCorners"/> 失败（Win10）时
/// 也一律保持原样，绝不硬画圆角边框。
/// </para>
/// </remarks>
internal static class WindowCorners
{
    /// <summary>窗口圆角半径，与 Win11 系统圆角一致。</summary>
    private const double Radius = 8;

    /// <summary>在 <c>App.OnStartup</c> 里调用一次。</summary>
    public static void Attach()
        => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        if (window.AllowsTransparency || window.WindowStyle != WindowStyle.None) return;
        if (!Win32.TryEnableRoundCorners(new WindowInteropHelper(window).Handle)) return;

        // ModernWindow 模板里的那圈 1px 边框（其余窗口没有这个部件，圆角交给 DWM 就够了）
        if (window.Template?.FindName("WindowFrame", window) is Border frame)
            frame.CornerRadius = new CornerRadius(Radius);
    }
}
