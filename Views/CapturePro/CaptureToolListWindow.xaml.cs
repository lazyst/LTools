using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 工具勾选清单（设置「通用 → 截图 Pro · 绘制行为」里两个「编辑…」）。
/// </summary>
/// <remarks>
/// 照上游设置的两份清单：<c>drawing/automatic_selection_excluded_tools</c>
/// （画完不自动选中的工具）与 <c>drawing/quick_selection_disabled_tools</c>
/// （禁用快速选择的工具）。选项与顺序都照上游 <c>drawingSelectableToolOptions()</c>，
/// 由 <see cref="CaptureToolIds"/> 唯一维护；编辑的是本地草稿，点「保存」才写
/// <c>configs/settings.json</c>（见 <see cref="DrawingSettings"/>）。
/// </remarks>
public partial class CaptureToolListWindow : Window
{
    /// <summary>这份清单是哪一个（决定默认值与保存目标）。</summary>
    private readonly bool _quickSelection;

    private readonly List<(string Id, CheckBox Box)> _rows = new();

    private CaptureToolListWindow(bool quickSelection)
    {
        InitializeComponent();
        _quickSelection = quickSelection;

        Title = quickSelection ? "禁用快速选择的工具" : "不自动选中的工具";
        LeadText.Text = quickSelection
            ? "勾上的工具：左键点在已有的同类元素上时继续画新的，而不是选中并拖动它。"
            : "勾上的工具：画完之后不选中刚画好的元素（工具本来就不变）。";
        HintText.Text = "只影响新建的标注，不改已有内容。";

        var current = new HashSet<string>(
            quickSelection ? DrawingSettings.QuickSelectionDisabled : DrawingSettings.AutoSelectExcluded,
            StringComparer.Ordinal);

        foreach (var (id, tool) in CaptureToolIds.Selectable)
        {
            var box = new CheckBox
            {
                Content = ToolCatalog.Of(tool).Name,
                IsChecked = current.Contains(id),
                Margin = new Thickness(0, 0, 12, 10),
                Width = 186,
                Tag = id,
            };
            _rows.Add((id, box));
            ToolList.Items.Add(box);
        }
    }

    /// <summary>打开清单（<paramref name="quickSelection"/> 决定编辑哪一份）。</summary>
    internal static void Show(Window? owner, bool quickSelection)
    {
        var window = new CaptureToolListWindow(quickSelection)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? true,
        };

        window.ShowDialog();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new HashSet<string>(
            _quickSelection ? DrawingSettings.DefaultQuickSelectionDisabled : DrawingSettings.DefaultAutoSelectExcluded,
            StringComparer.Ordinal);

        foreach (var (id, box) in _rows) box.IsChecked = defaults.Contains(id);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var ids = _rows.Where(row => row.Box.IsChecked == true).Select(row => row.Id).ToList();

        if (_quickSelection) DrawingSettings.SetQuickSelectionDisabled(ids);
        else DrawingSettings.SetAutoSelectExcluded(ids);

        DialogResult = true;
    }
}
