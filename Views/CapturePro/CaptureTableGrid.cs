using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using TableCell = LTools.Features.CapturePro.TableCell;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果里的可编辑表格（照 Snow Shot 的 <c>screenshottableeditor</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 不是一个只读 <see cref="DataGrid"/>：需要**合并格**（跨行 / 跨列）、表头加粗、拖选矩形、
/// 双击 / 单击进编辑、右键菜单、键盘导航，DataGrid 的单元格模型承载不了这些（QTableView 的
/// <c>setSpan</c> 没有对应物）。所以这里自绘：列宽 / 行高按文字量出来，选区与命中有纯函数可断言。
/// </para>
/// <para>
/// 表格数据由宿主（结果窗）持有，本控件只读它、通过事件把「编辑了哪一格 / 要执行哪个命令」抛回去；
/// 撤销栈、文档替换、导出都在宿主，控件不自己改文档。
/// </para>
/// </remarks>
internal sealed class CaptureTableGrid : Panel
{
    private const double Padding = 8;
    private const double LineWidth = 1;
    private const double MinimumColumnWidth = 88;
    private const double MaximumColumnWidth = 360;
    private const double BaseRowHeight = 34;
    private const double DragThreshold = 3;

    private readonly TextBox _editor;
    private TableDocument _document = TableDocument.Empty;
    private double[] _columnWidths = Array.Empty<double>();
    private double[] _rowHeights = Array.Empty<double>();
    private double _viewportWidth;

    private TableRange _selection = TableRange.Empty;
    private (int Row, int Column) _current = (-1, -1);
    private (int Row, int Column) _hover = (-1, -1);
    private (int Row, int Column) _pressCell = (-1, -1);
    private ModifierKeys _pressModifiers = ModifierKeys.None;
    private bool _pressed;
    private bool _dragged;
    private bool _dragAdds;
    private Point _pressPoint;
    private TableRange _selectionBeforeDrag = TableRange.Empty;

    private double _editorRow;
    private double _editorColumn;
    private double _editorRowSpan = 1;
    private double _editorColumnSpan = 1;

