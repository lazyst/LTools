using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 面板里「把文件 / 文件夹变成动作」的入口：从资源管理器拖进来、<c>Ctrl+V</c>。
/// </summary>
/// <remarks>
/// <para>
/// 拖放走的是标准 OLE（<c>AllowDrop</c> + <c>DragOver</c> / <c>Drop</c>）：面板住在**普通权限**宿主里，
/// 资源管理器（中完整性）拖过来的东西 UIPI 不再拦。以前那层 <c>WM_DROPFILES</c> 桥（<c>FileDropBridge</c>）
/// 是替提权窗口兜底的，实测连消息都收不到，已随拆分一起删掉。
/// </para>
/// <para>
/// 两个入口共用 <see cref="PanelDrop"/>（建模 / 去重 / 落位 / 写盘只有一份）。落到的位置：
/// 拖放在光标下那个磁贴的前后（虚线占位和拖动排序用同一套几何），<c>Ctrl+V</c> 落到末尾。
/// **右键菜单不再有「从文件 / 文件夹创建动作」**：要建单个文件 / 文件夹动作走「新建简单动作」，
/// 要一次放一批就直接把文件拖进面板。
/// </para>
/// </remarks>
public partial class SuperPanelWindow
{
    // 拖动经过时插在网格里的虚线占位（和拖动排序长得一样），离开 / 松手就摘掉。
    private System.Windows.Shapes.Rectangle? _dropHole;
    private TileGridPanel? _dropHolePanel;

    // 「离开」的清除延后一拍：DragLeave 从磁贴冒上来时也会来一条，立刻清就是「一直闪」。
    private DispatcherTimer? _dropHoleClear;

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
        StopDropHoleClear();
        Point pos = e.GetPosition(this);
        (PanelGroupDto Group, int Index, TileGridPanel? Grid)? target = ResolveDropTarget(e, pos);
        if (target == null)
        {
            ClearDropHole();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        ShowDropHole(target.Value.Grid, target.Value.Index);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Panel_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave 是冒泡事件：光标从一块磁贴挪到另一块、或占位插到光标底下时都会先发一条
        // （WPF 每换一个命中的元素就发 Leave + Enter）。那不是「离开面板」——立刻清掉的话，
        // 每个 DragOver 前占位都被摘掉再加回来，看起来就是一直闪。
        // 延后 120ms：期间来了新的 DragOver 就撤销；真拖走 / 按 Esc 取消时不会再有，到点清掉。
        ScheduleDropHoleClear();
        e.Handled = true;
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        (PanelGroupDto Group, int Index, TileGridPanel? Grid)? target = ResolveDropTarget(e, e.GetPosition(this));
        ClearDropHole();
        e.Handled = true;

        if (target == null || !TryGetDroppedPaths(e.Data, out string[] paths)) return;

        AddPathsTo(target.Value.Group, paths, target.Value.Index);
        e.Effects = DragDropEffects.Copy;
    }

    /// <summary>
    /// 光标下这一摞文件应该落在哪：分组按**分组块的矩形**找，没落在分组块上就按它所在的区取第一个分组
    /// （区头标签 / 左栏 / 窗口空白都算「拖进面板」）；下标按它所在磁贴网格的几何算
    /// （<see cref="TileLayout.InsertIndex"/>，与拖动排序同一个函数）。搜索时不给拖。
    /// </summary>
    /// <remarks>
    /// **别用 <c>InputHitTest</c> 判区 / 判网格**：OLE 拖放期间鼠标捕获在拖动源那边，WPF 的
    /// <c>IsMouseOver</c> 不更新；更要紧的是虚线占位自己是 <c>IsHitTestVisible=false</c>
    /// （见 <see cref="DragFx.CreateHole"/>），它插在光标那一格时命中会穿过去落到块容器上，
    /// 「在哪个网格」就判不出来了 —— 占位于是每个 DragOver 被清掉再加回来（一直在闪）。
    /// 一律按容器自己的矩形算。
    /// </remarks>
    private (PanelGroupDto Group, int Index, TileGridPanel? Grid)? ResolveDropTarget(DragEventArgs e, Point pos)
    {
        if (!TryGetDroppedPaths(e.Data, out _)) return null;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) return null;

        // 光标落在哪个分组块上：块是上下平铺的，不会重叠，按矩形命中即可。
        foreach (StackPanel zone in new[] { GlobalGroups, ContextGroups })
        {
            foreach (StackPanel block in zone.Children.OfType<StackPanel>())
            {
                if (block.Tag is not PanelGroupDto group || !ContainsPoint(block, pos)) continue;

                TileGridPanel? grid = block.Children.OfType<TileGridPanel>().FirstOrDefault();
                return (group, grid == null ? group.Actions.Count : InsertIndexAt(grid, pos), grid);
            }
        }

        // 没落在分组块上（区头 / 左栏 / 窗口空白）：按它所在的区取第一个分组，落到末尾（不显示占位）。
        bool global = ContainsPoint(GlobalScroll, pos) || ContainsPoint(GlobalSceneTabs, pos);
        PanelGroupDto? fallback = FirstGroupOf(global) ?? FirstGroupOf(global: true);
        return fallback == null ? null : (fallback, fallback.Actions.Count, null);
    }

    /// <summary>光标在这个磁贴网格里应该插到第几个（只算真磁贴，占位本身不算）。</summary>
    private int InsertIndexAt(TileGridPanel grid, Point windowPoint)
    {
        var rects = new List<Rect>(grid.Children.Count);
        foreach (Button tile in grid.Children.OfType<Button>().Where(b => b.Tag is PanelTileRef { ActionId: not null }))
        {
            Point local = tile.TranslatePoint(new Point(0, 0), grid);
            rects.Add(new Rect(local, tile.RenderSize));
        }

        return TileLayout.InsertIndex(rects, TranslatePoint(windowPoint, grid));
    }

    /// <summary>点在这块元素的矩形里（OLE 拖放期间唯一可靠的「在哪个区」判定）。</summary>
    private bool ContainsPoint(FrameworkElement element, Point windowPoint)
    {
        if (!element.IsVisible || element.ActualWidth <= 0) return false;
        try
        {
            Rect bounds = element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));
            return bounds.Contains(windowPoint);
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

    /// <summary>把虚线占位插到目标位置（已经在那就什么都不做）。</summary>
    private void ShowDropHole(TileGridPanel? grid, int index)
    {
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

    /// <summary>离开面板 / 松手 / 取消：延后一拍再清（同一拍里紧接着的 DragOver 会把它撤销）。</summary>
    private void ScheduleDropHoleClear()
    {
        if (_dropHoleClear == null)
        {
            _dropHoleClear = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            _dropHoleClear.Tick += (_, _) => ClearDropHole();
        }

        _dropHoleClear.Stop();
        _dropHoleClear.Start();
    }

    private void StopDropHoleClear() => _dropHoleClear?.Stop();

    private void ClearDropHole()
    {
        _dropHoleClear?.Stop();
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
