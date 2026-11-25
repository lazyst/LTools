using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 面板里「把文件 / 文件夹变成动作」的入口：从资源管理器拖进来、右键菜单两项、<c>Ctrl+V</c>。
/// </summary>
/// <remarks>
/// <para>
/// 拖放走的是标准 OLE（<c>AllowDrop</c> + <c>DragOver</c> / <c>Drop</c>）：面板住在**普通权限**宿主里，
/// 资源管理器（中完整性）拖过来的东西 UIPI 不再拦。以前那层 <c>WM_DROPFILES</c> 桥（<c>FileDropBridge</c>）
/// 是替提权窗口兜底的，实测连消息都收不到，已随拆分一起删掉。
/// </para>
/// <para>
/// 四个入口共用 <see cref="PanelDrop"/>（建模 / 去重 / 落位 / 写盘只有一份）。落到的位置：
/// 拖放在光标下那个磁贴的前后（虚线占位和拖动排序用同一套几何），其余入口落到分组末尾。
/// </para>
/// </remarks>
public partial class SuperPanelWindow
{
    // 拖动经过时插在网格里的虚线占位（和拖动排序长得一样），离开 / 松手就摘掉。
    private System.Windows.Shapes.Rectangle? _dropHole;
    private TileGridPanel? _dropHolePanel;

