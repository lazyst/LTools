using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>菜单弹出窗口（对应 lib/ui/MenuUI.ahk CreateMenuGUI）。
/// 无边框置顶；标题 + 序号按钮列表 + 关闭；Esc/点击外部/选号 关闭。</summary>
public partial class MenuPopupWindow : Window
{
    private readonly int _groupIndex;
    private readonly IReadOnlyList<string> _itemNames;
    private bool _isClosing;

    public MenuPopupWindow(string groupName, int groupIndex, IReadOnlyList<string> itemNames)
    {
        InitializeComponent();
        _groupIndex = groupIndex;
        _itemNames = itemNames;
        TitleText.Text = groupName;

        for (int i = 0; i < itemNames.Count; i++)
        {
            string numText = (i < 9) ? (i + 1).ToString() : "0";
            int itemIndex = i + 1;

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var num = new TextBlock
            {
                Text = numText,
                Foreground = (System.Windows.Media.Brush)FindResource("MenuAccentBrush"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(num, 0);
            row.Children.Add(num);

            var btn = new Button
            {
                Style = (Style)FindResource("BtnGhost"),
                Content = itemNames[i],
                MinHeight = 36,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            btn.Click += (_, _) => MenuSystem.SelectItem(_groupIndex, itemIndex);
            Grid.SetColumn(btn, 1);
            row.Children.Add(btn);

            ItemsHost.Children.Add(row);
        }

        // 淑入动画（对应 AHK FadeInWindow 150ms）
        Opacity = 0;
        Loaded += (_, _) =>
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
    }

    // 淡出动画（对应 AHK FadeOutWindow 100ms）：关闭时先淡出再真实销毁
    private bool _fading;
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_fading)
        {
            _fading = true;
            e.Cancel = true;
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            anim.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, anim);
            return;
        }
        base.OnClosing(e);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        int idx = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            Key.D0 or Key.NumPad0 => 10,
            _ => -1,
        };
        if (idx >= 1)
        {
            if (idx <= _itemNames.Count)
                MenuSystem.SelectItem(_groupIndex, idx);
            else
                MenuSystem.CloseCurrent();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            MenuSystem.CloseCurrent();
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_isClosing) return;
        // 旧窗口淡出动画期间，新窗口已接管 _current；忽略旧窗口的 Deactivated，避免误关新菜单
        if (sender is System.Windows.Window w && !MenuSystem.IsCurrentWindow(w)) return;
        _isClosing = true;
        try { MenuSystem.CloseCurrent(); } catch { /* 静默 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => MenuSystem.CloseCurrent();
}
