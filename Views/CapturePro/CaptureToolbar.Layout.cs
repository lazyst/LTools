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

/// <summary>CaptureToolbar 的「布局编辑（堆叠 / 收起）与拖动复位」部分。</summary>
public partial class CaptureToolbar
{
    // ============ 布局编辑（堆叠 / 收起） ============

    /// <summary>改布局：落盘 → 重建格子 → 当前工具被收起来时退回「选择」（别拿着一个看不见的工具）。</summary>
    private void ChangeLayout(Func<ToolLayout, ToolLayout> change)
    {
        var next = change(_layout);
        if (next.SameAs(_layout)) return;

        ToolLayoutStore.Save(next);
        ApplyLayout(next);

        if (next.IsHidden(CurrentTool)) ToolChanged?.Invoke(CaptureTool.Select);
    }

    /// <summary>格子右键：收起这个工具 / 拆成单独一格 / 并到左边一格 / 恢复默认。</summary>
    /// <remarks>
    /// 返回菜单是为了让冒烟能直接检查条目；<paramref name="open"/> = false 只建菜单不弹出
    /// （冒烟的环境里不该留下悬着的弹层）。
    /// </remarks>
    private ContextMenu OpenCellMenu(ToolCell cell, FrameworkElement target, bool open = true)
    {
        var tool = cell.Current;
        var info = ToolCatalog.Of(tool);
        var menu = new ContextMenu { PlacementTarget = target };

        var hide = new MenuItem { Header = $"收起「{info.Name}」" };
        hide.Click += (_, _) => ChangeLayout(l => l.Hide(tool));
        menu.Items.Add(hide);

        if (cell.Tools.Count > 1)
        {
            var split = new MenuItem { Header = $"把「{info.Name}」拆成单独一格" };
            split.Click += (_, _) => ChangeLayout(l => l.Split(tool));
            menu.Items.Add(split);
        }

        var merge = new MenuItem
        {
            Header = "并到左边一格",
            IsEnabled = _cells.IndexOf(cell) > 0,      // 第一格左边没东西
        };
        merge.Click += (_, _) => ChangeLayout(l => l.MergeLeft(tool));
        menu.Items.Add(merge);

        menu.Items.Add(new Separator());
        menu.Items.Add(ResetItem());

        if (open) menu.IsOpen = true;
        return menu;
    }