    public CaptureTableGrid()
    {
        Background = Brushes.Transparent;   // 透明也要给，否则空白处收不到鼠标
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;

        _editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Padding),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Top,
            MinHeight = 0,
        };
        _editor.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        _editor.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
        _editor.PreviewKeyDown += Editor_PreviewKeyDown;
        _editor.LostKeyboardFocus += (_, _) => CommitEdit();
        _editor.ContextMenu = BuildEditorMenu(_editor);

        Children.Add(_editor);

        MouseLeftButtonDown += Grid_MouseLeftButtonDown;
        MouseMove += Grid_MouseMove;
        MouseLeftButtonUp += Grid_MouseLeftButtonUp;
        MouseLeave += (_, _) =>
        {
            if (_hover == (-1, -1)) return;
            _hover = (-1, -1);
            InvalidateVisual();
        };
    }

    // ============ 状态 ============

    public TableDocument Document => _document;

    public TableRange Selection => _selection;

    public (int Row, int Column) Current => _current;

    /// <summary>当前列宽（DIP，供 xlsx 导出用）。</summary>
    public IReadOnlyList<int> ColumnWidths
    {
        get
        {
            var widths = new int[_columnWidths.Length];
            for (int index = 0; index < _columnWidths.Length; index++)
                widths[index] = (int)Math.Round(_columnWidths[index]);
            return widths;
        }
    }

    public bool IsEditing => _editor.Visibility == Visibility.Visible;

    /// <summary>单元格文字被改过（宿主据此记撤销）。</summary>
    public event Action<int, int, string>? CellEditRequested;

    /// <summary>选区 / 当前格变了（宿主据此刷按钮可用性）。</summary>
    public event Action? SelectionChanged;

    /// <summary>要执行命令：copy / paste / clear / undo / redo / selectAll。</summary>
    public event Action<string>? CommandRequested;

    /// <summary>要弹右键菜单（屏幕坐标）。</summary>
    public event Action<Point>? ContextMenuRequested;

    public void Reload(TableDocument document)
    {
        CancelEdit();
        _document = document ?? TableDocument.Empty;
        if (_document.IsEmpty)
        {
            _selection = TableRange.Empty;
            _current = (-1, -1);
        }
        else
        {
            _selection = _document.BoundedRange(_selection);
            if (!_selection.IsValid) _selection = _document.ExpandedRange(TableRange.Single(0, 0));
            _current = _document.AnchorAt(_current.Row, _current.Column);
            if (_current.Row < 0) _current = (_selection.Top, _selection.Left);
        }

        InvalidateMeasure();
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void SetViewportWidth(double width)
    {
        if (width < 0 || double.IsNaN(width) || Math.Abs(width - _viewportWidth) < 0.5) return;
        _viewportWidth = width;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>选中一个格子（扩成它整块合并格）；<paramref name="extend"/> = Shift 连选。</summary>
    public void SelectCell(int row, int column, bool extend = false)
    {
        if (_document.IsEmpty || !_document.Contains(row, column)) return;
        TableRange span = _document.ExpandedRange(_document.SpanRangeAt(row, column));
        _current = _document.AnchorAt(row, column);
        SetSelection(extend ? TableRange.Union(_selection, span) : span);
    }

    public void SelectAll()
    {
        if (_document.IsEmpty) return;
        SetSelection(new TableRange(0, 0, _document.RowCount - 1, _document.ColumnCount - 1));
    }

    public void SetSelection(TableRange range)
    {
        TableRange expanded = _document.ExpandedRange(range);
        if (expanded == _selection) return;
        _selection = expanded;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    // ============ 编辑 ============

    /// <summary>进入单元格编辑（<paramref name="replaceWith"/> 非空时用它替换全部文字，照 AnyKeyPressed 的语义）。</summary>
    public void BeginEdit(int row, int column, string? replaceWith = null)
    {
        if (_document.IsEmpty) return;
        (int anchorRow, int anchorColumn) = _document.AnchorAt(row, column);
        if (anchorRow < 0) return;

        TableCell? cell = _document.AnchorCellAt(anchorRow, anchorColumn);
        if (cell == null) return;

        _current = (anchorRow, anchorColumn);
        TableRange span = _document.SpanRangeAt(anchorRow, anchorColumn);
        SetSelection(span);

        _editorRow = anchorRow;
        _editorColumn = anchorColumn;
        _editorRowSpan = span.RowCount;
        _editorColumnSpan = span.ColumnCount;
        _editor.Text = replaceWith ?? cell.Text;
        _editor.Visibility = Visibility.Visible;
        InvalidateArrange();
        InvalidateVisual();

        // 面板刚变可见时布局还没跑，先强制排一次再抢焦点，否则 SelectAll 落在错的位置
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsEditing) return;
            _editor.Focus();
            _editor.SelectAll();
        });
    }

    public void CommitEdit()
    {
        if (!IsEditing) return;
        string text = _editor.Text;
        int row = (int)_editorRow;
        int column = (int)_editorColumn;
        CancelEdit();
        if (!_document.IsEmpty && _document.Contains(row, column))
        {
            _current = (row, column);
            SetSelection(_document.SpanRangeAt(row, column));
            CellEditRequested?.Invoke(row, column, text);
        }
    }

    public void CancelEdit()
    {
        if (!IsEditing) return;
        _editor.Visibility = Visibility.Collapsed;
        InvalidateArrange();
        InvalidateVisual();
        Focus();
    }

    /// <summary>
    /// 单元格编辑框的右键菜单（照上游 <c>showCellEditorContextMenu</c>）：复制 / 剪切 / 粘贴 / 删除 / 全选，
    /// 可用性在菜单打开时按当前选区与剪贴板重新算。
    /// </summary>
    private static ContextMenu BuildEditorMenu(TextBox editor)
    {
        var menu = new ContextMenu { PlacementTarget = editor };

        MenuItem Add(string header, string gesture, Action action)
        {
            var item = new MenuItem { Header = header + "\t" + gesture };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        MenuItem copy = Add("复制", "Ctrl+C", () => editor.Copy());
        MenuItem cut = Add("剪切", "Ctrl+X", () => editor.Cut());
        MenuItem paste = Add("粘贴", "Ctrl+V", () => editor.Paste());
        MenuItem delete = Add("删除", "Delete", () =>
        {
            if (editor.SelectionLength > 0) editor.SelectedText = "";
        });
        menu.Items.Add(new Separator());
        MenuItem selectAll = Add("全选", "Ctrl+A", () => editor.SelectAll());

        menu.Opened += (_, _) =>
        {
            bool hasSelection = editor.SelectionLength > 0;
            copy.IsEnabled = hasSelection;
            cut.IsEnabled = hasSelection;
            paste.IsEnabled = ClipboardHasText();
            delete.IsEnabled = hasSelection;
            selectAll.IsEnabled = editor.Text.Length > 0;
        };

        return menu;
    }

    private static bool ClipboardHasText()
    {
        try
        {
            return Clipboard.ContainsText();
        }
        catch (Exception)
        {
            // 剪贴板被别的程序占着时读不了：按「没有」处理，别让右键菜单开不出来
            return false;
        }
    }

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)    {
        switch (e.Key)
        {
            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                return;

            case Key.Enter:
                MoveFromEditor(e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1, 0);
                e.Handled = true;
                return;

            case Key.Tab:
                MoveFromEditor(0, e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                return;
        }
    }

    private void MoveFromEditor(int rowDelta, int columnDelta)
    {
        int row = (int)_editorRow;
        int column = (int)_editorColumn;
        TableRange span = _document.SpanRangeAt(row, column);
        CommitEdit();

        int targetRow = rowDelta > 0 ? span.Bottom + 1 : rowDelta < 0 ? span.Top - 1 : row;
        int targetColumn = columnDelta > 0 ? span.Right + 1 : columnDelta < 0 ? span.Left - 1 : column;

        if (columnDelta != 0)
        {
            // 走到行尾就折到下一行（上游 QTableView 的 Tab 行为）
            if (targetColumn >= _document.ColumnCount || targetColumn < 0)
            {
                targetRow += columnDelta > 0 ? 1 : -1;
                targetColumn = columnDelta > 0 ? 0 : _document.ColumnCount - 1;
            }
        }

        targetRow = Math.Clamp(targetRow, 0, _document.RowCount - 1);
        targetColumn = Math.Clamp(targetColumn, 0, _document.ColumnCount - 1);
        SelectCell(targetRow, targetColumn);
        Focus();
    }

    // ============ 鼠标 ============

    private void Grid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_document.IsEmpty) return;
        Focus();

        (int row, int column) = CellAt(e.GetPosition(this));
        if (row < 0) return;

        _pressed = true;
        _dragged = false;
        _pressPoint = e.GetPosition(this);
        _pressCell = (row, column);
        _selectionBeforeDrag = _selection;
        _pressModifiers = Keyboard.Modifiers;
        _dragAdds = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.ClickCount >= 2)
        {
            _pressed = false;
            BeginEdit(row, column);
            e.Handled = true;
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (shift && _current is { Row: >= 0 })
        {
            SetSelection(TableRange.Between(_current.Row, _current.Column, row, column));
        }
        else
        {
            _current = _document.AnchorAt(row, column);
            SetSelection(_document.SpanRangeAt(row, column));
        }

        CaptureMouse();
        e.Handled = true;
    }

    private void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_document.IsEmpty) return;
        Point point = e.GetPosition(this);

        if (_pressed)
        {
            if (!_dragged &&
                (Math.Abs(point.X - _pressPoint.X) > DragThreshold ||
                 Math.Abs(point.Y - _pressPoint.Y) > DragThreshold))
            {
                _dragged = true;
            }

            if (_dragged)
            {
                (int row, int column) = CellAt(point);
                if (row >= 0)
                {
                    TableRange drag = TableRange.Between(_pressCell.Row, _pressCell.Column, row, column);
                    SetSelection(_dragAdds ? TableRange.Union(_selectionBeforeDrag, drag) : drag);
                }
            }

            return;
        }

        (int hoverRow, int hoverColumn) = CellAt(point);
        (int, int) anchor = hoverRow >= 0 ? _document.AnchorAt(hoverRow, hoverColumn) : (-1, -1);
        if (anchor == _hover) return;
        _hover = anchor;
        InvalidateVisual();
    }

    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (_dragged) return;
        // 上游 mouseReleaseEvent 要求**没按任何修饰键**才进编辑（Shift / Ctrl 的那一下只是扩选 / 加选）
        if (_pressModifiers != ModifierKeys.None) return;
        (int row, int column) = CellAt(e.GetPosition(this));
        if (row < 0 || (row, column) != _pressCell) return;

        _current = _document.AnchorAt(row, column);
        SetSelection(_document.SpanRangeAt(row, column));

        // 上游 mouseReleaseEvent：没拖过就进编辑
        BeginEdit(row, column);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_document.IsEmpty) return;

        (int row, int column) = CellAt(e.GetPosition(this));
        if (row >= 0 && !_selection.Contains(row, column))
        {
            _current = _document.AnchorAt(row, column);
            SetSelection(_document.SpanRangeAt(row, column));
        }

        Focus();
        ContextMenuRequested?.Invoke(PointToScreen(e.GetPosition(this)));
        e.Handled = true;
    }

    // ============ 键盘 ============

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (IsEditing || _document.IsEmpty) return;

        bool ctrl = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.A:
                    SelectAll();
                    e.Handled = true;
                    return;
                case Key.C:
                    CommandRequested?.Invoke("copy");
                    e.Handled = true;
                    return;
                case Key.V:
                    CommandRequested?.Invoke("paste");
                    e.Handled = true;
                    return;
                case Key.Z:
                    CommandRequested?.Invoke(shift ? "redo" : "undo");
                    e.Handled = true;
                    return;
                case Key.Y:
                    CommandRequested?.Invoke("redo");
                    e.Handled = true;
                    return;
                case Key.Home:
                    SelectCell(0, 0);
                    EnsureVisible(0, 0);
                    e.Handled = true;
                    return;
                case Key.End:
                    SelectCell(_document.RowCount - 1, _document.ColumnCount - 1);
                    EnsureVisible(_document.RowCount - 1, _document.ColumnCount - 1);
                    e.Handled = true;
                    return;
            }

            return;
        }

        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                CommandRequested?.Invoke("clear");
                e.Handled = true;
                return;
            case Key.F2:
            case Key.Enter:
                BeginEdit(_current.Row, _current.Column);
                e.Handled = true;
                return;
            case Key.Left:
                Move(-1, 0, shift);
                e.Handled = true;
                return;
            case Key.Right:
                Move(1, 0, shift);
                e.Handled = true;
                return;
            case Key.Up:
                Move(0, -1, shift);
                e.Handled = true;
                return;
            case Key.Down:
                Move(0, 1, shift);
                e.Handled = true;
                return;
            case Key.Tab:
                Move(shift ? -1 : 1, 0, false, wrap: true);
                e.Handled = true;
                return;
            case Key.Home:
                Move(-_document.ColumnCount, 0, shift);
                e.Handled = true;
                return;
            case Key.End:
                Move(_document.ColumnCount, 0, shift);
                e.Handled = true;
                return;
            case Key.PageUp:
                Move(0, -PageRows(), shift);
                e.Handled = true;
                return;
            case Key.PageDown:
                Move(0, PageRows(), shift);
                e.Handled = true;
                return;
        }
    }

    /// <summary>PageUp / PageDown 一次翻几行：按视口高度和第一行高算（拿不到视口就按 10 行）。</summary>
    private int PageRows()
    {
        if (Parent is ScrollViewer viewer && viewer.ViewportHeight > 0 && _rowHeights.Length > 0)
        {
            double rowHeight = _rowHeights[0];
            if (rowHeight > 0) return Math.Max(1, (int)(viewer.ViewportHeight / rowHeight));
        }

        return 10;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (IsEditing || _document.IsEmpty) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        if (char.IsControl(e.Text[0])) return;
        if (_current.Row < 0) return;

        // 上游 EditTriggers::AnyKeyPressed：直接开编辑，敲的字替换整格
        BeginEdit(_current.Row, _current.Column, e.Text);
        e.Handled = true;
    }

    private void Move(int columnDelta, int rowDelta, bool extend, bool wrap = false)
    {
        if (_current.Row < 0) return;
        TableRange span = _document.SpanRangeAt(_current.Row, _current.Column);
        int row = rowDelta < 0 ? span.Top - 1 : rowDelta > 0 ? span.Bottom + 1 : span.Top;
        int column = columnDelta < 0 ? span.Left - 1 : columnDelta > 0 ? span.Right + 1 : span.Left;

        if (wrap && column >= _document.ColumnCount)
        {
            row++;
            column = 0;
        }
        else if (wrap && column < 0)
        {
            row--;
            column = _document.ColumnCount - 1;
        }

        row = Math.Clamp(row, 0, _document.RowCount - 1);
        column = Math.Clamp(column, 0, _document.ColumnCount - 1);

        if (extend)
        {
            SetSelection(TableRange.Between(_current.Row, _current.Column, row, column));
        }
        else
        {
            SelectCell(row, column);
            EnsureVisible(row, column);
        }
    }

    /// <summary>把某格滚进视口（键盘走到看不见的格子时）。</summary>
    private void EnsureVisible(int row, int column)
    {
        if (Parent is not ScrollViewer viewer) return;
        Rect rect = CellRect(row, column, 1, 1);
        viewer.ScrollToVerticalOffset(ClampOffset(viewer.VerticalOffset, viewer.ViewportHeight,
            rect.Top, rect.Bottom, viewer.ScrollableHeight));
        viewer.ScrollToHorizontalOffset(ClampOffset(viewer.HorizontalOffset, viewer.ViewportWidth,
            rect.Left, rect.Right, viewer.ScrollableWidth));
    }

    private static double ClampOffset(double current, double viewport, double start, double end,
        double max)
    {
        if (viewport <= 0) return current;
        if (start < current) return Math.Max(0, start);
        if (end > current + viewport) return Math.Min(max, end - viewport);
        return current;
    }

    // ============ 布局 ============

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureTable();

        double width = TotalWidth;
        double height = TotalHeight;

        if (IsEditing)
        {
            Rect rect = CellRect((int)_editorRow, (int)_editorColumn, (int)_editorRowSpan,
                (int)_editorColumnSpan);
            _editor.Measure(new Size(Math.Max(1, rect.Width), Math.Max(1, rect.Height)));
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (IsEditing)
        {
            int row = Math.Clamp((int)_editorRow, 0, Math.Max(0, _document.RowCount - 1));
            int column = Math.Clamp((int)_editorColumn, 0, Math.Max(0, _document.ColumnCount - 1));
            Rect rect = CellRect(row, column, (int)_editorRowSpan, (int)_editorColumnSpan);
            _editor.Arrange(rect);
        }

        return finalSize;
    }

    private double TotalWidth
    {
        get
        {
            double sum = 0;
            foreach (double width in _columnWidths) sum += width;
            return Math.Max(1, sum);
        }
    }

    private double TotalHeight
    {
        get
        {
            double sum = 0;
            foreach (double height in _rowHeights) sum += height;
            return Math.Max(1, sum);
        }
    }

    // ============ 度量 ============

    private Typeface Typeface =>
        new(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Typeface BoldTypeface =>
        new(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private double FontSize => TextElement.GetFontSize(this);

    private double PixelsPerDip
    {
        get
        {
            try
            {
                return VisualTreeHelper.GetDpi(this).PixelsPerDip;
            }
            catch (InvalidOperationException)
            {
                return 1;
            }
        }
    }

    private double LineHeight
    {
        get
        {
            var text = new FormattedText("Ag", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                Typeface, FontSize, Brushes.Black, PixelsPerDip);
            return text.Height;
        }
    }

    private double MeasureText(string text, bool bold)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double widest = 0;
        foreach (string line in text.Split('\n'))
        {
            var formatted = new FormattedText(line, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, bold ? BoldTypeface : Typeface, FontSize, Brushes.Black,
                PixelsPerDip);
            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        return widest;
    }

    private void MeasureTable()
    {
        int rows = _document.RowCount;
        int columns = _document.ColumnCount;
        if (rows == 0 || columns == 0)
        {
            _columnWidths = Array.Empty<double>();
            _rowHeights = Array.Empty<double>();
            return;
        }

        if (_columnWidths.Length != columns)
        {
            _columnWidths = new double[columns];
            Array.Fill(_columnWidths, MinimumColumnWidth);
        }

        if (_rowHeights.Length != rows) _rowHeights = new double[rows];

        // 列宽：先只看单格文字（合并格放到后面补差值）
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                TableCell? cell = _document.AnchorCellAt(row, column);
                if (cell == null || cell.ColumnSpan > 1) continue;
                double width = MeasureText(cell.Text, cell.Header) + Padding * 2 + LineWidth * 2;
                _columnWidths[column] = Math.Max(_columnWidths[column], width);
            }
        }

        for (int column = 0; column < columns; column++)
            _columnWidths[column] = Math.Min(MaximumColumnWidth, _columnWidths[column]);

        // 合并格：跨几列就按这几列合计补足
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                TableCell? cell = _document.AnchorCellAt(row, column);
                if (cell == null || cell.ColumnSpan <= 1) continue;
                int span = Math.Min(cell.ColumnSpan, columns - column);
                double available = 0;
                for (int offset = 0; offset < span; offset++) available += _columnWidths[column + offset];
                double needed = Math.Min(MaximumColumnWidth * span,
                    MeasureText(cell.Text, cell.Header) + Padding * 2 + LineWidth * 2);
                if (needed <= available) continue;

                double extra = (needed - available) / span;
                for (int offset = 0; offset < span; offset++)
                    _columnWidths[column + offset] = Math.Min(MaximumColumnWidth,
                        _columnWidths[column + offset] + extra);
            }
        }

        // 装得下就把多余宽度均摊（上游 updateTableMetrics 的同一做法）
        double total = 0;
        foreach (double width in _columnWidths) total += width;
        double availableWidth = Math.Max(_viewportWidth, total) - 2;
        if (total < availableWidth)
        {
            double remaining = availableWidth - total;
            for (int column = 0; column < columns && remaining > 0; column++)
            {
                double share = Math.Max(1, remaining / (columns - column));
                double growth = Math.Min(share, MaximumColumnWidth - _columnWidths[column]);
                _columnWidths[column] += growth;
                remaining -= growth;
            }
        }

        // 行高：最多算 3 行的换行高度
        double lineHeight = LineHeight;
        for (int row = 0; row < rows; row++)
        {
            int lines = 1;
            for (int column = 0; column < columns; column++)
            {
                TableCell? cell = _document.AnchorCellAt(row, column);
                if (cell == null) continue;
                int count = cell.Text.Split('\n').Length;
                lines = Math.Max(lines, Math.Min(3, count));
            }

            _rowHeights[row] = Math.Max(BaseRowHeight,
                lines * lineHeight + Padding * 2 + LineWidth * 2);
        }
    }

    /// <summary>第 row 行、第 column 列（跨 span 行 / 列）在控件里的矩形。</summary>
    public Rect CellRect(int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        double left = 0, top = 0, width = 0, height = 0;
        for (int i = 0; i < column && i < _columnWidths.Length; i++) left += _columnWidths[i];
        for (int i = 0; i < row && i < _rowHeights.Length; i++) top += _rowHeights[i];
        for (int i = column; i < column + columnSpan && i < _columnWidths.Length; i++)
            width += _columnWidths[i];
        for (int i = row; i < row + rowSpan && i < _rowHeights.Length; i++)
            height += _rowHeights[i];
        return new Rect(left, top, Math.Max(1, width), Math.Max(1, height));
    }

    /// <summary>坐标 → 行列（表格外返回 (-1, -1)）。</summary>
    public (int Row, int Column) CellAt(Point point)
    {
        if (point.X < 0 || point.Y < 0) return (-1, -1);

        int row = -1, column = -1;
        double y = 0;
        for (int i = 0; i < _rowHeights.Length; i++)
        {
            if (point.Y < y + _rowHeights[i])
            {
                row = i;
                break;
            }

            y += _rowHeights[i];
        }

        double x = 0;
        for (int i = 0; i < _columnWidths.Length; i++)
        {
            if (point.X < x + _columnWidths[i])
            {
                column = i;
                break;
            }

            x += _columnWidths[i];
        }

        return row >= 0 && column >= 0 ? (row, column) : (-1, -1);
    }

    /// <summary>命中所在的合并格锚点（冒烟 / 自检用）。</summary>
    public (int Row, int Column) AnchorAt(Point point)
    {
        (int row, int column) = CellAt(point);
        return row >= 0 ? _document.AnchorAt(row, column) : (-1, -1);
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        var surface = ThemeBrush("SurfaceBrush", Brushes.White);
        var header = ThemeBrush("SurfaceAltBrush", Brushes.WhiteSmoke);
        var hover = ThemeBrush("HoverBgBrush", Brushes.WhiteSmoke);
        var selection = ThemeBrush("LightAccentBrush", Brushes.LightBlue);
        var grid = ThemeBrush("BorderBrush", Brushes.Gray);
        var focus = ThemeBrush("PrimaryBrush", Brushes.DodgerBlue);
        var textBrush = ThemeBrush("TextBrush", Brushes.Black);

        dc.DrawRectangle(surface, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_document.IsEmpty) return;

        var gridPen = new Pen(grid, LineWidth);
        gridPen.Freeze();
        var focusPen = new Pen(focus, 2);
        focusPen.Freeze();

        int rows = _document.RowCount;
        int columns = _document.ColumnCount;
        double lineHeight = LineHeight;
        double ppd = PixelsPerDip;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                TableCell? cell = _document.AnchorCellAt(row, column);
                if (cell == null) continue;

                Rect rect = CellRect(row, column);
                bool selected = false;
                for (int r = row; r < row + cell.RowSpan && !selected; r++)
                    for (int c = column; c < column + cell.ColumnSpan && !selected; c++)
                        selected = _selection.Contains(r, c);

                Brush background = selected ? selection
                    : _hover == (row, column) ? hover
                    : cell.Header ? header
                    : surface;

                dc.DrawRectangle(background, null, rect);
                dc.DrawRectangle(null, gridPen, Inset(rect, LineWidth / 2));

                string text = cell.Text;
                if (text.Length > 0)
                {
                    var typeface = cell.Header ? BoldTypeface : Typeface;
                    var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, typeface, FontSize, textBrush, ppd)
                    {
                        MaxTextWidth = Math.Max(1, rect.Width - Padding * 2 - LineWidth * 2),
                        MaxTextHeight = Math.Max(1, rect.Height - Padding * 2 - LineWidth * 2),
                        MaxLineCount = 3,
                        Trimming = TextTrimming.CharacterEllipsis,
                    };

                    double top = rect.Top + Math.Max(Padding, (rect.Height - formatted.Height) / 2);
                    dc.DrawText(formatted, new Point(rect.Left + Padding + LineWidth, top));
                }

                if ((row, column) == _current && !IsEditing)
                    dc.DrawRectangle(null, focusPen, Inset(rect, 1));
            }
        }

        _ = lineHeight;
    }

    private static Rect Inset(Rect rect, double amount) => new(
        rect.X + amount, rect.Y + amount,
        Math.Max(0, rect.Width - amount * 2), Math.Max(0, rect.Height - amount * 2));

    private Brush ThemeBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;
}
