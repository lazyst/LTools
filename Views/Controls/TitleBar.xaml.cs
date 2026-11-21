using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace LTools.Views.Controls;

/// <summary>可复用自定义标题栏。配合 <c>WindowChrome</c> 使用：标题区可拖动、双击最大化；
/// 右侧最小化/最大化/关闭按钮。按钮通过 <c>WindowChrome.IsHitTestVisibleInChrome</c> 接收点击。</summary>
public partial class TitleBar
{
    private Window? _window;

    private static readonly Geometry MaximizeIcon = Geometry.Parse("M 1,1 L 9,1 L 9,9 L 1,9 Z");
    private static readonly Geometry RestoreIcon = Geometry.Parse("M 1,1 L 6,1 L 6,6 L 1,6 Z M 4,4 L 9,4 L 9,9 L 4,9 Z");

    public static readonly DependencyProperty ShowMinimizeProperty = DependencyProperty.Register(
        nameof(ShowMinimize), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(true, (d, _) => ((TitleBar)d).MinimizeButton.Visibility =
            ((TitleBar)d).ShowMinimize ? Visibility.Visible : Visibility.Collapsed));

    public static readonly DependencyProperty ShowMaximizeProperty = DependencyProperty.Register(
        nameof(ShowMaximize), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(true, (d, _) => ((TitleBar)d).MaximizeButton.Visibility =
            ((TitleBar)d).ShowMaximize ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>是否显示标题栏上的「置顶」图钉（默认不显示，只有速记窗口打开它）。</summary>
    public static readonly DependencyProperty ShowPinProperty = DependencyProperty.Register(
        nameof(ShowPin), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, (d, _) => ((TitleBar)d).PinButton.Visibility =
            ((TitleBar)d).ShowPin ? Visibility.Visible : Visibility.Collapsed));

    /// <summary>图钉的显示状态（窗口的 <c>Topmost</c>）；深色 = 已置顶，淡色 = 未置顶。</summary>
    public static readonly DependencyProperty IsPinnedProperty = DependencyProperty.Register(
        nameof(IsPinned), typeof(bool), typeof(TitleBar),
        new PropertyMetadata(false, (d, _) => ((TitleBar)d).UpdatePinState()));

    public static readonly DependencyProperty ExtraContentProperty = DependencyProperty.Register(
        nameof(ExtraContent), typeof(object), typeof(TitleBar),
        new PropertyMetadata(null, (d, e) => ((TitleBar)d).ExtraHost.Content = e.NewValue));

    public bool ShowMinimize { get => (bool)GetValue(ShowMinimizeProperty); set => SetValue(ShowMinimizeProperty, value); }
    public bool ShowMaximize { get => (bool)GetValue(ShowMaximizeProperty); set => SetValue(ShowMaximizeProperty, value); }
    public bool ShowPin { get => (bool)GetValue(ShowPinProperty); set => SetValue(ShowPinProperty, value); }
    public bool IsPinned { get => (bool)GetValue(IsPinnedProperty); set => SetValue(IsPinnedProperty, value); }

    /// <summary>点了「置顶」图钉（窗口自己翻转 <c>Topmost</c>，别在这里改）。</summary>
    public event EventHandler? PinClicked;

    /// <summary>标题右侧的可选内容（如「普通动作 / 组合动作」切换）。默认为空，不影响其他窗口。</summary>
    public object? ExtraContent { get => GetValue(ExtraContentProperty); set => SetValue(ExtraContentProperty, value); }

    public TitleBar()
    {
        InitializeComponent();
        UpdatePinState();
        Loaded += OnLoaded;
    }

    private void UpdatePinState()
    {
        PinIcon.Opacity = IsPinned ? 1.0 : 0.4;
        PinButton.ToolTip = IsPinned ? "取消置顶" : "置顶窗口";
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => PinClicked?.Invoke(this, EventArgs.Empty);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window == null) return;
        // 标题文字绑定到宿主窗口的 Title
        TitleText.SetBinding(TextBlock.TextProperty, new Binding
        {
            Source = _window,
            Path = new PropertyPath(nameof(Window.Title)),
            Mode = BindingMode.OneWay,
        });
        _window.StateChanged += (_, _) =>
        {
            UpdateMaximizeIcon();
            // 无最大化按钮的窗口（对话框）禁止双击标题栏最大化
            if (!ShowMaximize && _window.WindowState == WindowState.Maximized)
                _window.WindowState = WindowState.Normal;
        };
        UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        if (_window == null) return;
        MaxIcon.Data = _window.WindowState == WindowState.Maximized ? RestoreIcon : MaximizeIcon;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => _window!.WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_window == null) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
