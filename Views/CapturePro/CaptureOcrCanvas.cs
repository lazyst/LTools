using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果的「文字层」：把 OCR 的行框重排成可读、可选中、可编辑的文字，并按原图像素估出的
/// 字色 / 底色绘制。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshotocrtextlayer.cpp</c> 的定位：**文字层不是只读文本框**——它的坐标仍属于
/// 源图片，所以可以逐行点中、框选、双击整行改写，而「显示原图」只是把同一块画布换成原图。
/// </para>
/// <para>
/// 画布坐标 = **源图像素**，整体乘一个 <see cref="Zoom"/>（DPI 与窗口宽高的拟合），
/// 所以文字与原始行框永远对得上，缩放只改变观感。
/// </para>
/// </remarks>
internal sealed class CaptureOcrCanvas : FrameworkElement
{
    private readonly Dictionary<int, FormattedText> _textCache = new();
    private readonly Dictionary<int, double> _sizeCache = new();

    private OcrTextEditModel? _model;
    private IReadOnlyList<OcrTextLine> _lines = Array.Empty<OcrTextLine>();
    private IReadOnlyList<string>? _translation;
    private IReadOnlyList<OcrTextLine>? _translationLayer;
    private ImageSource? _sourceImage;
    private bool _translationOnImage;

    private int _hoverLine = -1;
    private int _dragLine = -1;
    private bool _dragging;

    public CaptureOcrCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>画布缩放：DIP / 源图像素。</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>文字层是否铺每行估出来的底色（关掉 = 只画文字）。</summary>
    public bool FillBackground { get; set; } = true;

    /// <summary>
    /// 隐形文字层（照上游 <c>RenderingMode::SelectionOnly</c>）：不画白纸底、不画文字、不画光标，
    /// 只把拖选中的那块高亮画出来——底下的原图看起来一点没变，但文字能直接划选。
    /// 命中只认文字行（空白处让给窗口拖动）。
    /// </summary>
    public bool SelectionOnly { get; private set; }

    /// <summary>切到 / 退出隐形文字层。</summary>
    public void SetSelectionOnly(bool on)
    {
        if (SelectionOnly == on) return;
        SelectionOnly = on;
        _hoverLine = -1;
        _dragLine = -1;
        _dragging = false;
        Cursor = on ? Cursors.IBeam : Cursors.Arrow;
        InvalidateVisual();
    }

    /// <summary>源图像素宽高（决定画布大小）。</summary>
    public int ImageWidth { get; private set; } = 1;

    public int ImageHeight { get; private set; } = 1;

    /// <summary>双击某一行（界面据此弹整行编辑）。</summary>
    public event Action<int>? LineEditRequested;

    /// <summary>用户改了选区 / 文字（界面据此刷新状态行）。</summary>
    public event Action? SelectionChanged;

    /// <summary>光标所在的行（-1 = 不在任何行上）。</summary>
    public int HoverLine => _hoverLine;

