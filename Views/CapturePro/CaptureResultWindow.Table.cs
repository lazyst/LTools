using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;
using Microsoft.Win32;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗的表格页：表格文档 / 撤销栈 / 命令 / 复制 / 导出。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshottableeditor</c> + <c>screenshottableeditingwindow</c> 的分工：
/// **文档与撤销栈在窗口这边**，画它的 <see cref="CaptureTableGrid"/> 只负责命中 / 选区 / 编辑入口，
/// 通过事件回报。上游把这两半放在 <c>ScreenshotTableEditingSession</c> 与 <c>ScreenshotTableEditor</c>，
/// 这里用「窗口 + 控件」代替。
/// </para>
/// <para>
/// 命令集与上游工具条一致：撤销 / 重做 / 合并 / 拆分 / 重置 / 打开表格（导出临时 xlsx 用默认程序打开），
/// 外加复制 HTML · TSV · 导出 xlsx（上游在导出对话框里，这里直接给按钮）。
/// 可用性一律按文档算（<c>canUndo</c> / <c>canRedo</c> / <c>canMerge</c> / <c>canSplit</c> / <c>canReset</c>），
/// 不给「看着能点但什么都不做」的按钮。
/// </para>
/// </remarks>
public partial class CaptureResultWindow
{
    private const int TableUndoLimit = 50;
    private const string TableTemporaryFolder = "ltools-table";

    private TableDocument _tableDocument = TableDocument.Empty;
    private TableDocument _tableBaseline = TableDocument.Empty;
    private readonly List<TableDocument> _tableBefore = new();
    private readonly List<TableDocument> _tableAfter = new();
    private bool _tableWired;

    /// <summary>表格文档（冒烟 / 自检读它做断言）。</summary>
    internal TableDocument TableDoc => _tableDocument;

    /// <summary>表格画布。</summary>
    internal CaptureTableGrid TableGrid => TableCanvas;

    /// <summary>撤销 / 重做栈深度（冒烟断言用）。</summary>
    internal int TableUndoDepth => _tableBefore.Count;

    internal int TableRedoDepth => _tableAfter.Count;

    // ============ 装载 ============

    private void WireTable()
    {
        if (_tableWired) return;
        _tableWired = true;

        TableCanvas.CellEditRequested += (row, column, text) =>
        {
            TableDocument next = _tableDocument.Clone();
            if (!next.SetCellText(row, column, text))
            {
                RefreshTableCommands();
                return;
            }

            PushTableUndo(_tableDocument);
            ApplyTableDocument(next);
        };

        TableCanvas.SelectionChanged += RefreshTableCommands;
        TableCanvas.CommandRequested += TableCommand;
        TableCanvas.ContextMenuRequested += ShowTableMenu;
    }

    /// <summary>把 OCR 的近似表格灌成可编辑文档（没有表格就清空，页面上给一句说明）。</summary>
    private void FillTable(TableGrid table)
    {
        WireTable();

        TableDocument document = TableDocument.FromGrid(table);
        _tableDocument = document;
        _tableBaseline = document.Clone();
        _tableBefore.Clear();
        _tableAfter.Clear();

        bool has = !document.IsEmpty;
        TableCanvas.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        TableEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;

        TableCanvas.Reload(document);
        TableScroll.UpdateLayout();
        TableCanvas.SetViewportWidth(TableScroll.ViewportWidth);
        RefreshTableCommands();
    }

