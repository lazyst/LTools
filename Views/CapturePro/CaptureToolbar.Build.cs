using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Views.Controls;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>CaptureToolbar 的「构建（工具格 / 分组弹层）」部分。</summary>
public partial class CaptureToolbar
{
    // ============ 构建 ============

    /// <summary>
    /// 图片编辑窗的「平移」工具（照上游 <c>MoveToolPresentation::Pan</c> 那一格）：
    /// 与工具格子同一个 <c>GroupName</c>，所以勾上它 = 其它工具自动取消；它排在最前面。
    /// </summary>
    private void BuildPanButton()
    {
        var button = new RadioButton
        {
            Style = (Style)FindResource("ToolBtn"),
            GroupName = "captureTool",
            ToolTip = "平移（拖动画面；中键在任何工具下都能平移）",
            Content = new ShapePath
            {
                Data = (Geometry)FindResource("IconMove"),
                Style = (Style)FindResource("IconPath"),
            },
        };
        button.Click += (_, _) =>
        {
            if (_panBtn?.IsChecked == true) PanRequested?.Invoke();
        };

        ToolRow.Children.Insert(0, button);
        _panBtn = button;
    }

    /// <summary>平移模式开 / 关（开 = 取消所有绘图工具的选中态、参数条收起）。</summary>
    internal void SetPan(bool active)
    {
        if (_panBtn == null) return;

        _syncing = true;
        if (active)
        {
            foreach (var cell in _cells) cell.Button.IsChecked = false;
            _panBtn.IsChecked = true;
        }
        else
        {
            _panBtn.IsChecked = false;
        }
        _syncing = false;

        if (active)
        {
            _styleOverride = null;
            _toolOverride = null;
            _stylePeers = null;
            ParamBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            ApplyParamVisibility(_tool);
        }
    }

    /// <summary>裁剪模式回显（勾选状态由宿主决定；null = 两个都不勾）。</summary>
    internal void SetCrop(CropAxis? axis)
    {
        if (_cropRowsBtn == null || _cropColumnsBtn == null) return;

        _syncing = true;
        _cropRowsBtn.IsChecked = axis == CropAxis.Rows;
        _cropColumnsBtn.IsChecked = axis == CropAxis.Columns;
        _syncing = false;
    }

