using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 截图 Pro 的全局快捷键：一个动作一行，最多两个快捷键（照 Snow Shot「全局快捷键」页的
/// <c>ShortcutKeyRow</c> / <c>maxShortcutCount = 2</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 这里不另造一套注册：读写的就是 <c>configs/hotkeys.json</c> 里名为「截图 Pro」的预设组
/// （见 <see cref="CaptureShortcuts"/>），所以「快捷键」页里看到的是同一份数据，冲突判定、
/// 生效范围、CapsLock 修饰键、暂停总开关全部复用既有实现。
/// </para>
/// <para>
/// 任何改动立刻生效并落盘（<see cref="HotkeyRegistry.Load(HotkeyFile)"/> + <see cref="HotkeyStore"/>），
/// 没有「确定 / 取消」——只有「关闭」，所以关掉窗口不会丢东西。
/// </para>
/// </remarks>
public partial class CaptureShortcutsWindow : Window
{
    private const string EmptyChip = "＋ 添加";

    private sealed record RowView(
        CaptureShortcut Action, TextBox First, TextBox Second, TextBlock Status, Button Clear);

    private readonly HotkeyFile _file;
    private readonly List<RowView> _rows = new();
    private bool _syncing;

    public CaptureShortcutsWindow()
    {
        InitializeComponent();

        _file = HotkeyRegistry.File.Clone();
        BuildRows();
        RefreshState();
    }

    /// <summary>打开对话框（改动即时保存，返回值没有意义）。</summary>
    public static void Show(Window? owner)
    {
        var window = new CaptureShortcutsWindow();
        if (owner != null)
        {
            window.Owner = owner;
            window.Topmost = owner.Topmost;
        }

        window.ShowDialog();
    }

    // ============ 构建 ============

    private void BuildRows()
    {
        RowsHost.Children.Clear();
        _rows.Clear();

        foreach (var action in CaptureShortcuts.All)
        {
            var label = new TextBlock
            {
                Text = action.Label,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var hint = new TextBlock
            {
                Text = action.Hint,
                Style = (Style)FindResource("HintLabel"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
            };
            hint.ToolTip = action.Hint;

            var captions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            captions.Children.Add(label);
            captions.Children.Add(hint);

            var first = MakeChip();
            var second = MakeChip();

            var status = new TextBlock
            {
                Text = "冲突",
                Foreground = (Brush)FindResource("DangerBrush"),
                Style = (Style)FindResource("HintLabel"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "这个组合键和别的（启用中的）绑定重复，只有排在前面的那条会触发",
            };

            var clear = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("BtnGhostDanger"),
                MinWidth = 28,
                Height = 28,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "清空这一行的快捷键",
            };

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(first);
            right.Children.Add(second);
            right.Children.Add(status);
            right.Children.Add(clear);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(captions, 0);
            Grid.SetColumn(right, 1);
            row.Children.Add(captions);
            row.Children.Add(right);

            RowsHost.Children.Add(row);

            var view = new RowView(action, first, second, status, clear);
            _rows.Add(view);

            // 录制回调要认行：闭包捕获 view
            HotkeyCapture.Attach(first, _ => CommitRow(view));
            HotkeyCapture.Attach(second, _ => CommitRow(view));
            clear.Click += (_, _) => ClearRow(view);
        }

        foreach (var row in _rows) RefreshChips(row);
    }

    private TextBox MakeChip()
    {
        var box = new TextBox
        {
            MinWidth = 104,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "点一下再按组合键即可录入（Esc 取消）",
        };

        // Attach 会把框设成只读并负责录制；空值显示成「＋ 添加」
        HotkeyCapture.SetHotkey(box, "");
        return box;
    }

    // ============ 行内交互 ============

    private void CommitRow(RowView row)
    {
        if (_syncing) return;

        var chords = new List<string>(2);
        string first = HotkeyCapture.GetHotkey(row.First);
        string second = HotkeyCapture.GetHotkey(row.Second);
        if (first.Length > 0) chords.Add(first);
        if (second.Length > 0) chords.Add(second);

        CaptureShortcuts.SetChords(_file, row.Action, chords);
        Persist();

        RefreshChips(row);
        RefreshState();
    }

    private void ClearRow(RowView row)
    {
        // 正在录的那一格先收尾（点按钮会先离开焦点触发一次 commit）
        CaptureShortcuts.SetChords(_file, row.Action, Array.Empty<string>());
        Persist();

        RefreshChips(row);
        RefreshState();
    }

    private void RefreshChips(RowView row)
    {
        var chords = CaptureShortcuts.ChordsOf(_file, row.Action);

        _syncing = true;
        try
        {
            SetChip(row.First, chords.Count > 0 ? chords[0] : "");
            SetChip(row.Second, chords.Count > 1 ? chords[1] : "");
            row.Clear.IsEnabled = chords.Count > 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SetChip(TextBox box, string chord)
    {
        HotkeyCapture.SetHotkey(box, chord);
        if (chord.Length == 0) box.Text = EmptyChip;
        box.Foreground = (Brush)box.FindResource(chord.Length == 0 ? "HintTextBrush" : "TextBrush");
    }

    private void RefreshState()
    {
        var conflicts = CaptureShortcuts.Conflicting(_file);

        foreach (var row in _rows)
        {
            bool conflict = conflicts.Contains(row.Action.Id);
            row.Status.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;

            var brush = (Brush)FindResource(conflict ? "DangerBrush" : "BorderStrongBrush");
            row.First.BorderBrush = brush;
            row.Second.BorderBrush = brush;
        }

        SummaryText.Text = "当前：" + CaptureShortcuts.Summary(_file, 6);

        _syncing = true;
        try
        {
            EnableBox.IsChecked = HotkeyRegistry.GlobalEnabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ============ 顶栏 / 底部 ============

    private void Enable_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        HotkeyRegistry.GlobalEnabled = EnableBox.IsChecked == true;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        foreach (var action in CaptureShortcuts.All)
            CaptureShortcuts.SetChords(_file, action, action.Defaults);

        Persist();

        foreach (var row in _rows) RefreshChips(row);
        RefreshState();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ============ 落盘 ============

    private void Persist()
    {
        _file.Normalize();
        HotkeyRegistry.Load(_file);   // 钩子下一按就是新配置

        string path = string.IsNullOrWhiteSpace(ConfigStore.ConfigPath)
            ? ConfigLocator.FindPath()
            : ConfigStore.ConfigPath;

        try
        {
            HotkeyStore.Modify(path, store =>
            {
                var copy = _file.Clone();
                store.SchemaVersion = copy.SchemaVersion;
                store.DefaultScope = copy.DefaultScope;
                store.DefaultFilter = copy.DefaultFilter;
                store.SeedVersion = copy.SeedVersion;
                store.CaptureSeedVersion = copy.CaptureSeedVersion;
                store.Groups = copy.Groups;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hotkeys", "保存截图 Pro 快捷键失败：" + ex.Message);
        }
    }
}
