using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「设置 → 通用 → 截图 Pro · 覆盖层内快捷键」：四张表（覆盖层 / 绘图 / 贴图 / 录屏）一个页签一张，
/// 一行一个动作、最多两个组合键。
/// </summary>
/// <remarks>
/// <para>
/// 默认值逐条照 Snow Shot 的 <c>screenshot_shortcuts</c> / <c>drawing_shortcuts</c> /
/// <c>pin_to_screen_shortcuts</c> / <c>screen_recording_shortcuts</c>；用户改过的项只写进
/// <c>configs/settings.json</c> 的 <c>CaptureShortcuts</c> 段（缺项 = 默认值，空列表 = 明确不绑）。
/// </para>
/// <para>
/// 与 <see cref="CaptureShortcutsWindow"/>（全局热键，落 <c>hotkeys.json</c>）是两件事：
/// 这里管的是**覆盖层 / 编辑窗 / 贴图窗 / 录屏面板里面**的按键。改动即时生效、没有「确定 / 取消」。
/// </para>
/// </remarks>
public partial class CaptureKeymapWindow : Window
{
    private const string EmptyChip = "＋";

    private sealed record RowView(
        CaptureShortcutScope Scope,
        string Id,
        TextBox[] Chips,
        Button Clear,
        Button Reset);

    private readonly List<RowView> _rows = new();
    private bool _syncing;

    public CaptureKeymapWindow()
    {
        InitializeComponent();

        Build(ScreenshotHost, CaptureShortcutScope.Screenshot);
        Build(DrawingHost, CaptureShortcutScope.Drawing);
        Build(PinHost, CaptureShortcutScope.PinToScreen);
        Build(RecordingHost, CaptureShortcutScope.Recording);

        RefreshAll();
    }

    /// <summary>打开对话框（改动即时保存，返回值没有意义）。</summary>
    public static void Show(Window? owner)
    {
        var window = new CaptureKeymapWindow();
        if (owner != null)
        {
            window.Owner = owner;
            window.Topmost = owner.Topmost;
        }

        window.ShowDialog();
    }

    // ============ 构建 ============

    private void Build(StackPanel host, CaptureShortcutScope scope)
    {
        foreach (var entry in CaptureShortcutTable.Of(scope))
        {
            var label = new TextBlock
            {
                Text = entry.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"{entry.Label}（{entry.Id}）",
            };

            var chips = new List<TextBox>(2);
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            for (int i = 0; i < 2; i++)
            {
                var box = MakeChip();
                chips.Add(box);
                right.Children.Add(box);
            }

            var clear = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("BtnGhostDanger"),
                MinWidth = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "清空这一行的快捷键",
            };

            var reset = new Button
            {
                Content = "↺",
                Style = (Style)FindResource("BtnGhost"),
                MinWidth = 26,
                Height = 26,
                Padding = new Thickness(0),
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "恢复这一项的默认快捷键",
            };

            right.Children.Add(clear);
            right.Children.Add(reset);

            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(right, 1);
            row.Children.Add(label);
            row.Children.Add(right);

            host.Children.Add(row);

            var view = new RowView(scope, entry.Id, chips.ToArray(), clear, reset);
            _rows.Add(view);

            // 录制回调认「第几个格子」：闭包捕获 view + index，不靠读框里的字（空槽显示的是「＋」）
            for (int i = 0; i < view.Chips.Length; i++)
            {
                int index = i;
                CaptureChordRecorder.Attach(view.Chips[i], chord => CommitChip(view, index, chord));
            }

            clear.Click += (_, _) => ClearRow(view);
            reset.Click += (_, _) => ResetRow(view);
        }
    }

    private TextBox MakeChip()
    {
        var box = new TextBox
        {
            MinWidth = 104,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };

        box.Text = EmptyChip;
        return box;
    }

    // ============ 行内交互 ============

    private void CommitChip(RowView row, int index, string chord)
    {
        if (_syncing || string.IsNullOrWhiteSpace(chord)) return;

        var chords = new List<string>(CaptureShortcutTable.Chords(row.Scope, row.Id));
        while (chords.Count <= index) chords.Add("");
        chords[index] = chord;
        chords.RemoveAll(string.IsNullOrWhiteSpace);

        CaptureShortcutTable.Set(row.Scope, row.Id, chords);
        RefreshRow(row);
        RefreshSummary();
    }

    private void ClearRow(RowView row)
    {
        CaptureShortcutTable.Set(row.Scope, row.Id, Array.Empty<string>());
        RefreshRow(row);
        RefreshSummary();
    }

    private void ResetRow(RowView row)
    {
        CaptureShortcutTable.Reset(row.Scope, row.Id);
        RefreshRow(row);
        RefreshSummary();
    }

    private void RefreshRow(RowView row)
    {
        var chords = CaptureShortcutTable.Chords(row.Scope, row.Id);

        _syncing = true;
        try
        {
            for (int i = 0; i < row.Chips.Length; i++)
            {
                var box = row.Chips[i];
                bool filled = i < chords.Count;
                box.Text = filled ? chords[i] : EmptyChip;
                box.Foreground = (Brush)FindResource(filled ? "TextBrush" : "HintTextBrush");
            }

            row.Clear.IsEnabled = chords.Count > 0;
            row.Reset.IsEnabled = !CaptureShortcutTable.IsDefault(row.Scope, row.Id);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshAll()
    {
        foreach (var row in _rows) RefreshRow(row);
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        int custom = 0;
        foreach (CaptureShortcutScope scope in Enum.GetValues<CaptureShortcutScope>())
            custom += CaptureShortcutTable.CustomCount(scope);

        SummaryText.Text = custom == 0 ? "全部为默认值" : $"{custom} 项已改";
    }

    // ============ 底部 ============

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (CaptureShortcutScope scope in Enum.GetValues<CaptureShortcutScope>())
            CaptureShortcutTable.ResetAll(scope);

        RefreshAll();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