    /// <summary>按布局建格子（插在 <c>ToolRow</c> 最前面；动作按钮与分隔条在后面）。</summary>
    private void BuildCells(ToolLayout layout)
    {
        _layout = layout;
        int index = _panBtn != null ? 1 : 0;      // 平移格固定在 0 号位

        bool canvasMode = _mode == ToolbarMode.GlobalCanvas;
        int visibleGroups = layout.Groups.Count(g =>
            !canvasMode || g.Any(ToolCatalog.SupportsGlobalCanvas));

        foreach (var group in layout.Groups)
        {
            // 全屏画布：只用不依赖底图的工具；整格都被筛掉就跳过（不留空格）
            var tools = canvasMode
                ? group.Where(ToolCatalog.SupportsGlobalCanvas).ToList()
                : group.ToList();
            if (tools.Count == 0) continue;

            var icon = new ShapePath { Style = (Style)FindResource("IconPath") };

            // 同格不止一个工具：右下角一个灰点（别用数字——小图标里认不出是几）
            var badge = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(0, 0, 3, 3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
                Visibility = tools.Count > 1 ? Visibility.Visible : Visibility.Collapsed,
                IsHitTestVisible = false,
            };

            var grid = new Grid();
            grid.Children.Add(icon);
            grid.Children.Add(badge);

            var button = new RadioButton
            {
                Style = (Style)FindResource("ToolBtn"),
                GroupName = "captureTool",
                Content = grid,
            };

            var cell = new ToolCell(tools, button, icon);
            button.Tag = cell;
            button.Click += (_, _) => OnCellClick(cell);

            // 右键在按下时装菜单（与贴图同一个坑：ContextMenuOpening 里现赋第一次弹不出来）
            // 全屏画布**不提供布局编辑**（上游那条工具条也不给），右键直接吃掉。
            if (canvasMode)
            {
                button.PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
            }
            else
            {
                button.PreviewMouseRightButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    OpenCellMenu(cell, button);
                };
            }

            _cells.Add(cell);
            ToolRow.Children.Insert(index++, button);
            ShowCellIcon(cell);

            // 多工具格：悬停弹出二级工具条（照上游 ensureDrawingToolGroupPopover）
            if (tools.Count > 1) AttachGroupPopover(cell, button);

            // 「选择」后面一条竖线（照 Snow Shot 的 select-separator）：只建一次，重建时搬位置
            if (_cells.Count == 1 && visibleGroups > 1)
            {
                _selectSeparator ??= new Border { Style = (Style)FindResource("SeparatorBar") };
                if (_selectSeparator.Parent is Panel old) old.Children.Remove(_selectSeparator);
                ToolRow.Children.Insert(index++, _selectSeparator);
            }
        }
    }

    /// <summary>格子的图标与提示跟着「这一格现在显示哪个工具」走。</summary>
    private void ShowCellIcon(ToolCell cell)
    {
        var current = EntryOf(cell);
        var info = ToolCatalog.Of(current);
        cell.Icon.Data = (Geometry)FindResource("Icon" + info.Icon);
        cell.Button.ToolTip = cell.Tools.Count <= 1
            ? $"{info.Name}（{info.Shortcut}）\n{info.Hint}"
            : $"{info.Name}（{info.Shortcut}）· 这一格还有 " + string.Join(" / ",
                cell.Tools.Where(t => t != current)
                    .Select(t => ToolCatalog.Of(t).Name + "（" + ToolCatalog.Of(t).Shortcut + "）"))
              + "，悬停这一格选其它工具\n" + info.Hint;
    }

    /// <summary>
    /// 这一格「显示 / 首点」用哪个工具：照上游 <c>toolbarGroupEntryItemId</c>，
    /// 「组首按钮常显」打开时固定显示这一格默认露的那个（<see cref="ToolLayout.EntryOf"/>，即 position 最后一项），
    /// 否则用这一格上次点过的那个。
    /// </summary>
    private static CaptureTool EntryOf(ToolCell cell) =>
        GroupEntry(cell.Tools, cell.Current, DrawingSettings.AlwaysShowFirstGroup);

    /// <summary><see cref="EntryOf"/> 的纯函数版本（自检直接断言）。</summary>
    internal static CaptureTool GroupEntry(IReadOnlyList<CaptureTool> tools, CaptureTool current,
        bool alwaysShowFirst) =>
        tools.Count > 1 && alwaysShowFirst ? tools[^1] : current;

    /// <summary>
    /// 点格子：切到这一格现在显示的工具（**不再循环**）。
    /// </summary>
    /// <remarks>
    /// 上游的组是「一个按钮 + 悬停弹出二级工具条」（<c>ensureDrawingToolGroupPopover</c>）：
    /// 点按钮就是用它，变体靠悬停那排图标选。以前这里是「再点一次切下一个」，
    /// 用户点一下会莫名其妙换成别的工具（报过）。
    /// </remarks>
    private void OnCellClick(ToolCell cell) => ToolChanged?.Invoke(cell.Current);

    /// <summary>
    /// 多工具格子：悬停弹出「二级工具条」（照上游 <c>ensureDrawingToolGroupPopover</c>）——
    /// 一行图标，点哪个就切到哪个，点完收起。
    /// </summary>
    private void AttachGroupPopover(ToolCell cell, FrameworkElement target)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Opacity = 0.22,
                Color = Colors.Black,
            },
            Child = row,
        };

        var popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Custom,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = border,
            IsOpen = false,
        };
        CenterOn(popup, preferAbove: false);

        // 悬停离开按钮 / 弹层都延迟收起：鼠标从按钮挪到弹层要跨一条缝，不能立刻关
        var close = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220),
        };
        close.Tick += (_, _) =>
        {
            close.Stop();
            popup.IsOpen = false;
        };

        void Open()
        {
            close.Stop();
            RefreshGroupItems();
            popup.IsOpen = true;
        }

        void CloseSoon()
        {
            close.Stop();
            close.Start();
        }

        void RefreshGroupItems()
        {
            row.Children.Clear();

            // 照上游 popoverItemIds：position 反过来排，入口（格子当前显示的）在最左。
            foreach (var tool in cell.Tools.AsEnumerable().Reverse())
            {
                var info = ToolCatalog.Of(tool);
                var item = new RadioButton
                {
                    Style = (Style)FindResource("ToolBtn"),
                    GroupName = "captureToolPopover",
                    Content = new ShapePath
                    {
                        Style = (Style)FindResource("IconPath"),
                        Data = (Geometry)FindResource("Icon" + info.Icon),
                    },
                    ToolTip = $"{info.Name}（{info.Shortcut}）\n{info.Hint}",
                    IsChecked = cell.Current == tool,
                };

                item.Click += (_, _) =>
                {
                    cell.Current = tool;
                    ShowCellIcon(cell);
                    popup.IsOpen = false;
                    ToolChanged?.Invoke(tool);
                };

                row.Children.Add(item);
            }
        }

        target.MouseEnter += (_, _) => Open();
        target.MouseLeave += (_, _) => CloseSoon();
        border.MouseEnter += (_, _) => close.Stop();
        border.MouseLeave += (_, _) => CloseSoon();

        _groupPopups[cell] = popup;
    }

    private readonly Dictionary<ToolCell, Popup> _groupPopups = new();

    private ToolCell? CellOf(CaptureTool tool) => _cells.FirstOrDefault(c => c.Tools.Contains(tool));

}
