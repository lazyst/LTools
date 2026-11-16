using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 截图覆盖层的浮动工具栏：工具切换、样式参数、撤销 / 重做与完成动作。
/// </summary>
/// <remarks>
/// 工具按钮按 <see cref="ToolCatalog.All"/> 生成、参数格按 <see cref="ToolCatalog.Uses"/> 显隐：
/// 新增工具只改目录表，**别在这里另写一份清单**。
/// 工具栏自身只改 <see cref="ToolStyle"/> 并抛事件，文档与画面由覆盖层负责。
/// </remarks>
public partial class CaptureToolbar : UserControl
{
    private readonly Dictionary<CaptureTool, RadioButton> _toolButtons = new();
    private readonly Dictionary<ToolParam, FrameworkElement> _paramHosts = new();
    private readonly List<Action> _refreshers = new();
    private readonly List<RadioButton> _colorChips = new();

    private ToolStyle? _style;
    private bool _built;
    private bool _syncing;

    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft, _dragTop;

    /// <summary>用户切了工具。</summary>
    internal event Action<CaptureTool>? ToolChanged;

    /// <summary>样式（颜色 / 线宽 / 字号 / 块 / 透明度）变了。</summary>
    public event Action? StyleChanged;

    public event Action? UndoRequested;
    public event Action? RedoRequested;
    public event Action? CopyRequested;
    public event Action? PinRequested;
    public event Action? RecognizeRequested;
    public event Action? SaveRequested;
    public event Action? QuickSaveRequested;
    public event Action? PrintRequested;
    public event Action? CancelRequested;

    internal CaptureTool CurrentTool { get; private set; } = CaptureTool.Select;

    public CaptureToolbar()
    {
        InitializeComponent();
    }

    /// <summary>用户自己拖过位置（之后不再跟着默认落点跑；双击复位会清掉）。</summary>
    public bool UserMoved { get; private set; }

    /// <summary>按样式与当前工具建一次按钮（覆盖层构造完调）。</summary>
    internal void Init(ToolStyle style, CaptureTool current)
    {
        if (_built) return;
        _built = true;
        _style = style;

        BuildTools();
        BuildActions();
        BuildParams();

        Sync(current);
        RefreshValues();
    }

    /// <summary>界面跟着工具的默认状态对齐（覆盖层切工具时调，会同步按钮的选中态）。</summary>
    internal void Sync(CaptureTool tool)
    {
        CurrentTool = tool;

        _syncing = true;
        if (_toolButtons.TryGetValue(tool, out var button)) button.IsChecked = true;
        _syncing = false;

        foreach (var (param, host) in _paramHosts)
            host.Visibility = ToolCatalog.Uses(tool, param) ? Visibility.Visible : Visibility.Collapsed;

        ParamBar.Visibility = ToolCatalog.Of(tool).Params.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshValues();
    }

    /// <summary>撤销 / 重做按钮的可用状态。</summary>
    public void SetUndo(bool canUndo, bool canRedo)
    {
        if (_undoBtn != null) _undoBtn.IsEnabled = canUndo;
        if (_redoBtn != null) _redoBtn.IsEnabled = canRedo;
    }

    /// <summary>放到屏幕底部中间（第一次显示与双击标题栏时用）。</summary>
    public void PlaceDefault(double width, double height)
    {
        Remeasure();
        double left = Math.Max(8, (width - What(ActualWidth, DesiredSize.Width)) / 2);
        double top = Math.Max(8, height - What(ActualHeight, DesiredSize.Height) - 20);
        if (Parent is Canvas)
        {
            Canvas.SetLeft(this, left);
            Canvas.SetTop(this, top);
        }
    }

    /// <summary>
    /// 把工具栏收回可见区。切工具会改高度（「只有一排」↔「再加参数条」），
    /// 不变高的那一次放置会让底部那截被画布裁掉。
    /// </summary>
    public void ClampInside(double width, double height)
    {
        if (Parent is not Canvas) return;

        Remeasure();
        double w = What(ActualWidth, DesiredSize.Width);
        double h = What(ActualHeight, DesiredSize.Height);
        double left = Canvas.GetLeft(this), top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        Canvas.SetLeft(this, Math.Clamp(left, 0, Math.Max(0, width - w)));
        Canvas.SetTop(this, Math.Clamp(top, 0, Math.Max(0, height - h)));
    }

    /// <summary>
    /// 现跑一次布局再量。只 <c>Measure</c> 不行：参数条刚切显隐时子树的 <c>IsMeasureValid</c>
    /// 还是 true，WPF 直接把「只有一排」的旧 <c>DesiredSize</c> 还回来，
    /// 于是按矮的高度贴底、底下一截被画布裁掉。
    /// </summary>
    private void Remeasure() => UpdateLayout();

    /// <summary>布局跑过就用实际尺寸，没有（还没显示 / 还没量）才退回 <c>DesiredSize</c>。</summary>
    private static double What(double actual, double desired) => actual > 0.5 ? actual : desired;

    // ============ 构建 ============

