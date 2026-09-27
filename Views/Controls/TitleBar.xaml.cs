using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace CapsLockPro.Views.Controls;

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

    public bool ShowMinimize { get => (bool)GetValue(ShowMinimizeProperty); set => SetValue(ShowMinimizeProperty, value); }
    public bool ShowMaximize { get => (bool)GetValue(ShowMaximizeProperty); set => SetValue(ShowMaximizeProperty, value); }

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

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
