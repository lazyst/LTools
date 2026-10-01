using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Features;

namespace LTools.Views;

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

        // 不做 Opacity 淡入/淡出：不透明窗口上 Opacity<1 会触发 WS_EX_LAYERED（分层窗），
        // 而 DWM 销毁分层窗口时会短暂闪烁（见 CHANGELOG v1.4.3「菜单关闭闪烁修复」——彼时以
        // WinHide 规避，WPF 版未移植）。故菜单打开/关闭均无 Opacity 动画，窗口全程不透明、
        // 非分层 → ClearType 正常、关闭无黑闪（与 SuperPanel 即关即毁一致）。
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
        // 切换菜单组时旧窗口失活、新窗口已接管 _current；忽略旧窗口的 Deactivated，避免误关新菜单
        if (sender is System.Windows.Window w && !MenuSystem.IsCurrentWindow(w)) return;
        _isClosing = true;
        try { MenuSystem.CloseCurrent(); } catch { /* 静默 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => MenuSystem.CloseCurrent();
}