    private void BuildTools()
    {
        foreach (var info in ToolCatalog.All)
        {
            var icon = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource("Icon" + info.Icon),
                Style = (Style)FindResource("IconPath"),
            };
            var button = new RadioButton
            {
                Style = (Style)FindResource("ToolBtn"),
                GroupName = "captureTool",
                ToolTip = $"{info.Name}（{info.Shortcut}）\n{info.Hint}",
                Tag = info.Tool,
                Content = icon,
            };
            button.Checked += (_, _) =>
            {
                if (_syncing) return;
                ToolChanged?.Invoke((CaptureTool)button.Tag);
            };
            _toolButtons[info.Tool] = button;
            ToolRow.Children.Add(button);
        }
    }

    private void BuildActions()
    {
        ToolRow.Children.Add(new Border { Style = (Style)FindResource("SeparatorBar") });

        _undoBtn = AddAction("IconUndo", "撤销（Ctrl+Z）", () => UndoRequested?.Invoke());
        _redoBtn = AddAction("IconRedo", "重做（Ctrl+Y）", () => RedoRequested?.Invoke());

        ToolRow.Children.Add(new Border { Style = (Style)FindResource("SeparatorBar") });

        AddAction("IconCopy", "复制到剪贴板（Enter）", () => CopyRequested?.Invoke());
        AddAction("IconPin", "贴图到屏幕（Ctrl+T）", () => PinRequested?.Invoke());
        AddAction("IconRecognize", "识别文字 / 表格 / 二维码（Ctrl+R）", () => RecognizeRequested?.Invoke());
        AddAction("IconSave", "另存为…（Ctrl+S）", () => SaveRequested?.Invoke());
        AddAction("IconQuickSave", "快速保存到图片\\LTools 截图，不弹框（Ctrl+Shift+S）",
            () => QuickSaveRequested?.Invoke());
        AddAction("IconPrint", "打印（Ctrl+P）", () => PrintRequested?.Invoke());
        AddAction("IconClose", "取消（Esc）", () => CancelRequested?.Invoke());
    }

    private Button? _undoBtn, _redoBtn;

    private Button AddAction(string icon, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ActBtn"),
            ToolTip = tip,
            Content = new System.Windows.Shapes.Path
            {
                Data = (Geometry)FindResource(icon),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) => click();
        ToolRow.Children.Add(button);
        return button;
    }

    private void BuildParams()
    {
        // 颜色
        var colors = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        colors.Children.Add(Label("颜色"));
        foreach (var color in ToolStyle.Palette)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ColorChip"),
                GroupName = "captureColor",
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = $"#{(int)color.R:X2}{(int)color.G:X2}{(int)color.B:X2}",
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing || _style == null) return;
                _style.Color = (Color)chip.Tag;
                StyleChanged?.Invoke();
            };
            _colorChips.Add(chip);
            colors.Children.Add(chip);
        }
        AddHost(ToolParam.Color, colors);

        // 线宽 / 字号 / 块大小 / 透明度
        AddHost(ToolParam.Width, Step("线宽", 1, 50, 1,
            () => _style?.Width ?? 4, v => { if (_style != null) _style.Width = v; },
            v => v.ToString("0"), 4));

        AddHost(ToolParam.FontSize, Step("字号", 8, 72, 2,
            () => _style?.FontSize ?? 20, v => { if (_style != null) _style.FontSize = v; },
            v => v.ToString("0"), 20));

        AddHost(ToolParam.Block, Step("块大小", 4, 24, 2,
            () => _style?.Block ?? 8, v => { if (_style != null) _style.Block = v; },
            v => v.ToString("0"), 8));

        AddHost(ToolParam.Opacity, Step("透明度", 10, 90, 5,
            () => (_style?.Opacity ?? 0.35) * 100, v => { if (_style != null) _style.Opacity = v / 100; },
            v => v.ToString("0") + "%", 35));

        // 序号的形状与数字写法（枚举用文字胶囊：图标认不出「圆角方 / 罗马数字」）
        AddHost(ToolParam.Shape, Chips("形状",
            new[] { SerialShape.Circle, SerialShape.Square, SerialShape.Rounded },
            () => _style?.Shape ?? SerialShape.Circle,
            v => { if (_style != null) _style.Shape = v; },
            SerialLabel.Name, v => "徽标形状：" + SerialLabel.Name(v)));

        AddHost(ToolParam.Numbering, Chips("编号",
            new[]
            {
                SerialNumbering.Arabic, SerialNumbering.RomanUpper, SerialNumbering.RomanLower,
                SerialNumbering.AlphaUpper, SerialNumbering.AlphaLower, SerialNumbering.Chinese,
            },
            () => _style?.Numbering ?? SerialNumbering.Arabic,
            v => { if (_style != null) _style.Numbering = v; },
            SerialLabel.Chip, v => "数字写法：" + SerialLabel.Of(8, v)));

        // 滤镜类型与强度
        AddHost(ToolParam.FilterKind, Chips("类型",
            new[] { FilterKind.Blur, FilterKind.Grayscale, FilterKind.Invert, FilterKind.Emboss, FilterKind.Brighten },
            () => _style?.FilterMode ?? FilterKind.Blur,
            v => { if (_style != null) _style.FilterMode = v; },
            SerialLabel.Name, v => "滤镜：" + SerialLabel.Name(v)));

        AddHost(ToolParam.FilterStrength, Step("强度", 0, 100, 5,
            () => _style?.FilterStrength ?? 40, v => { if (_style != null) _style.FilterStrength = v; },
            v => v.ToString("0"), 40));
    }

    /// <summary>
    /// 枚举型参数：一排文字胶囊，点了就换（单选）。
    /// 与颜色胶囊同一套「_syncing 防回环」写法，别让刷新把用户刚点的值又切回去。
    /// </summary>
    private FrameworkElement Chips<T>(string label, T[] values, Func<T> get, Action<T> set,
        Func<T, string> text, Func<T, string> tip)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(Label(label));

        var chips = new List<(RadioButton Button, T Value)>();
        foreach (var value in values)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChoiceChip"),
                GroupName = "captureChoice" + label,
                Content = text(value),
                ToolTip = tip(value),
                Tag = value,
            };
            chip.Checked += (_, _) =>
            {
                if (_syncing) return;
                set(value);
                StyleChanged?.Invoke();
            };
            chips.Add((chip, value));
            host.Children.Add(chip);
        }

        _refreshers.Add(() =>
        {
            var current = get();
            _syncing = true;
            foreach (var (button, value) in chips)
                button.IsChecked = EqualityComparer<T>.Default.Equals(value, current);
            _syncing = false;
        });

        return host;
    }

    private void AddHost(ToolParam param, FrameworkElement host)
    {
        _paramHosts[param] = host;
        ParamRow.Children.Add(host);
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
        FontSize = 12,
        Margin = new Thickness(10, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement Step(string label, double min, double max, double step,
        Func<double> get, Action<double> set, Func<double, string> format, double reset)
    {
        // 透明底色：整格（不只是字形）都能滚轮 / 点击，不然「点在数字旁边空白处」没反应
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        host.Children.Add(Label(label));

        var value = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE7)),
            FontSize = 12,
            MinWidth = 40,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeWE,
            ToolTip = $"滚轮微调（{step:0.##}）· 点一下复位到 {format(reset)}",
        };

        Button Minus() => new()
        {
            Style = (Style)FindResource("StepBtn"),
            Content = "−",
        };

        var minus = Minus();
        var plus = Minus();
        plus.Content = "+";

        minus.Click += (_, _) =>
        {
            set(Math.Max(min, Math.Round(get() - step)));
            RefreshValues();
            StyleChanged?.Invoke();
        };
        plus.Click += (_, _) =>
        {
            set(Math.Min(max, Math.Round(get() + step)));
            RefreshValues();
            StyleChanged?.Invoke();
        };

        // 滚轮在数值上直接微调（照 Snow Shot：光标停在参数上滚就是调它）；
        // 点一下数值复位——「调过头了」比「找不到恢复按钮」常见得多。
        host.MouseWheel += (_, e) =>
        {
            double next = Math.Clamp(Math.Round(get() + (e.Delta > 0 ? step : -step)), min, max);
            if (Math.Abs(next - get()) < 1e-9) return;
            set(next);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };
        value.MouseLeftButtonUp += (_, e) =>
        {
            set(reset);
            RefreshValues();
            StyleChanged?.Invoke();
            e.Handled = true;
        };

        host.Children.Add(minus);
        host.Children.Add(value);
        host.Children.Add(plus);

        _refreshers.Add(() => value.Text = format(get()));
        return host;
    }

    private void RefreshValues()
    {
        if (_style == null) return;
        foreach (var refresh in _refreshers) refresh();

        _syncing = true;
        foreach (var chip in _colorChips)
            chip.IsChecked = (Color)chip.Tag == _style.Color;
        _syncing = false;
    }

    // ============ 拖动 / 双击复位 ============

    private void Chrome_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            UserMoved = false;
            if (Parent is FrameworkElement parent) PlaceDefault(parent.ActualWidth, parent.ActualHeight);
            e.Handled = true;
            return;
        }

        if (Parent is not Canvas canvas) return;
        UserMoved = true;
        _dragging = true;
        _dragStart = e.GetPosition(canvas);
        _dragLeft = Canvas.GetLeft(this);
        _dragTop = Canvas.GetTop(this);
        if (double.IsNaN(_dragLeft)) _dragLeft = 0;
        if (double.IsNaN(_dragTop)) _dragTop = 0;
        CaptureMouse();
        e.Handled = true;
    }

    private void Chrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Parent is not Canvas canvas) return;

        Point at = e.GetPosition(canvas);
        double left = Math.Clamp(_dragLeft + (at.X - _dragStart.X), 0, Math.Max(0, canvas.ActualWidth - ActualWidth));
        double top = Math.Clamp(_dragTop + (at.Y - _dragStart.Y), 0, Math.Max(0, canvas.ActualHeight - ActualHeight));
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        e.Handled = true;
    }

    private void Chrome_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>点在按钮上时不要启动拖动（否则点工具会顺手把工具栏拖走）。</summary>
    private static bool IsOnControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or Thumb or Slider) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
