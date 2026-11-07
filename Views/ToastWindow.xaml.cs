using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LTools.Core;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 一条提示卡片：置顶、圆角、右下角堆叠，**不抢焦点、不阻塞**调用方。
/// </summary>
/// <remarks>
/// <para>
/// 卡片本身只是个显示壳：等待多久、出现在哪儿、什么时候关，都由 <see cref="ToastService"/> 统一管理
/// （它是唯一知道同时有几张卡片的地方）。
/// </para>
/// <para>
/// 自动化进行中时它可能出现在鼠标附近，所以整块都可点关闭，且不会激活本窗口：
/// 靠 <c>WS_EX_NOACTIVATE</c> + <c>ShowActivated=false</c>，点击只是关掉卡片，焦点还留在原来的窗口。
/// </para>
/// <para>
/// 同一个去重键的提示会被就地刷新（<see cref="SetContent"/>）并累加次数（<see cref="Bump"/>），
/// 循环里的「正在处理第 3 个」因此只占一条卡片。
/// </para>
/// </remarks>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _countdown;
    private bool _closing;
    private readonly TranslateTransform _slide = new(20, 0);
    private string _title = "";
    private int _repeat;
    private Action? _clickAction;

    /// <summary>创建一条卡片（由 <see cref="ToastService"/> 统一 Show / 定位）。</summary>
    /// <param name="title">标题，可空（空则整行不占位）。</param>
    /// <param name="message">正文。</param>
    /// <param name="kind">语义等级，决定左侧色条颜色。</param>
    /// <param name="position">出现位置（同一位置的卡片会堆叠）。</param>
    public ToastWindow(string? title, string message, ToastKind kind,
        ToastPosition position = ToastPosition.bottomRight)
    {
        InitializeComponent();

        Position = position;
        _title = title ?? "";
        TitleText.Text = _title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;

        ApplyKind(kind);

        Card.RenderTransform = _slide;

        _countdown = new DispatcherTimer();
        _countdown.Tick += (_, _) => Dismiss();

        Loaded += (_, _) => PlayIntro();
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    /// <summary>这条卡片占的位置（同一位置的卡片一起堆叠）。</summary>
    public ToastPosition Position { get; }

    /// <summary>点击整张卡片时执行（例如打开运行记录）；为空 = 只是关闭。</summary>
    public Action? ClickAction
    {
        get => _clickAction;
        set
        {
            _clickAction = value;
            DetailText.Visibility = value != null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>就地更新内容（同一个去重键重复提示时用）。</summary>
    public void SetContent(string? title, string message, ToastKind kind, Action? onClick = null)
    {
        _title = title ?? "";
        TitleText.Text = _title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(_title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;
        ApplyKind(kind);

        ClickAction = onClick;
    }

    /// <summary>累加一次「又来了」的次数，显示成标题后的 ×N。</summary>
    public void Bump()
    {
        _repeat++;
        if (_repeat <= 1) return;
        TitleText.Text = $"{_title}  ×{_repeat}";
        TitleText.Visibility = Visibility.Visible;
    }

    /// <summary>开始倒计时；<paramref name="milliseconds"/> ≤ 0 表示不自动关闭。</summary>
    public void StartCountdown(int milliseconds)
    {
        _countdown.Stop();
        if (milliseconds <= 0) return;
        _countdown.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _countdown.Start();
    }

    /// <summary>立刻关闭（重复调用安全）。</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _countdown.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            try { Close(); } catch { /* 窗口已关 */ }
        };
        Card.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>点一下卡片：先执行回调（打开详情），再关掉这张卡片。</summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        Dismiss();
        try { ClickAction?.Invoke(); } catch { /* 详情打不开也不该让提示卡住 */ }
        e.Handled = true;
    }

    /// <summary>按等级换左侧色条；失败 / 成功一眼能分清。</summary>
    private void ApplyKind(ToastKind kind)
    {
        AccentBar.Background = new SolidColorBrush(kind switch
        {
            ToastKind.success => Color.FromRgb(0x22, 0xC5, 0x5E),
            ToastKind.warning => Color.FromRgb(0xF5, 0x9E, 0x0B),
            ToastKind.error => Color.FromRgb(0xEF, 0x44, 0x44),
            _ => Color.FromRgb(0x3B, 0x82, 0xF6),
        });

        if (kind == ToastKind.error)
        {
            // 失败提示要留得住：边框也带一点红，扫一眼就知道不是普通消息。
            Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xEF, 0x44, 0x44));
        }
        else
        {
            Card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        }
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
