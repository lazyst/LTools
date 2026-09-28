using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>
/// 动作池选择器（阶段 3）：卡片网格浏览全局动作清单，搜索筛选，
/// 点击选中，「新建动作…」跳 <see cref="ActionEditorDialog"/> 新建。
/// 供「菜单组」页和「超级面板」槽位共用。
/// </summary>
public partial class ActionPoolPicker : Window
{
    private readonly Window _owner;
    private List<ActionCell> _cells = new();
    private List<ActionCell> _all = new();

    /// <summary>选中的动作 Id；取消时为 null。</summary>
    public string? Result { get; private set; }

    public ActionPoolPicker(Window owner, string title, string? preselectId = null)
    {
        _owner = owner;
        InitializeComponent();
        Title = title;
        HintText.Text = $"当前已选：{(string.IsNullOrEmpty(Result) ? "（未选）" : ActionRegistry.DisplayName(Result ?? ""))}";
        BuildCells(preselectId);
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void BuildCells(string? preselectId)
    {
        _all.Clear();
        foreach (var a in ActionRegistry.All.OrderBy(x => x.Id))
        {
            _all.Add(new ActionCell
            {
                Id = a.Id,
                Name = a.Name,
                Type = ActionTypeLabel.Of(a.Type),
                Glyph = IconCatalog.GetGlyph(a.Icon),
            });
        }
        ApplyFilter(SearchBox.Text);
        // 预选
        if (!string.IsNullOrEmpty(preselectId))
        {
            var sel = _cells.FirstOrDefault(c => c.Id == preselectId);
            if (sel != null) Highlight(sel);
            else Result = preselectId; // 池中已无该动作（例如被删）
        }
    }

    private void ApplyFilter(string keyword)
    {
        _cells = string.IsNullOrWhiteSpace(keyword)
            ? new List<ActionCell>(_all)
            : _all.Where(c => c.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                              || c.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        ActionPanel.Children.Clear();
        foreach (var c in _cells) ActionPanel.Children.Add(BuildCellUI(c));
    }

    private UIElement BuildCellUI(ActionCell c)
    {
        var grid = new Grid { Width = 98, Height = 98, Margin = new Thickness(3), Cursor = System.Windows.Input.Cursors.Hand };
        var bd = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        var inner = new Grid();
        inner.Children.Add(new TextBlock
        {
            Text = c.Glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
        });
        inner.Children.Add(new TextBlock
        {
            Text = c.Name,
            MaxWidth = 90,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 60, 0, 0),
        });
        inner.Children.Add(new TextBlock
        {
            Text = c.Type,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 76, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("HintTextBrush"),
        });
        bd.Child = inner;
        grid.Children.Add(bd);
        grid.Tag = c;
        // 命中透明层：整格都可点
        var hit = new Border { Background = System.Windows.Media.Brushes.Transparent };
        hit.MouseLeftButtonUp += (_, _) => Highlight(c);
        grid.Children.Add(hit);

        if (c.Id == Result) ApplyHighlight(bd);
        return grid;
    }

    private void Highlight(ActionCell? c)
    {
        if (c == null) return;
        Result = c.Id;
        HintText.Text = $"已选：{c.Name}  [{c.Type}]";
        ActionPanel.Children.Clear();
        foreach (var cell in _cells) ActionPanel.Children.Add(BuildCellUI(cell));
    }

    private static void ApplyHighlight(Border bd)
    {
        bd.BorderBrush = (System.Windows.Media.Brush)bd.FindResource("TextBrush");
        bd.BorderThickness = new Thickness(2);
    }

    // —— 搜索 ——
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text ?? "");

    // —— 新建动作 ——
    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActionEditorDialog("新建动作", null) { Owner = this };
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        ConfigStore.AddAction(dlg.Result);
        ConfigStore.Save();
        Result = dlg.Result.Id;
        BuildCells(null);
        Highlight(_cells.FirstOrDefault(c => c.Id == Result));
    }

    // —— 确定 ——
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(Result))
        {
            ConfirmDialog.Info(this, "选择动作", "请先在列表中选择一个动作。");
            return;
        }
        DialogResult = true;
    }

    private sealed class ActionCell
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Glyph { get; set; } = "";
    }
}