    public void Attach(OcrTextEditModel model, IReadOnlyList<OcrTextLine> lines, int imageWidth,
        int imageHeight)
    {
        if (_model != null) _model.Changed -= OnModelChanged;
        _model = model;
        _lines = lines;
        _translation = null;
        _translationLayer = null;
        _translationOnImage = false;
        ImageWidth = Math.Max(1, imageWidth);
        ImageHeight = Math.Max(1, imageHeight);
        _model.Changed += OnModelChanged;
        ResetMeasure();
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>
    /// 译文的底图（照上游 <c>originalImageTranslation</c>）：给了它且
    /// <see cref="SetTranslation"/> 打开「画进原图」时，画布底色换成这张原图。
    /// </summary>
    public void SetSourceImage(ImageSource? image)
    {
        if (ReferenceEquals(_sourceImage, image)) return;
        _sourceImage = image;
        InvalidateVisual();
    }

    /// <summary>文字层现在显示的是译文（照上游的 overlay translation：同一批行框换成译文）。</summary>
    public bool ShowingTranslation => _translation != null;

    /// <summary>当前显示的译文行（没开翻译时为 null）。</summary>
    public IReadOnlyList<string>? TranslationLines => _translation;

    /// <summary>译文落在哪些行框上（智能合并时行数比文字层少；没开翻译时为 null）。</summary>
    public IReadOnlyList<OcrTextLine>? TranslationLayer => _translationLayer;

    /// <summary>译文是不是画在截图上（上游 <c>originalImageTranslation</c>：底图换成原图）。</summary>
    public bool TranslationOnImage => _translation != null && _translationOnImage;

    /// <summary>
    /// 把文字层换成译文 / 换回原文。译文模式下**只读**：不画选区与光标、点不中行，
    /// 这样译文不会被误编辑，划选仍交给窗口里的「复制译文」。
    /// </summary>
    /// <param name="lines">译文（null = 切回原文）。</param>
    /// <param name="layer">译文落在哪些行框上（null = 与文字层一一对应）。</param>
    /// <param name="onOriginalImage">译文画在截图上（底图换原图，照上游 originalImageTranslation）。</param>
    public void SetTranslation(IReadOnlyList<string>? lines, IReadOnlyList<OcrTextLine>? layer = null,
        bool onOriginalImage = false)
    {
        if (ReferenceEquals(_translation, lines) && ReferenceEquals(_translationLayer, layer) &&
            _translationOnImage == onOriginalImage)
        {
            return;
        }

        _translation = lines;
        _translationLayer = lines == null ? null : layer ?? _lines;
        _translationOnImage = lines != null && onOriginalImage;
        _hoverLine = -1;
        _dragLine = -1;
        _dragging = false;
        Cursor = Cursors.Arrow;
        ResetMeasure();
        InvalidateVisual();
    }

    /// <summary>换缩放（DPI / 窗口拟合）。</summary>
    public void SetZoom(double zoom)
    {
        zoom = Math.Clamp(zoom, 0.05, 8);
        if (Math.Abs(zoom - Zoom) < 0.0005) return;
        Zoom = zoom;
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>底色开关变了。</summary>
    public void SetFillBackground(bool fill)
    {
        if (FillBackground == fill) return;
        FillBackground = fill;
        InvalidateVisual();
    }

    /// <summary>让某一行进入视野并选中它（诊断 / 定位用）。</summary>
    public void SelectLine(int index)
    {
        if (_model == null || index < 0 || index >= _model.LineCount) return;
        _model.SelectLine(index);
        BringIntoView(index);
    }

    /// <summary>把某一行滚进视野（<see cref="System.Windows.Controls.ScrollViewer"/> 里也能用）。</summary>
    public void BringIntoView(int index)
    {
        if (index < 0 || index >= _lines.Count) return;
        Int32Rect box = _lines[index].Box;
        var rect = new Rect(box.X * Zoom, box.Y * Zoom, Math.Max(1, box.Width * Zoom),
            Math.Max(1, box.Height * Zoom));
        InvalidateVisual();
        BringIntoView(rect);
    }

    // ============ 布局 ============

    protected override Size MeasureOverride(Size availableSize) =>
        new(ImageWidth * Zoom, ImageHeight * Zoom);

    protected override Size ArrangeOverride(Size finalSize) => new(ImageWidth * Zoom,
        ImageHeight * Zoom);

    private void ResetMeasure()
    {
        _textCache.Clear();
        _sizeCache.Clear();
    }

    private void OnModelChanged()
    {
        _textCache.Clear();
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(0, 0, ImageWidth * Zoom, ImageHeight * Zoom);

        // 译文画进原图时底图是截图本身（上游 originalImageTranslation 的画布），
        // 否则是白纸（文字层本身就是「重排后的文字」，与上游识别内容页的容器底色一致）。
        // 隐形文字层什么都不铺：底下的原图就是底图（照上游 SelectionOnly）。
        if (!SelectionOnly)
        {
            if (TranslationOnImage && _sourceImage is ImageSource source)
                dc.DrawImage(source, bounds);
            else
                dc.DrawRectangle(Brushes.White, null, bounds);
        }

        dc.PushTransform(new ScaleTransform(Zoom, Zoom));

        IReadOnlyList<OcrTextLine> layer = _translation != null ? _translationLayer ?? _lines : _lines;
        IReadOnlyList<string> text = _translation ?? _model?.Lines ?? Array.Empty<string>();
        var (selStartLine, selStartChar, selEndLine, selEndChar) = Selection();

        for (int i = 0; i < layer.Count && !SelectionOnly; i++)
        {
            OcrTextLine line = layer[i];
            string content = i < text.Count ? text[i] : line.Text;
            var box = new Rect(line.Box.X, line.Box.Y,
                Math.Max(1, line.Box.Width), Math.Max(1, line.Box.Height));

            if (FillBackground)
                dc.DrawRectangle(OcrTextPaint.Brush(line.BackgroundColor), null, box);

            if (content.Length == 0) continue;

            FormattedText formatted = TextFor(i, content, line);
            formatted.SetForegroundBrush(OcrTextPaint.Brush(line.TextColor));
            double top = box.Y + (box.Height - formatted.Height) / 2;
            var origin = new Point(box.X, top);

            dc.PushClip(new RectangleGeometry(box));
            dc.DrawText(formatted, origin);
            dc.Pop();

            if (i == _hoverLine && _translation == null)
            {
                var pen = new Pen(PrimaryBrush(), 1.2) { DashStyle = DashStyles.Dot };
                dc.DrawRectangle(null, pen, box);
            }
        }

        if (_translation == null && _model != null && _model.HasSelection)
        {
            DrawSelection(dc, text, selStartLine, selStartChar, selEndLine, selEndChar);
        }
        else if (!SelectionOnly && _translation == null && _model != null && IsKeyboardFocusWithin)
        {
            DrawCaret(dc, text);
        }

        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc, IReadOnlyList<string> text, int startLine,
        int startChar, int endLine, int endChar)
    {
        var fill = new SolidColorBrush(PrimaryColor(0.28));
        for (int i = startLine; i <= endLine && i < _lines.Count; i++)
        {
            OcrTextLine line = _lines[i];
            string content = i < text.Count ? text[i] : line.Text;
            var box = new Rect(line.Box.X, line.Box.Y,
                Math.Max(1, line.Box.Width), Math.Max(1, line.Box.Height));

            int from = i == startLine ? startChar : 0;
            int to = i == endLine ? endChar : content.Length;
            from = Math.Clamp(from, 0, content.Length);
            to = Math.Clamp(to, from, content.Length);

            double height = Math.Max(4, box.Height);
            double top = box.Y + (box.Height - height) / 2;
            if (content.Length == 0)
            {
                dc.DrawRectangle(fill, null, new Rect(box.X, top, Math.Max(3, box.Height * 0.12),
                    height));
                continue;
            }

            double x1 = box.X + PrefixWidth(i, line, content, from);
            double x2 = box.X + PrefixWidth(i, line, content, to);
            if (x2 - x1 < 1.5) x2 = x1 + 1.5;
            dc.DrawRectangle(fill, null, new Rect(x1, top, x2 - x1, height));
        }
    }

    private void DrawCaret(DrawingContext dc, IReadOnlyList<string> text)
    {
        if (_model == null || _model.CaretLine >= _lines.Count) return;

        int index = _model.CaretLine;
        OcrTextLine line = _lines[index];
        string content = index < text.Count ? text[index] : line.Text;
        int caret = Math.Clamp(_model.CaretChar, 0, content.Length);
        double x = line.Box.X + PrefixWidth(index, line, content, caret);
        double height = Math.Max(4, line.Box.Height * 0.92);
        dc.DrawRectangle(PrimaryBrush(), null,
            new Rect(x, line.Box.Y + (line.Box.Height - height) / 2, 1.6, height));
    }

    private FormattedText TextFor(int index, string content, OcrTextLine line)
    {
        double available = Math.Max(1, line.Box.Width);
        if (_textCache.TryGetValue(index, out FormattedText? cached) &&
            cached.Text == content && Math.Abs(cached.MaxTextWidth - available) < 0.5)
        {
            return cached;
        }

        (FormattedText text, double fitted) = OcrTextPaint.Fit(content, line, PixelsPerDip);
        _textCache[index] = text;
        _sizeCache[index] = fitted;
        return text;
    }

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    /// <summary>前 <paramref name="count"/> 个字符的宽度（源图像素）。</summary>
    private double PrefixWidth(int index, OcrTextLine line, string content, int count)
    {
        if (count <= 0) return 0;
        if (count >= content.Length)
            return TextFor(index, content, line).WidthIncludingTrailingWhitespace;

        double size = BestSize(index, content, line);
        return OcrTextPaint.Create(content[..count], size, PixelsPerDip)
            .WidthIncludingTrailingWhitespace;
    }

    private double BestSize(int index, string content, OcrTextLine line)
    {
        if (_sizeCache.TryGetValue(index, out double size)) return size;

        TextFor(index, content, line);
        if (_sizeCache.TryGetValue(index, out size)) return size;
        return line.FontSize ?? Math.Max(1, line.Box.Height * 0.82);
    }

    // ============ 命中 ============

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_model == null || _translation != null) return;

        Focus();
        Point point = e.GetPosition(this);
        (int line, double x) = HitTest(point);
        if (line < 0)
        {
            _model.ClearSelection();
            _dragLine = -1;
            return;
        }

        if (e.ClickCount >= 2)
        {
            _model.SelectLine(line);
            if (!SelectionOnly) LineEditRequested?.Invoke(line);   // 隐形层没有编辑框：双击只是选整行
            _dragLine = -1;
            e.Handled = true;
            return;
        }

        int ch = CharAt(line, x);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (shift) _model.ExtendCaret(line, ch);
        else _model.SetCaret(line, ch);

        _dragLine = line;
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model == null) return;
        if (_translation != null)
        {
            if (_hoverLine >= 0)
            {
                _hoverLine = -1;
                InvalidateVisual();
            }

            Cursor = Cursors.Arrow;
            return;
        }

