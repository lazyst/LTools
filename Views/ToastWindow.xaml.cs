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
/// **正文不执行回调**：带 <see cref="ClickAction"/> 的提示（失败要看日志、宏录好要开设置）
/// 在底部给一个写明动作的按钮 + 「关闭」，右上角 ✕ 只关闭。点正文 / 点 ✕ 永远不会把日志顶出来，
/// 想关掉就一定能干净关掉。
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
    private string _actionText = "";

    /// <summary>这一轮倒计时的总时长（ms；0 = 不自动关闭）。</summary>
    private int _countdownMs;

    /// <summary>到期时刻；鼠标停在卡片上时它会被不断往后推（剩余时间冻住）。</summary>
    private DateTime _deadline;

    /// <summary>还没走完的停留时间（悬停期间保持不变）。</summary>
    private TimeSpan _remaining;

    /// <summary>上一个心跳时光标是否在卡片上（用来认出「刚离开」那一下）。</summary>
    private bool _wasInside;

    /// <summary>倒计时心跳间隔：按光标是否在卡片上决定要不要继续走，150ms 足够跟手。</summary>
    private const int TickMs = 150;

    /// <summary>鼠标移开后再给的最短停留：不然只剩 100ms 时一走开就没了。</summary>
    private static readonly TimeSpan MinResume = TimeSpan.FromMilliseconds(1200);

    /// <summary>是否正被截图临时收起来（见 <see cref="HideForCapture"/>）。</summary>
    private bool _captured;

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

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _countdown.Tick += (_, _) => OnCountdownTick();

        Loaded += (_, _) => PlayIntro();
        SourceInitialized += (_, _) => MakeNonActivating();

        // 模态窗口（动作编辑器 / 确认框…）会禁掉同进程其它窗口：卡片得自己救回来
        StayClickable.Attach(this, () => _closing || _captured);
    }

    /// <summary>这条卡片占的位置（同一位置的卡片一起堆叠）。</summary>
    public ToastPosition Position { get; }

    /// <summary>
    /// 底部主操作按钮要执行的动作（例如打开运行记录）；为空 = 只有关闭。
    /// 正文点击**不会**执行它，必须点按钮。
    /// </summary>
    public Action? ClickAction
    {
        get => _clickAction;
        set
        {
            _clickAction = value;
            ApplyActionChrome();
        }
    }

    /// <summary>主操作按钮上的文字（「查看日志」「打开键鼠宏设置」…）；为空时用「查看详情」。</summary>
    public string ActionText
    {
        get => _actionText;
        set
        {
            _actionText = value ?? "";
            ApplyActionChrome();
        }
    }

    /// <summary>就地更新内容（同一个去重键重复提示时用）。</summary>
    public void SetContent(string? title, string message, ToastKind kind,
        Action? onClick = null, string? actionText = null)
    {
        _title = title ?? "";
        TitleText.Text = _title;
        TitleText.Visibility = string.IsNullOrWhiteSpace(_title) ? Visibility.Collapsed : Visibility.Visible;
        MessageText.Text = message;
        ApplyKind(kind);

        _actionText = actionText ?? "";
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

    /// <summary>
    /// 开始倒计时；<paramref name="milliseconds"/> ≤ 0 表示不自动关闭。
    /// 鼠标停在卡片上时剩余时间冻住（见 <see cref="OnCountdownTick"/>），离开后接着走。
    /// </summary>
    public void StartCountdown(int milliseconds)
    {
        _countdown.Stop();
        _countdownMs = milliseconds;
        if (milliseconds <= 0) return;

        _remaining = TimeSpan.FromMilliseconds(milliseconds);
        _deadline = DateTime.UtcNow + _remaining;
        _wasInside = false;
        // 正被截图收起（隐藏中）：别让计时器在用户看不见的时候把卡片耗掉，放回来时再走。
        if (!_captured) _countdown.Start();
    }

    /// <summary>
    /// 倒计时心跳：鼠标在卡片上就**冻住**剩余时间（正在读，别抢走），移开接着走完。
    /// </summary>
    /// <remarks>
    /// 用「光标坐标 vs 窗口矩形」而不是 WPF 的 <see cref="UIElement.IsMouseOver"/>：
    /// 卡片可能正好出现在静止的光标下面，那样既收不到 MouseEnter 也收不到 MouseLeave，
    /// 一旦按 IsMouseOver 判断就会永远停在那儿（实测）。
    /// </remarks>
    private void OnCountdownTick()
    {
        if (_countdownMs <= 0)
        {
            _countdown.Stop();
            return;
        }

        var now = DateTime.UtcNow;

        if (PointerInside())
        {
            _deadline = now + _remaining;
            _wasInside = true;
            return;
        }

        // 刚离开：至少再留一会儿，别一走开就没了。
        if (_wasInside)
        {
            _wasInside = false;
            if (_deadline - now < MinResume) _deadline = now + MinResume;
        }

        if (now >= _deadline)
        {
            Dismiss();
            return;
        }

        _remaining = _deadline - now;
    }

    /// <summary>光标是否落在这张卡片上（物理像素比窗口矩形，避开多屏 DPI 换算）。</summary>
    private bool PointerInside()
    {
        if (!Win32.GetCursorPos(out var pt)) return false;

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var rect)) return false;

        return pt.X >= rect.Left && pt.X < rect.Right
            && pt.Y >= rect.Top && pt.Y < rect.Bottom;
    }

    /// <summary>截图 / 抓帧前临时收起（倒计时跟着冻住，回来接着走）。</summary>
    /// <remarks>
    /// 提示卡片是置顶窗口，会被截进成品图（<see cref="ToastService.SuppressForCapture"/> 统一调这一对）。
    /// 收起只是 <see cref="Window.Hide"/>，卡片仍在 <see cref="ToastService"/> 的列表里，
    /// 内容 / 点击回调 / 剩余停留时间一样不丢。
    /// </remarks>
    public void HideForCapture()
    {
        if (_captured) return;

        // 正在淡出（已经在关了）：别去 Hide，隐藏的窗口动画不再推进，淡出的 Completed 就永远不来、
        // 窗口关不掉了。直接关掉——它本来就要走了。
        if (_closing)
        {
            try { Close(); } catch { /* 已经关掉 */ }
            return;
        }

        _captured = true;

        // 倒计时一起冻住：框选可能持续十几秒，别让用户还没读到这条提示它就过期消失了。
        if (_countdownMs > 0)
        {
            var left = _deadline - DateTime.UtcNow;
            _remaining = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            _countdown.Stop();
        }

        try { Hide(); } catch { /* 正在关闭 */ }
    }

    /// <summary>截图结束把卡片放回来（剩余停留时间接着走）。</summary>
    public void ShowAfterCapture()
    {
        if (!_captured) return;
        _captured = false;

        // 收起期间被关掉了（显式关闭 / 挤掉最旧的）：把淡出没走完的窗口直接关干净，
        // 别留一个隐藏的僵尸窗口在列表里。
        if (_closing)
        {
            try { Close(); } catch { /* 已经关掉 */ }
            return;
        }

        try
        {
            // 截图流程可能顺手最小化过（TransientMinimize 会跳过隐藏中的窗口、还原不到它）：
            // 不先回正常态，Show() 会把它显示成最小化、看起来就是「提示没了」。
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            Show();
            UpdateLayout();
        }
        catch
        {
            return;    // 已经关掉了
        }

        if (_countdownMs > 0)
        {
            _deadline = DateTime.UtcNow + (_remaining > TimeSpan.Zero
                ? _remaining
                : TimeSpan.FromMilliseconds(1));
            _countdown.Start();
        }

        // 收起期间可能正赶上模态窗口（动作编辑器 / 确认框）把大家禁掉：放回来时补一次
        StayClickable.Nudge(this);
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

    /// <summary>
    /// 点正文：只有「没什么可打开」的普通提示才顺手关掉；带操作按钮的提示正文不响应，
    /// 免得用户想关掉却把日志顶出来（那是按钮的事）。
    /// </summary>
    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_clickAction != null) return;
        Dismiss();
    }

    /// <summary>右上角 ✕ / 底部「关闭」：只关卡片，不执行任何回调。</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Dismiss();
    }

    /// <summary>底部主操作按钮：先收起卡片，再执行回调（打开日志 / 设置）。</summary>
    private void Action_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var action = _clickAction;
        Dismiss();
        try { action?.Invoke(); } catch { /* 详情打不开也不该让提示卡住 */ }
    }

    /// <summary>有回调 → 显出「主操作 + 关闭」并收起角落的 ✕；没有 → 只剩 ✕ 和「点一下关掉」。</summary>
    private void ApplyActionChrome()
    {
        bool hasAction = _clickAction != null;

        ActionButton.Content = string.IsNullOrWhiteSpace(_actionText) ? "查看详情" : _actionText;
        Actions.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        CloseMark.Visibility = hasAction ? Visibility.Collapsed : Visibility.Visible;

        // 没有操作时整块可点关闭，鼠标也给出这个暗示；有操作时别让正文看起来能点。
        Card.Cursor = hasAction ? Cursors.Arrow : Cursors.Hand;
        Card.ToolTip = hasAction ? null : "点击关闭";
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
