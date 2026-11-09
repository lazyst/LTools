using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Views;

/// <summary>
/// 记事本式的文本窗口：显示一段文本 / 数据，或让用户改完再读回。
/// </summary>
/// <remarks>
/// <para>
/// 由「文本窗口」单元（<c>ui.textWindow</c>）经 <see cref="Features.TextWindowService"/> 打开，
/// 单元自己负责轮询结果——窗口**不是**模态的，这样流程停止热键仍然有效（模态对话框会卡住 UI 线程）。
/// </para>
/// <para>
/// 显示模式：只读、不抢焦点（<see cref="Window.ShowActivated"/> = false），用户点进来仍能选中复制；
/// 读取模式：可编辑，「确定」或 Ctrl+Enter 返回内容，Esc / 关闭 = 取消。
/// 两种模式的外观差别集中在 <see cref="ApplyModeChrome"/>：标题栏徽标、正文底色、光标、底部按钮。
/// 位置尺寸由服务记住（最大化时存还原后的矩形）。
/// </para>
/// </remarks>
public partial class TextViewWindow : Window
{
    private readonly bool _editable;
    private readonly Border _modeBadge = new()
    {
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 2, 8, 2),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _modeGlyph = new()
    {
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 5, 0),
    };
    private readonly TextBlock _modeText = new() { VerticalAlignment = VerticalAlignment.Center };
    private string _initial = "";

    /// <summary>窗口是否可编辑（读取模式）。</summary>
    public bool IsEditable => _editable;

    /// <summary>读取模式下用户是否点了「确定」。</summary>
    public bool Confirmed { get; private set; }

    /// <summary>窗口是否已经关掉（单元轮询它决定何时继续）。</summary>
    public bool IsClosed { get; private set; }

    /// <summary>窗口里的当前内容；只能在 UI 线程读。</summary>
    public string Text => Body.Text;

    /// <summary>读取模式下内容是否被改过（与打开 / 最后一次设置的内容比较）。</summary>
    public bool Changed => Body.Text != _initial;

    public TextViewWindow(string title, string text, bool editable, bool monospace, bool wrap, bool activate)
    {
        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(title) ? (editable ? "输入文本" : "文本窗口") : title.Trim();
        _editable = editable;
        ShowActivated = activate;

        if (monospace) Body.FontFamily = (FontFamily)FindResource("MonoFont");
        Body.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Body.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

        // 标题栏徽标：图标 + 文字 + 底色三重区分，不只靠颜色。
        var badgeContent = new StackPanel { Orientation = Orientation.Horizontal };
        badgeContent.Children.Add(_modeGlyph);
        badgeContent.Children.Add(_modeText);
        _modeBadge.Child = badgeContent;
        Bar.ExtraContent = _modeBadge;

        // 读取模式才给按钮；显示模式只有「关闭」（标题栏关闭按钮与 Esc 也行）。
        Body.IsReadOnly = !editable;
        ApplyModeChrome(editable);

        SetContent(text);
        Body.TextChanged += (_, _) => UpdateStats();
        UpdateStats();

        Loaded += (_, _) =>
        {
            if (!editable) return;
            Body.Focus();
            Body.CaretIndex = Body.Text.Length;
            Body.ScrollToEnd();
        };
        Closed += (_, _) => IsClosed = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>替换窗口内容（同一个 key 复用窗口时用）。</summary>
    public void SetContent(string text)
    {
        Body.Text = text ?? "";
        _initial = Body.Text;
        Body.CaretIndex = 0;
        Body.ScrollToHome();
        UpdateStats();
    }

    /// <summary>把一段内容接在末尾（显示模式的「追加」）。</summary>
    public void AppendContent(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string current = Body.Text;
        if (current.Length > 0 && !current.EndsWith('\n')) current += "\n";
        Body.Text = current + text;
        Body.CaretIndex = Body.Text.Length;
        Body.ScrollToEnd();
        UpdateStats();
    }

    /// <summary>读取模式：拿当前内容并关窗（<see cref="Confirmed"/> 由调用方决定）。</summary>
    public void CloseWithResult(bool confirmed)
    {
        Confirmed = confirmed && _editable;
        Close();
    }

    private void UpdateStats()
    {
        string text = Body.Text ?? "";
        int lines = text.Length == 0 ? 1 : 1;
        foreach (char c in text) if (c == '\n') lines++;
        StatsText.Text = $"{lines} 行 · {text.Length} 字";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseWithResult(false);
            return;
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _editable)
        {
            e.Handled = true;
            CloseWithResult(true);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => CloseWithResult(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    /// <summary>
    /// 只读 / 可编辑两套外观：标题栏徽标（图标 + 文字 + 底色）、正文底色、鼠标光标、底部按钮。
    /// 形状与文字都不同，不只靠颜色——本主题是灰阶，单靠颜色对比不牢。
    /// </summary>
    private void ApplyModeChrome(bool editable)
    {
        var fontSize = (double)FindResource("FontSizeSecondary");
        _modeGlyph.FontSize = fontSize;
        _modeText.FontSize = fontSize;

        _modeBadge.Background = (Brush)FindResource(editable ? "AccentBrush" : "SurfaceAltBrush");
        _modeBadge.BorderBrush = (Brush)FindResource(editable ? "AccentBrush" : "BorderBrush");
        var foreground = (Brush)FindResource(editable ? "BgBrush" : "SecondaryTextBrush");
        _modeGlyph.Foreground = foreground;
        _modeText.Foreground = foreground;
        _modeGlyph.Text = editable ? "\uE70F" : "\uE72E";   // 铅笔 / 锁
        _modeText.Text = editable ? "可编辑" : "只读";
        _modeBadge.ToolTip = editable
            ? "可以改：点「确定」或 Ctrl+Enter 把内容交回流程，Esc 取消"
            : "只能看：可以全选复制，但改不了内容；Esc 或「关闭」关掉";

        // 只读偏灰、没有文本光标、鼠标是箭头：一眼看出不是输入框（但仍能选中复制）。
        BodyHost.Background = (Brush)FindResource(editable ? "SurfaceBrush" : "SurfaceAltBrush");
        Body.Cursor = editable ? Cursors.IBeam : Cursors.Arrow;
        Body.IsReadOnlyCaretVisible = false;

        OkBtn.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        CloseBtn.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
    }

    private void WrapCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (Body == null) return;   // XAML 初始化期间 IsChecked 先于 Body 赋值
        bool wrap = WrapCheck.IsChecked == true;
        Body.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Body.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }
}