    /// <summary>从文件创建动作（可多选），落到 <paramref name="group"/> 末尾。</summary>
    private void AddFilesFor(PanelGroupDto group)
    {
        var dialog = new OpenFileDialog
        {
            Title = "从文件创建动作",
            Multiselect = true,
            CheckFileExists = true,
            Filter = "所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;
        AddPathsTo(group, dialog.FileNames);
    }

    /// <summary>从文件夹创建动作，落到 <paramref name="group"/> 末尾。</summary>
    private void AddFolderFor(PanelGroupDto group)
    {
        var dialog = new OpenFolderDialog { Title = "从文件夹创建动作" };

        if (dialog.ShowDialog(this) != true) return;
        AddPathsTo(group, new[] { dialog.FolderName });
    }

    /// <summary>
    /// <c>Ctrl+V</c>：剪贴板里的文件 / 文件夹 → 动作，落到光标下的分组（不在分组里就落到第一个）。
    /// </summary>
    /// <returns>剪贴板里没有文件时返回 false，让按键照常下传。</returns>
    private bool PasteFilesFromClipboard()
    {
        if (!NativeClipboard.TryGetFileList(out List<string> files) || files.Count == 0) return false;

        PanelGroupDto? group = GroupAt(Mouse.GetPosition(this)) ?? ZoneGroups(global: true).FirstOrDefault();
        if (group == null)
        {
            ToastService.Show("先新建一个分组，再往里放动作", "面板", 2600, ToastKind.warning,
                key: "panel:dropfiles");
            return true;
        }

        AddPathsTo(group, files);
        return true;
    }

    // ============ 从资源管理器拖进来 ============

    private void Panel_DragOver(object sender, DragEventArgs e)
    {
        Point pos = e.GetPosition(this);
        (PanelGroupDto Group, int Index)? target = ResolveDropTarget(e, pos);
        if (target == null)
        {
            ClearDropHole();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowDropHole(target.Value.Group, target.Value.Index, pos);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Panel_DragLeave(object sender, DragEventArgs e)
    {
        ClearDropHole();
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        (PanelGroupDto Group, int Index)? target = ResolveDropTarget(e, e.GetPosition(this));
        ClearDropHole();
        e.Handled = true;

        if (target == null || !TryGetDroppedPaths(e.Data, out string[] paths)) return;

        AddPathsTo(target.Value.Group, paths, target.Value.Index);
        e.Effects = DragDropEffects.Copy;
    }

    /// <summary>
    /// 光标下这一摞文件应该落在哪：分组按光标下那个分组块，没落在分组上就按它所在的区取第一个分组
    /// （区头标签 / 左栏 / 窗口空白都算「拖进面板」）；下标按它所在磁贴网格的几何算
    /// （<see cref="TileLayout.InsertIndex"/>，与拖动排序同一个函数）。搜索时不给拖。
    /// </summary>
    /// <remarks>
    /// **别用 <c>IsMouseOver</c> 判区**：OLE 拖放期间鼠标捕获在拖动源那边，WPF 的 IsMouseOver 不更新，
    /// 结果就是「拖到面板上也说这里不能放」。一律按区容器自己的矩形判。
    /// </remarks>
    private (PanelGroupDto Group, int Index)? ResolveDropTarget(DragEventArgs e, Point pos)
    {
        if (!TryGetDroppedPaths(e.Data, out _)) return null;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return null;

        // 光标下的分组：磁贴 / 分组标题 / 空网格都把 Tag 设成 PanelGroupDto，沿视觉树往上找。
        PanelGroupDto? group = GroupAt(pos);
        if (group == null)
        {
            bool global = ZoneContainsPoint(pos, GlobalScroll) || ZoneContainsPoint(pos, GlobalSceneTabs);
            group = FirstGroupOf(global) ?? FirstGroupOf(global: true);
            if (group == null) return null;
        }

        TileGridPanel? grid = GridAt(pos, group);
        if (grid == null) return (group, group.Actions.Count);

        var rects = new List<Rect>(grid.Children.Count);
        foreach (Button tile in grid.Children.OfType<Button>().Where(b => b.Tag is PanelTileRef { ActionId: not null }))
        {
            Point local = tile.TranslatePoint(new Point(0, 0), grid);
            rects.Add(new Rect(local, tile.RenderSize));
        }

        return (group, TileLayout.InsertIndex(rects, TranslatePoint(pos, grid)));
    }

    /// <summary>点在不在这块容器矩形里（OLE 拖放期间唯一可靠的「在哪个区」判定）。</summary>
    private bool ZoneContainsPoint(Point windowPoint, FrameworkElement zone)
    {
        if (!zone.IsVisible || zone.ActualWidth <= 0) return false;
        try
        {
            Point topLeft = zone.TransformToAncestor(this).Transform(new Point(0, 0));
            return new Rect(topLeft, zone.RenderSize).Contains(windowPoint);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>某个区的第一个分组（没有就 null；**不建**场景 / 分组）。</summary>
    private PanelGroupDto? FirstGroupOf(bool global) =>
        (global
            ? PanelLibrary.ActiveGlobalScene()?.Groups
            : GetContextScene()?.Groups ?? PanelLibrary.Current.Context.Common)
        ?.FirstOrDefault();

    /// <summary>光标下的磁贴网格（只在它属于 <paramref name="group"/> 时算）。</summary>
    private TileGridPanel? GridAt(Point windowPoint, PanelGroupDto group)
    {
        if (InputHitTest(windowPoint) is not DependencyObject d) return null;
        while (d != null)
        {
            if (d is TileGridPanel grid && ReferenceEquals(grid.Tag, group)) return grid;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>把虚线占位插到目标位置（已经在那就什么都不做）。</summary>
    private void ShowDropHole(PanelGroupDto group, int index, Point windowPoint)
    {
        TileGridPanel? grid = GridAt(windowPoint, group);
        if (grid == null) { ClearDropHole(); return; }

        var hole = _dropHole ?? DragFx.CreateHole();
        double w = 72, h = PanelLibrary.Current.Preferences.CompactTiles ? 56 : 72;
        Button? sample = grid.Children.OfType<Button>().FirstOrDefault(b => b.Tag is PanelTileRef { ActionId: not null });
        if (sample is { ActualWidth: > 0 }) { w = sample.ActualWidth; h = sample.ActualHeight; }
        hole.Width = w;
        hole.Height = h;

        int at = Math.Clamp(index, 0, grid.Children.Count);
        if (ReferenceEquals(grid, _dropHolePanel) && grid.Children.IndexOf(hole) == at) return;

        if (_dropHolePanel != null) _dropHolePanel.Children.Remove(hole);
        grid.Children.Insert(at, hole);
        _dropHole = hole;
        _dropHolePanel = grid;
    }

    private void ClearDropHole()
    {
        if (_dropHolePanel != null && _dropHole != null) _dropHolePanel.Children.Remove(_dropHole);
        _dropHole = null;
        _dropHolePanel = null;
    }

    /// <summary>拖进来的是不是「一串路径」；不是（文字 / 图片 / 别的程序的私有格式）就当拖放下移。</summary>
    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (data.GetData(DataFormats.FileDrop) is not string[] list || list.Length == 0) return false;

        paths = list;
        return true;
    }

    /// <summary>
    /// 建动作 → 落进分组（<paramref name="index"/> 为空 = 末尾）→ 重建面板 → 一条 toast
    /// （创建 / 已有 / 找不到各说一句）。
    /// </summary>
    private void AddPathsTo(PanelGroupDto group, IReadOnlyList<string> paths, int? index = null)
    {
        var known = PanelDrop.ExistingPaths();
        PanelDrop.Result result = PanelDrop.Build(paths, known.Contains);
        int added = PanelDrop.Insert(result.Actions, group, index ?? group.Actions.Count);

        if (added > 0) RebuildAll();

        var parts = new List<string>();
        if (added > 0) parts.Add($"已添加 {added} 个动作到「{group.Name}」");
        if (result.Duplicates.Count > 0) parts.Add($"{result.Duplicates.Count} 个已经在面板里");
        if (result.Missing.Count > 0) parts.Add($"{result.Missing.Count} 个找不到");

        string text = parts.Count > 0 ? string.Join(" · ", parts) : "没有可添加的文件";
        ToastService.Show(text, "面板", added > 0 ? 2600 : 3200,
            added > 0 ? ToastKind.success : ToastKind.warning, key: "panel:dropfiles");
    }
}