        Point point = e.GetPosition(this);

        if (_dragging && _dragLine >= 0)
        {
            (int line, double x) = HitTest(point);
            if (line < 0) line = point.Y < 0 ? 0 : _lines.Count - 1;
            if (line >= 0 && line < _lines.Count)
            {
                _model.ExtendCaret(line, CharAt(line, x));
            }

            return;
        }

        int hover = HitTest(point).Line;
        if (hover == _hoverLine) return;
        _hoverLine = hover;
        Cursor = hover >= 0 ? Cursors.IBeam : Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            _dragLine = -1;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        if (_hoverLine < 0) return;
        _hoverLine = -1;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    /// <summary>DIP 点 → (行号, 行内 x)。不在任何行上时行号为 -1。</summary>
    internal (int Line, double X) HitTest(Point point)
    {
        double ix = point.X / Zoom;
        double iy = point.Y / Zoom;

        for (int i = 0; i < _lines.Count; i++)
        {
            Int32Rect box = _lines[i].Box;
            if (ix < box.X || ix > box.X + box.Width) continue;
            if (iy < box.Y || iy > box.Y + box.Height) continue;
            return (i, ix - box.X);
        }

        return (-1, 0);
    }

    /// <summary>
    /// 隐形文字层只让文字行可点：空白处不命中，鼠标落到贴图窗口上照常拖动窗口。
    /// </summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (!SelectionOnly) return base.HitTestCore(hitTestParameters);

