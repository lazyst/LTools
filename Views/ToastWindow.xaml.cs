using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 一条提示卡片：置顶、圆角、右下角堆叠，**不抢焦点、不阻塞**调用方。
/// </summary>
/// <remarks>
/// <para>
/// 「不阻塞」不只是调用方不等待：自动化正在往别的窗口输入时，这张卡片出现 / 被点也不能把前台抢走，
/// 否则后续的键入会打到卡片上。所以窗口用 <c>WS_EX_NOACTIVATE</c> + <c>ShowActivated=false</c>，
/// 点击只是关掉它，焦点仍留在原来的窗口。
/// </para>
/// <para>
/// 位置与堆叠由 <see cref="LTools.Core.ToastService"/> 统一管理（它是唯一知道同时有几条卡片的地方）。
/// </para>
/// </remarks>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _countdown;
    private bool _closing;
    private readonly TranslateTransform _slide = new(20, 0);

    /// <summary>构造卡片（不自动 Show，也不开始计时，由服务安排位置后统一控制）。</summary>
    /// <param name="title">标题，可空（空则不占一行）。</param>
    /// <param name="message">正文。</param>
    public ToastWindow(string? title, string message)
    {
        InitializeComponent();

        TitleText.Text = title ?? "";
        TitleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;

        Card.RenderTransform = _slide;

        _countdown = new DispatcherTimer();
        _countdown.Tick += (_, _) => Dismiss();

        Loaded += (_, _) => PlayIntro();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    /// <summary>开始倒计时；<paramref name="milliseconds"/> ≤ 0 表示不自动关闭。</summary>
    public void StartCountdown(int milliseconds)
    {
        if (milliseconds <= 0) return;
        _countdown.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _countdown.Start();
    }

    /// <summary>淡出并关闭（重复调用安全）。</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _countdown.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { /* 已在关闭 */ }
        };
        Card.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>鼠标点一下整张卡片就关掉（不用去找小叉）。</summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        Dismiss();
        e.Handled = true;
    }

    /// <summary>淡入 + 从右侧滑入 20px。</summary>
    private void PlayIntro()
    {
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _slide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>不抢焦点、不进 Alt+Tab：提示出现时键盘输入仍留在原来的窗口。</summary>
    private void MakeNonActivating()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            long ex = Win32.GetWindowLongPtr(hwnd, Win32.GwlExstyle).ToInt64();
            Win32.SetWindowLongPtr(hwnd, Win32.GwlExstyle,
                (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));
        }
        catch { /* 样式加不上也还能显示，只是可能抢焦点 */ }
    }
}