    private void TableScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TableCanvas == null) return;
        TableCanvas.SetViewportWidth(TableScroll.ViewportWidth);
    }

    private void ApplyTableDocument(TableDocument document)
    {
        _tableDocument = document;
        TableCanvas.Reload(document);
        TableCanvas.SetViewportWidth(TableScroll.ViewportWidth);
        RefreshTableCommands();
        RefreshStatus();
    }

    private void PushTableUndo(TableDocument before)
    {
        _tableBefore.Add(before);
        if (_tableBefore.Count > TableUndoLimit) _tableBefore.RemoveAt(0);
        _tableAfter.Clear();
    }

    /// <summary>按钮可用性 + 底部说明（上游 <c>refreshCommandState</c> 的同一套判定）。</summary>
    private void RefreshTableCommands()
    {
        if (TableUndoBtn is not { } undo || TableMergeBtn is not { } merge) return;

        bool has = !_tableDocument.IsEmpty;
        bool editing = TableCanvas.IsEditing;
        TableRange range = has ? TableCanvas.Selection : TableRange.Empty;

        undo.IsEnabled = _tableBefore.Count > 0;
        TableRedoBtn.IsEnabled = _tableAfter.Count > 0;
        merge.IsEnabled = has && !editing && _tableDocument.CanMerge(range);
        TableSplitBtn.IsEnabled = has && !editing && _tableDocument.CanSplit(range);
        TableResetBtn.IsEnabled = has && !_tableDocument.ContentEquals(_tableBaseline);

        TableCopyHtmlBtn.IsEnabled = has;
        TableCopyTsvBtn.IsEnabled = has;
        TableExportBtn.IsEnabled = has;
        TableOpenBtn.IsEnabled = has;

        TableInfo.Text = has
            ? string.Format(CultureInfo.CurrentCulture, "{0} 行 × {1} 列 · {2}",
                _tableDocument.RowCount, _tableDocument.ColumnCount,
                range.IsValid
                    ? string.Format(CultureInfo.CurrentCulture, "选中 {0} 格（{1} × {2}）",
                        range.RowCount * range.ColumnCount, range.RowCount, range.ColumnCount)
                    : "未选中")
            : "";
    }

    // ============ 命令 ============

    private void TableCommand(string command)
    {
        switch (command)
        {
            case "undo":
                TableUndo();
                break;
            case "redo":
                TableRedo();
                break;
            case "copy":
                TableCopySelection();
                break;
            case "paste":
                TablePaste();
                break;
            case "clear":
                TableClearSelection();
                break;
            case "selectAll":
                TableCanvas.SelectAll();
                break;
        }
    }

    private void TableUndo()
    {
        if (_tableBefore.Count == 0) return;
        TableDocument previous = _tableBefore[^1];
        _tableBefore.RemoveAt(_tableBefore.Count - 1);
        _tableAfter.Add(_tableDocument);
        ApplyTableDocument(previous);
    }

    private void TableRedo()
    {
        if (_tableAfter.Count == 0) return;
        TableDocument next = _tableAfter[^1];
        _tableAfter.RemoveAt(_tableAfter.Count - 1);
        _tableBefore.Add(_tableDocument);
        ApplyTableDocument(next);
    }

    private void TableMergeSelection()
    {
        if (_tableDocument.IsEmpty) return;
        TableRange range = TableCanvas.Selection;
        TableDocument next = _tableDocument.Clone();
        if (!next.Merge(range)) return;

        PushTableUndo(_tableDocument);
        ApplyTableDocument(next);
        TableCanvas.SetSelection(next.ExpandedRange(range));
    }

    private void TableSplitSelection()
    {
        if (_tableDocument.IsEmpty) return;
        TableRange range = TableCanvas.Selection;
        TableDocument next = _tableDocument.Clone();
        if (!next.Split(range)) return;

        PushTableUndo(_tableDocument);
        ApplyTableDocument(next);
        TableCanvas.SetSelection(range);
    }

    private void TableReset()
    {
        if (_tableDocument.ContentEquals(_tableBaseline)) return;
        PushTableUndo(_tableDocument);
        ApplyTableDocument(_tableBaseline.Clone());
    }

    private void TableClearSelection()
    {
        if (_tableDocument.IsEmpty) return;
        TableDocument next = _tableDocument.Clone();
        if (!next.Clear(TableCanvas.Selection)) return;

        PushTableUndo(_tableDocument);
        ApplyTableDocument(next);
    }

    /// <summary>复制选区（没选区就是整张表）：HTML + 纯文本一起写，表格软件读 HTML、记事本读文本。</summary>
    private void TableCopySelection()
    {
        if (_tableDocument.IsEmpty) return;

        TableRange range = TableCanvas.Selection;
        (string html, string text) = _tableDocument.ToClipboard(range);
        if (string.IsNullOrEmpty(html))
        {
            ToastService.Show("没有可复制的表格内容", "识别结果", 1600);
            return;
        }

        if (NativeClipboard.SetHtmlAndText(html, text))
        {
            ToastService.Show(
                string.Format(CultureInfo.CurrentCulture, "已复制表格 {0} × {1}", range.RowCount,
                    range.ColumnCount), "识别结果", 1600);
        }
        else
        {
            ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
        }
    }

    private void TablePaste()
    {
        if (_tableDocument.IsEmpty) return;
        (int row, int column) = TableCanvas.Current;
        if (row < 0) return;

        TableDocument? source = ReadClipboardTable();
        if (source == null || source.IsEmpty)
        {
            ToastService.Show("剪贴板里没有表格", "识别结果", 2400, ToastKind.warning);
            return;
        }

        TableDocument next = _tableDocument.Clone();
        TablePasteResult result = next.Paste(source, row, column);
        switch (result)
        {
            case TablePasteResult.DoesNotFit:
                ToastService.Show("粘贴的格子放不进识别出的表格", "识别结果", 2400, ToastKind.warning);
                return;
            case TablePasteResult.CutsMergedCell:
                ToastService.Show("先把这块合并格整个选中，再粘贴", "识别结果", 2400, ToastKind.warning);
                return;
            case TablePasteResult.EmptySource:
                ToastService.Show("剪贴板里没有表格", "识别结果", 2400, ToastKind.warning);
                return;
        }

        PushTableUndo(_tableDocument);
        ApplyTableDocument(next);
        TableCanvas.SetSelection(next.ExpandedRange(new TableRange(row, column,
            row + source.RowCount - 1, column + source.ColumnCount - 1)));
    }

    /// <summary>剪贴板里的表格：优先 HTML（带合并 / 表头），没有就按 TSV 文本拆。</summary>
    private static TableDocument? ReadClipboardTable()
    {
        if (NativeClipboard.TryGetHtml(out string html) && html.Length > 0)
        {
            TableDocument document = TableDocument.FromHtml(html, requireTable: true);
            if (!document.IsEmpty) return document;
        }

        if (NativeClipboard.TryGetText(out string text) && text.Trim().Length > 0)
        {
            TableDocument document = TableDocument.FromTsv(text);
            if (!document.IsEmpty) return document;
        }

        return null;
    }

    // ============ 导出 / 打开 ============

    private byte[]? BuildTableWorkbook() =>
        XlsxWriter.Build(_tableDocument, TableCanvas.ColumnWidths);

    private void TableExport()
    {
        if (_tableDocument.IsEmpty) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出表格",
            FileName = "识别表格.xlsx",
            DefaultExt = ".xlsx",
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        byte[]? bytes = BuildTableWorkbook();
        if (bytes == null)
        {
            ToastService.Show("表格内容为空或超出 Excel 上限", "识别结果", 0, ToastKind.error);
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception ex)
        {
            ToastService.Show("导出失败：" + ex.Message, "识别结果", 0, ToastKind.error);
            return;
        }

        string path = dialog.FileName;
        ToastService.Show($"已导出 {Path.GetFileName(path)}", "识别结果", 6000, ToastKind.success,
            onClick: () => OpenTableFile(path), actionText: "打开文件");
    }

    /// <summary>上游 <c>openSpreadsheetFile</c>：导出一份临时 xlsx，用系统默认程序打开。</summary>
    private void TableOpen()
    {
        if (_tableDocument.IsEmpty) return;

        byte[]? bytes = BuildTableWorkbook();
        if (bytes == null)
        {
            ToastService.Show("表格内容为空或超出 Excel 上限", "识别结果", 0, ToastKind.error);
            return;
        }

        string path;
        try
        {
            string folder = Path.Combine(Path.GetTempPath(), TableTemporaryFolder);
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "识别表格-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".xlsx");
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            ToastService.Show("打开失败：" + ex.Message, "识别结果", 0, ToastKind.error);
            return;
        }

        if (!OpenTableFile(path))
            ToastService.Show("没有可以打开 xlsx 的程序", "识别结果", 2400, ToastKind.warning);
    }

    private static bool OpenTableFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.TableOpen", ex);
            return false;
        }
    }

    // ============ 键盘 / 右键菜单 ============

    /// <summary>表格页的按键（Ctrl+C/V/Z/Y/A、Delete）。返回 true = 已处理。</summary>
    private bool HandleTableKey(KeyEventArgs e)
    {
        if (TableCanvas.IsEditing) return false;   // 单元格里的文本框自己收键

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.C:
                    TableCopySelection();
                    return true;
                case Key.V:
                    TablePaste();
                    return true;
                case Key.Z:
                    if (shift) TableRedo();
                    else TableUndo();
                    return true;
                case Key.Y:
                    TableRedo();
                    return true;
                case Key.A:
                    TableCanvas.SelectAll();
                    return true;
            }

            return false;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            TableClearSelection();
            return true;
        }

        return false;   // 方向键 / Enter / F2 交给画布自己处理
    }

    private void ShowTableMenu(Point screenPoint)
    {
        if (_tableDocument.IsEmpty) return;

        var menu = new ContextMenu
        {
            PlacementTarget = TableCanvas,
            Placement = PlacementMode.Relative,
        };

        Point local = TableCanvas.PointFromScreen(screenPoint);
        menu.HorizontalOffset = local.X;
        menu.VerticalOffset = local.Y;

        TableRange range = TableCanvas.Selection;
        AddTableMenuItem(menu, "复制", "Ctrl+C", "copy", true);
        AddTableMenuItem(menu, "粘贴", "Ctrl+V", "paste", true);
        AddTableMenuItem(menu, "清空内容", "Delete", "clear",
            _tableDocument.CanMerge(range) || range.IsValid);
        menu.Items.Add(new Separator());
        AddTableMenuItem(menu, "合并单元格", null, "merge",
            _tableDocument.CanMerge(range), TableMergeSelection);
        AddTableMenuItem(menu, "拆分单元格", null, "split",
            _tableDocument.CanSplit(range), TableSplitSelection);
        menu.Items.Add(new Separator());
        AddTableMenuItem(menu, "全选", "Ctrl+A", "selectAll", true);
        AddTableMenuItem(menu, "重置表格", null, "reset",
            !_tableDocument.ContentEquals(_tableBaseline), TableReset);

        TableCanvas.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void AddTableMenuItem(ContextMenu menu, string header, string? gesture, string command,
        bool enabled, Action? action = null)
    {
        var item = new MenuItem
        {
            Header = gesture == null ? header : header + "\t" + gesture,
            IsEnabled = enabled,
        };

        item.Click += (_, _) =>
        {
            if (action != null) action();
            else TableCommand(command);
        };

        menu.Items.Add(item);
    }

    // ============ 按钮 ============

    private void TableUndo_Click(object sender, RoutedEventArgs e) => TableUndo();

    private void TableRedo_Click(object sender, RoutedEventArgs e) => TableRedo();

    private void TableMerge_Click(object sender, RoutedEventArgs e) => TableMergeSelection();

    private void TableSplit_Click(object sender, RoutedEventArgs e) => TableSplitSelection();

    private void TableReset_Click(object sender, RoutedEventArgs e) => TableReset();

    private void TableCopyHtml_Click(object sender, RoutedEventArgs e)
    {
        if (_tableDocument.IsEmpty) return;
        if (NativeClipboard.SetText(_tableDocument.ToHtml()))
            ToastService.Show("已复制表格 HTML", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    private void TableCopyTsv_Click(object sender, RoutedEventArgs e)
    {
        if (_tableDocument.IsEmpty) return;
        if (NativeClipboard.SetText(_tableDocument.ToTsv()))
            ToastService.Show("已复制表格 TSV", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    private void TableExport_Click(object sender, RoutedEventArgs e) => TableExport();

    private void TableOpen_Click(object sender, RoutedEventArgs e) => TableOpen();
}
