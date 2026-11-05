using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Views.Controls;

/// <summary>
/// 支持 <c>${变量}</c> 的输入框：右侧有一个小小的「$」按钮，点开就是当前流程的用户变量，
/// 选中后插入到光标处。
/// </summary>
/// <remarks>
/// <para>
/// 变量列表来自 <see cref="VariableSource.NamesProperty"/>（沿逻辑树继承的附加属性），
/// 编辑窗口构造时设置一次即可，各输入框不用逐个传参。没有变量时按钮自动隐藏，
/// 外观与普通输入框一致。
/// </para>
/// <para>
/// 按钮不吃焦点（<see cref="UIElement.Focusable"/> = false，并在按下时阻止默认处理），
/// 因此插入位置始终是打开下拉前的光标 / 选区。
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
            _popup.IsOpen = false;
            e.Handled = true;
        };
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_button != null)
            _button.PreviewMouseLeftButtonDown -= Button_PreviewMouseLeftButtonDown;

        _button = GetTemplateChild("PART_VarButton") as Button;
        if (_button != null)
        {
            // 点按钮时焦点要留在输入框里，否则选区会丢、插入位置变成 0。
            _button.Focusable = false;
            _button.IsTabStop = false;
            _button.PreviewMouseLeftButtonDown += Button_PreviewMouseLeftButtonDown;
        }

        RefreshAvailability();
    }

    private void RefreshAvailability() => HasVariables = VariableSource.Resolve(this) != null;

    private void Button_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 不让按钮抢焦点 / 不触发换行等输入行为
        TogglePopup();
    }

    private void TogglePopup()
    {
        var names = VariableSource.Resolve(this);
        if (names == null) return;

        if (_popup == null) BuildPopup();

        if (_popup!.IsOpen)
        {
            _popup.IsOpen = false;
            return;
        }

        _list!.SetCustom(names);
        // 下拉不跟着宽输入框铺满整行（那样像一块白板），但窄输入框也不至于挤成一条。
        double width = Math.Clamp(ActualWidth, 200, 320);
        _popup.MinWidth = width;
        _popup.MaxWidth = width;
        _popup.IsOpen = true;
    }

    private void BuildPopup()
    {
        _list = new VariableList();
        _list.VariableInvoked += InsertVariable;
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _popup!.IsOpen = false;
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
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
        };
    }

    /// <summary>把 <c>${name}</c> 插入光标处（有选区则替换选区），然后把焦点还给输入框。</summary>
    private void InsertVariable(string name)
    {
        _popup!.IsOpen = false;

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