        Point point = hitTestParameters.HitPoint;
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight) return null!;
        return HitTest(point).Line >= 0 ? new PointHitTestResult(this, point) : null!;
    }

    /// <summary>
    /// 当前选区在**画布 DIP**（未含 RenderTransform）里的外接矩形；没有选区返回 <c>null</c>。
    /// 隐形文字层用它把「复制」小按钮摆在选区旁边。
    /// </summary>
    internal Rect? SelectionBounds()
    {
        if (_model == null || _translation != null || !_model.HasSelection || _lines.Count == 0)
            return null;

        var (startLine, startChar, endLine, endChar) = Selection();
        var text = _model.Lines;
        Rect union = Rect.Empty;

        for (int i = startLine; i <= endLine && i < _lines.Count; i++)
        {
            OcrTextLine line = _lines[i];
            string content = i < text.Count ? text[i] : line.Text;
            var box = new Rect(line.Box.X, line.Box.Y,
                Math.Max(1, line.Box.Width), Math.Max(1, line.Box.Height));

            int from = i == startLine ? startChar : 0;
            int to = i == endLine ? endChar : content.Length;
            from = Math.Clamp(from, 0, content.Length);
            to = Math.Clamp(to, from, content.Length);

            double height = Math.Max(4, box.Height);
            double top = box.Y + (box.Height - height) / 2;

            Rect piece;
            if (content.Length == 0)
            {
                piece = new Rect(box.X, top, Math.Max(3, box.Height * 0.12), height);
            }
            else
            {
                double x1 = box.X + PrefixWidth(i, line, content, from);
                double x2 = box.X + PrefixWidth(i, line, content, to);
                if (x2 - x1 < 1.5) x2 = x1 + 1.5;
                piece = new Rect(x1, top, x2 - x1, height);
            }

            union = union.IsEmpty ? piece : Rect.Union(union, piece);
        }

        if (union.IsEmpty) return null;
        union.Scale(Zoom, Zoom);
        return union;
    }

    /// <summary>行内 x（源图像素）→ 字符下标。</summary>
    internal int CharAt(int line, double x)
    {
        if (_model == null || line < 0 || line >= _lines.Count) return 0;
        if (line >= _model.LineCount) return 0;

        string content = _model.Lines[line];
        if (content.Length == 0 || x <= 0) return 0;

        double available = Math.Max(1, _lines[line].Box.Width);
        double size = BestSize(line, content, _lines[line]);

        // 二分：宽度随字符数单调增长
        int low = 0, high = content.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            var prefix = OcrTextPaint.Create(content[..middle], size, PixelsPerDip);
            if (prefix.WidthIncludingTrailingWhitespace <= x) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    /// <summary>纯函数版命中：<paramref name="prefixWidths"/>[k] = 前 k 个字符的宽度。</summary>
    internal static int CharIndexAt(IReadOnlyList<double> prefixWidths, double x)
    {
        if (prefixWidths.Count == 0) return 0;
        if (x <= 0) return 0;
        int low = 0, high = prefixWidths.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (prefixWidths[middle] <= x) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    private (int StartLine, int StartChar, int EndLine, int EndChar) Selection()
    {
        if (_model == null) return (0, 0, 0, 0);

        int anchorLine = _model.AnchorLine, anchorChar = _model.AnchorChar;
        int caretLine = _model.CaretLine, caretChar = _model.CaretChar;

        bool forward = anchorLine < caretLine ||
                       (anchorLine == caretLine && anchorChar <= caretChar);
        return forward
            ? (anchorLine, anchorChar, caretLine, caretChar)
            : (caretLine, caretChar, anchorLine, anchorChar);
    }

    private static Brush PrimaryBrush() => Theme("PrimaryBrush", Color.FromRgb(0x2B, 0x6C, 0xF6));

    private static Color PrimaryColor(double alpha)
    {
        Color color = (Theme("PrimaryBrush", Color.FromRgb(0x2B, 0x6C, 0xF6)) as SolidColorBrush)?.Color
                      ?? Color.FromRgb(0x2B, 0x6C, 0xF6);
        return Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }

    private static Brush Theme(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
}
