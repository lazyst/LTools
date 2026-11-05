using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Views.Controls;

/// <summary>
/// 支持 <c>${变量}</c> 的输入框：右侧有一个「插入变量」按钮，点开就是当前流程的用户变量，
/// 选中后插入到光标处。
/// </summary>
/// <remarks>
/// <para>
/// 变量列表来自 <see cref="VariableSource.NamesProperty"/>（沿逻辑树继承的附加属性），
/// 编辑窗口构造时设置一次即可，各输入框不用逐个传参。没有变量时按钮自动隐藏，
/// 外观与普通输入框一致。
/// </para>
/// <para>
/// 按钮不吃焦点（<see cref="UIElement.Focusable"/> = false），因此插入位置始终是
/// 打开下拉前的光标 / 选区。
/// </para>
/// <para>
/// 下拉用 <see cref="Popup.StaysOpen"/> = true 并自己判断「点外面就收起」：
/// 若交给 WPF 的 StaysOpen=false，它会用鼠标捕获来判外部点击，而打开下拉的那次点击
/// 自身就在捕获开始前发生，两者叠加会出现「点下拉里的条目却先被收起、条目点不中」。
/// 自己判断时，下拉里的点击是独立窗口（不会进到主窗口的事件），所以不会误关。
/// </para>
/// </remarks>
public class VariableTextBox : TextBox
{
    /// <summary>是否有可插入的变量；模板用它控制按钮显隐。</summary>
    public static readonly DependencyProperty HasVariablesProperty = DependencyProperty.Register(
        nameof(HasVariables), typeof(bool), typeof(VariableTextBox),
        new FrameworkPropertyMetadata(false));

    public bool HasVariables
    {
        get => (bool)GetValue(HasVariablesProperty);
        set => SetValue(HasVariablesProperty, value);
    }

    private Button? _button;
    private Popup? _popup;
    private VariableList? _list;
    private Window? _host;

    public VariableTextBox()
    {
        Loaded += (_, _) => RefreshAvailability();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) RefreshAvailability();
        };
        // 下拉打开时按 Esc 只收起下拉（不把 Esc 传给对话框，避免顺手把整个窗口关掉）。
        PreviewKeyDown += (_, e) =>
        {
            if (_popup?.IsOpen != true || e.Key != Key.Escape) return;
            ClosePopup();
            e.Handled = true;
        };
        Unloaded += (_, _) => ClosePopup();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_button != null)
            _button.Click -= Button_Click;

        _button = GetTemplateChild("PART_VarButton") as Button;
        if (_button != null)
        {
            // 点按钮时焦点要留在输入框里，否则选区会丢、插入位置变成 0。
            _button.Focusable = false;
            _button.IsTabStop = false;
            // 用标准 Click（按下 + 抬起都走完）打开：这条路径与 WPF 自己的鼠标处理不冲突。
            _button.Click += Button_Click;
        }

        RefreshAvailability();
    }

    private void RefreshAvailability() => HasVariables = VariableSource.Resolve(this) != null;

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_popup?.IsOpen == true) ClosePopup();
        else OpenPopup();
    }

    private void OpenPopup()
    {
        var names = VariableSource.Resolve(this);
        if (names == null) return;

        if (_popup == null) BuildPopup();

        _list!.SetCustom(names);

        // 下拉与输入框同宽：变量名很长时在行内省略，不让下拉比输入框还宽。
        double width = Math.Max(ActualWidth, 120);
        _popup!.Width = width;
        _popup.IsOpen = true;

        _host = Window.GetWindow(this);
        if (_host != null)
        {
            _host.PreviewMouseDown += Host_PreviewMouseDown;
            _host.Deactivated += Host_Deactivated;
            _host.LocationChanged += Host_Moved;
            _host.SizeChanged += Host_Moved;
        }
    }

    private void ClosePopup()
    {
        if (_popup?.IsOpen == true) _popup.IsOpen = false;

        if (_host != null)
        {
            _host.PreviewMouseDown -= Host_PreviewMouseDown;
            _host.Deactivated -= Host_Deactivated;
            _host.LocationChanged -= Host_Moved;
            _host.SizeChanged -= Host_Moved;
            _host = null;
        }
    }

    /// <summary>点主窗口里其它地方就收起；点按钮本身交给 <see cref="Button_Click"/> 切换。</summary>
    private void Host_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_button != null && e.OriginalSource is DependencyObject src &&
            (ReferenceEquals(src, _button) || _button.IsAncestorOf(src)))
            return;

        ClosePopup();
    }

    private void Host_Deactivated(object? sender, EventArgs e) => ClosePopup();

    /// <summary>窗口移动 / 改大小时下拉会与输入框错位，直接收起。</summary>
    private void Host_Moved(object? sender, EventArgs e) => ClosePopup();

    private void BuildPopup()
    {
        _list = new VariableList();
        _list.VariableInvoked += InsertVariable;
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            ClosePopup();
            Focus();
            e.Handled = true;
        };

        var border = new Border
        {
            Background = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White,
            BorderBrush = TryFindResource("BorderStrongBrush") as Brush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.22,
            },
        };
        border.Child = new ScrollViewer
        {
            MaxHeight = 280,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _list,
        };

        _popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = this,
            VerticalOffset = 2,
            // 自己管关闭（见类注释），避免 StaysOpen=false 的鼠标捕获把下拉里的点击吃掉。
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
        };
        _popup.Closed += (_, _) => ClosePopup();
    }

    /// <summary>把 <c>${name}</c> 插入光标处（有选区则替换选区），然后把焦点还给输入框。</summary>
    private void InsertVariable(string name)
    {
        ClosePopup();

        string text = Text ?? "";
        int start = Math.Clamp(SelectionStart, 0, text.Length);
        int length = Math.Clamp(SelectionLength, 0, text.Length - start);
        string token = "${" + name + "}";

        Text = text[..start] + token + text[(start + length)..];
        SelectionStart = start + token.Length;
        SelectionLength = 0;
        CaretIndex = SelectionStart;
        Focus();
    }
}