    /// <summary>工具栏空白处右键：把收起来的工具放回去 / 恢复默认。</summary>
    /// <remarks>
    /// 返回菜单是为了让冒烟能直接检查条目；<paramref name="open"/> = false 只建菜单不弹出
    /// （冒烟的环境里不该留下悬着的弹层）。
    /// </remarks>
    private ContextMenu OpenBackgroundMenu(FrameworkElement target, bool open = true)
    {
        var menu = new ContextMenu { PlacementTarget = target };

        var hidden = _layout.HiddenTools;
        if (hidden.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（没有收起来的工具）", IsEnabled = false });
        }
        else
        {
            var restore = MenuFx.Submenu(menu, $"收起来的工具（{hidden.Count}）");
            foreach (var tool in hidden)
            {
                var info = ToolCatalog.Of(tool);
                var item = new MenuItem { Header = $"{info.Name}（{info.Shortcut}）" };
                item.Click += (_, _) => ChangeLayout(l => l.Insert(tool));
                restore.Items.Add(item);
            }
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(ResetItem());

        if (open) menu.IsOpen = true;
        return menu;
    }

    private MenuItem ResetItem()
    {
        var reset = new MenuItem { Header = "恢复默认工具栏" };
        reset.Click += (_, _) => ChangeLayout(_ => ToolLayout.Default());
        return reset;
    }

    /// <summary>工具栏空白处右键 = 布局菜单（点在格子上时是格子自己的菜单）。</summary>
    private void Chrome_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode == ToolbarMode.GlobalCanvas) return;    // 全屏画布不提供布局编辑
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        OpenBackgroundMenu(this);
    }

    /// <summary>
    /// 绘制工具右边排的是撤销 / 重做 + **完成动作**（照 Snow Shot：工具、撤销、完成动作在**一条**里，
    /// 用竖线分组；以前完成动作另起一条，两条叠在一起看着像两排工具）。
    /// </summary>
    private void BuildActions()
    {
        ToolRow.Children.Add(new Border { Style = (Style)FindResource("SeparatorBar") });

        if (_mode == ToolbarMode.GlobalCanvas)
        {
            BuildCanvasActions();
            return;
        }

        if (_mode == ToolbarMode.Edit)
        {
            BuildEditActions();
            return;
        }

        _undoBtn = AddAction("IconUndo", "撤销（Ctrl+Z）", () => UndoRequested?.Invoke());
        _redoBtn = AddAction("IconRedo", "重做（Ctrl+Y）", () => RedoRequested?.Invoke());

        // 撤销 / 重做与完成动作之间那条线：它属于「完成动作」这一截，整组收起时一起收（
        // 贴图内标注还要能撤销画上去的那几笔）。
        var line = new Border { Style = (Style)FindResource("SeparatorBar") };
        _actionParts.Add(line);
        ToolRow.Children.Add(line);

        AddActionGroups(ToolbarMode.Capture, separatorBeforeFirst: false);
        if (_pickMode) BuildPickActions();   // 只有参数框选才建这一组（默认工具条一条也不多）
    }

    /// <summary>
    /// 图片编辑窗右半边（照上游 <c>editorOptions()</c>）：裁剪行 / 裁剪列 → 撤销 / 重做 →
    /// 识别 → 复制 / 贴到屏幕 → 打印 / 快速保存 / 另存为。没有取消（Esc 只退裁剪）、
    /// 没有长截图 / 录屏。
    /// </summary>
    /// <remarks>
    /// 上游编辑窗把 OCR / 翻译 / 表格 / 二维码 / LaTeX / Markdown / HTML / 文档各摆一格工具；
    /// 本项目这些结果都在同一个结果窗的页签里，所以只留一个「识别」入口（与截图覆盖层同口径）。
    /// </remarks>
    private void BuildEditActions()
    {
        _cropRowsBtn = AddToolToggle("IconCropRows", "裁剪行：拖一条横带剪掉（减少高度）",
            () => CropRequested?.Invoke(ToggledCrop(CropAxis.Rows)));
        _cropColumnsBtn = AddToolToggle("IconCropColumns", "裁剪列：拖一条竖带剪掉（减少宽度）",
            () => CropRequested?.Invoke(ToggledCrop(CropAxis.Columns)));

        ToolRow.Children.Add(new Border { Style = (Style)FindResource("SeparatorBar") });

        _undoBtn = AddAction("IconUndo", "撤销（Ctrl+Z）", () => UndoRequested?.Invoke());
        _redoBtn = AddAction("IconRedo", "重做（Ctrl+Y）", () => RedoRequested?.Invoke());

        AddActionGroups(ToolbarMode.Edit, separatorBeforeFirst: true);
    }


    // ============ 拖动 / 双击复位 ============

    private void Chrome_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsOnControl(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            UserMoved = false;
            if (ResetRequested != null) ResetRequested();                      // 宿主按选区重新摆
            else if (Parent is FrameworkElement parent) PlaceDefault(parent.ActualWidth, parent.ActualHeight);
            e.Handled = true;
            return;
        }

        if (Parent is not Canvas canvas) return;
        UserMoved = true;
        _dragging = true;
        _dragStart = e.GetPosition(canvas);
        _dragLeft = Canvas.GetLeft(this);
        _dragTop = Canvas.GetTop(this);
        if (double.IsNaN(_dragLeft)) _dragLeft = 0;
        if (double.IsNaN(_dragTop)) _dragTop = 0;

        // 捕获必须落在**接手势的那个元素**上（`Chrome` 那层 Border），不能是本 UserControl：
        // 捕获之后鼠标消息只发给「被捕获的元素 + 它的祖先」，Border 是它的**子**元素，
        // `Chrome_MouseMove` 一次都收不到——实测按住工具条拖动时它纹丝不动（像卡住），
        // 松手又一切正常。捕获 Border 之后消息照常冒泡到本类的方法。
        _dragCapture = sender as IInputElement ?? this;
        _dragCapture.CaptureMouse();
        e.Handled = true;
    }

    private void Chrome_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || Parent is not Canvas canvas) return;

        Point at = e.GetPosition(canvas);
        double left = Math.Clamp(_dragLeft + (at.X - _dragStart.X), 0, Math.Max(0, canvas.ActualWidth - ActualWidth));
        double top = Math.Clamp(_dragTop + (at.Y - _dragStart.Y), 0, Math.Max(0, canvas.ActualHeight - ActualHeight));
        Canvas.SetLeft(this, left);
        Canvas.SetTop(this, top);
        e.Handled = true;
    }

    private void Chrome_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragCapture?.ReleaseMouseCapture();
        _dragCapture = null;
        e.Handled = true;
    }

    /// <summary>点在按钮上时不要启动拖动（否则点工具会顺手把工具栏拖走）。</summary>
    private static bool IsOnControl(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase or Thumb or Slider) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
