using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗：文字层（可选中 / 编辑 / 复制）、近似表格、二维码，外加「显示原图 / 填充背景 /
/// 原文预览」。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshotrecognitionwindow</c>：**无边框、置顶、贴选区摆位**，
/// 文字不是一整块只读文本，而是按 OCR 行框重排的**文字层**——点一下落下光标、拖过是选区、
/// 双击选整行，可以直接改识别错的字再复制。
/// </para>
/// <para>
/// 识别要一两秒，所以窗口**先开再算**：<c>Loaded</c> 里把 <see cref="CaptureRecognize.Run"/>
/// 丢到后台线程，算完回 UI 线程填内容（与编辑器试运行的轨迹上屏同一套思路）。
/// </para>
/// <para>
/// 架构差异（写进 <c>docs/SCREEN_CAPTURE_PRO.md</c> §5）：上游的识别窗是 Qt 的无边框 Tool 窗，
/// 它自己贴住选区；本项目覆盖层在「识别」时已经关闭，所以窗口自带「复制 / 关闭 / 显示原图」
/// 这几个出口，而不是依赖覆盖层的工具条。
/// </para>
/// </remarks>
public partial class CaptureResultWindow : Window
{
    private readonly BitmapSource? _image;
    private readonly string? _engine;
    private readonly CaptureEntry? _entry;
    private readonly bool _runOnLoad;
    private readonly Int32Rect _area;

    private RecognizeResult? _result;
    private string _tab = "text";
    private bool _started;
    private double _scale = 1;

    private OcrTextEditModel? _model;
    private IReadOnlyList<OcrTextLine> _layer = Array.Empty<OcrTextLine>();
    private CaptureOriginalPreviewWindow? _preview;
    private ConversionDocument _conversion = ConversionDocument.Empty;
    private string _renderedConversion = "";
    private bool _documentRendered;

    /// <summary>正常打开：先显示「正在识别…」，Loaded 之后在后台跑识别。</summary>
    internal CaptureResultWindow(BitmapSource image, string? engine, CaptureEntry? entry = null,
        Int32Rect area = default)
    {
        InitializeComponent();

        _image = image;
        _engine = engine;
        _entry = entry;
        _area = area;
        _runOnLoad = true;

        OriginalImage.Source = image;
        FillTranslateTargets();
        Place();
        Loaded += (_, _) =>
        {
            UpdateZoom();
            Start();
        };
        SizeChanged += (_, _) => UpdateZoom();
        PreviewKeyDown += OnPreviewKeyDown;
        TextInput += OnTextInput;
    }

    /// <summary>冒烟 / 自检用：直接灌一份已经算好的结果，不跑 OCR、不读屏幕。</summary>
    internal CaptureResultWindow(RecognizeResult result) : this(result, null)
    {
    }

    /// <summary>同上，但连原图一起给（「显示原图 / 原文预览」要有图才成立）。</summary>
    internal CaptureResultWindow(RecognizeResult result, BitmapSource? image)
    {
        InitializeComponent();

        _image = image;
        _runOnLoad = false;
        if (image != null) OriginalImage.Source = image;
        FillTranslateTargets();
        Place();
        Apply(result);
    }

    /// <summary>打开结果窗（非模态：用户慢慢改 / 复制，流程该干嘛干嘛）。</summary>
    internal static void Show(BitmapSource image, string? engine, CaptureEntry? entry = null,
        Int32Rect area = default)
    {
        var window = new CaptureResultWindow(image, engine, entry, area);
        window.Show();
        window.Activate();
        window.Focus();
    }

    /// <summary>识别结果（冒烟读它做断言；还没算完时为 null）。</summary>
    internal RecognizeResult? Result => _result;

    /// <summary>当前页：text / table / qr。</summary>
    internal string CurrentTab => _tab;

    /// <summary>文字层编辑模型（还没识别完时为 null）。</summary>
    internal OcrTextEditModel? EditModel => _model;

    /// <summary>文字层画布。</summary>
    internal CaptureOcrCanvas Canvas => OcrCanvas;

    /// <summary>Markdown / HTML 页的派生内容（冒烟读它做断言）。</summary>
    internal ConversionDocument Conversion => _conversion;

    /// <summary>Markdown / HTML 页的渲染预览（冒烟读它做断言）。</summary>
    internal CaptureConversionView ConversionView => ConversionPage;

    /// <summary>文档页的渲染预览（冒烟读它做断言）。</summary>
    internal CaptureConversionView DocumentPreview => DocumentView;

    private async void Start()
    {
        if (!_runOnLoad || _started) return;
        _started = true;

        if (_image == null)
        {
            Fail("没有可识别的图片");
            return;
        }

        RecognizeResult result;
        try
        {
            var image = _image;
            string? engine = _engine;
            result = await Task.Run(() => CaptureRecognize.Run(image, engine));
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Result", ex);
            if (_entry != null) CaptureCatalog.SetText(_entry.Id, "");
            Fail(ex.Message);
            return;
        }

        // 采集历史里的文字回填与窗口是否还开着无关：用户关掉结果窗也不该一直显示「识别中…」
        if (_entry != null) CaptureCatalog.SetText(_entry.Id, result.Text);

        if (!IsVisible) return;   // 用户已经关掉窗口
        Apply(result);
    }

    private void Fail(string message)
    {
        StatusText.Text = "识别失败";
        DetailText.Text = message;
        CopyBtn.IsEnabled = false;
    }

    internal void Apply(RecognizeResult result)
    {
        _result = result;

        _layer = result.TextLayer.Count > 0 ? result.TextLayer : OcrTextLayer.FromLines(result.Lines);

        var model = new OcrTextEditModel();
        model.Load(_layer.Select(line => line.Text));
        model.Changed += RefreshStatus;
        _model = model;

        int width = _image?.PixelWidth ?? Extent(_layer, true);
        int height = _image?.PixelHeight ?? Extent(_layer, false);
        OcrCanvas.Attach(model, _layer, width, height);
        OcrCanvas.SelectionChanged += RefreshStatus;
        OcrCanvas.LineEditRequested += _ => RefreshStatus();

        FillTable(result.Table);
        FillQr(result);

        // Markdown / HTML 由 OCR 行派生（上游这两页是云模型，本地按版面派生，见 ConversionDocument）
        _conversion = ConversionDocument.Build(result.Lines, result.Table);

        if (!result.Ok)
        {
            Fail(result.Error);
        }
        else
        {
            DetailText.Text = string.Format(CultureInfo.CurrentCulture,
                "{0} · 用时 {1:0.0} 秒", result.Engine, result.ElapsedMs / 1000.0);
        }

        CopyBtn.IsEnabled = result.Ok || _layer.Count > 0;
        UpdateTabs();
        UpdateZoom();
        RefreshStatus();
    }

    private static int Extent(IReadOnlyList<OcrTextLine> lines, bool horizontal)
    {
        int max = 1;
        foreach (OcrTextLine line in lines)
        {
            int value = horizontal ? line.Box.X + line.Box.Width : line.Box.Y + line.Box.Height;
            if (value > max) max = value;
        }

        return max;
    }

    // ============ 摆位 / 缩放 ============

    /// <summary>贴选区摆位：优先盖住选区，放不下就夹到所在显示器的工作区里。</summary>
    private void Place()
    {
        if (_area.Width <= 0 || _area.Height <= 0)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        ScreenGeometry? screen = CaptureRecordWindow.ScreenOf(_area);
        _scale = screen?.Scale ?? 1;
        if (_scale <= 0) _scale = 1;

        double areaLeft = _area.X / _scale;
        double areaTop = _area.Y / _scale;
        double areaWidth = _area.Width / _scale;
        double areaHeight = _area.Height / _scale;

        Width = Math.Clamp(areaWidth + 26, 460, 1000);
        Height = Math.Clamp(areaHeight + 132, 340, 840);

        double left = areaLeft + (areaWidth - Width) / 2;
        double top = areaTop;

        if (screen != null)
        {
            double workLeft = screen.WorkLeft / _scale;
            double workTop = screen.WorkTop / _scale;
            double workRight = (screen.WorkLeft + screen.WorkWidth) / _scale;
            double workBottom = (screen.WorkTop + screen.WorkHeight) / _scale;

            left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - Width));
            top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - Height));
        }

        Left = left;
        Top = top;
    }

    /// <summary>
    /// 文字层缩放：1 源像素 = 1 屏幕物理像素（= 1 / DPI 缩放 DIP）；窗口装不下时再等比缩小。
    /// </summary>
    private void UpdateZoom()
    {
        if (OcrCanvas == null) return;

        double imageWidth = Math.Max(1, OcrCanvas.ImageWidth);
        double imageHeight = Math.Max(1, OcrCanvas.ImageHeight);

        double availableWidth = OcrScroll.ActualWidth - 4;
        double availableHeight = OcrScroll.ActualHeight - 4;
        if (double.IsNaN(availableWidth) || availableWidth <= 0) availableWidth = Width - 30;
        if (double.IsNaN(availableHeight) || availableHeight <= 0) availableHeight = Height - 170;

        double zoom = 1 / _scale;
        if (imageWidth * zoom > availableWidth) zoom = availableWidth / imageWidth;
        if (imageHeight * zoom > availableHeight) zoom = Math.Min(zoom, availableHeight / imageHeight);
        zoom = Math.Max(zoom, 0.05);

        OcrCanvas.SetZoom(zoom);

        OriginalImage.Width = imageWidth * zoom;
        OriginalImage.Height = imageHeight * zoom;
    }

    // ============ 页签 ============

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button) return;
        _tab = button.Tag as string ?? "text";

        // XAML 解析期间 IsChecked="True" 会先触发一次，那时后面的兄弟控件还没建出来
        if (OcrScroll == null) return;

        UpdateTabs();
        RefreshStatus();
    }

    private void UpdateTabs()
    {
        // XAML 解析期间 IsChecked="True" 会先触发一次 Checked，那时后面的兄弟控件还没建出来
        if (OcrScroll is not { } ocrScroll || OriginalToggle is not { } originalToggle ||
            FillToggle is not { } fillToggle || PreviewBtn is not { } previewBtn ||
            CopyBtn is not { } copyBtn || EmptyText is not { } emptyText)
        {
            return;
        }

        bool text = _tab == "text";
        bool conversion = _tab is "markdown" or "html";
        bool document = _tab == "document";
        bool original = originalToggle.IsChecked == true;

        ocrScroll.Visibility = text && !original ? Visibility.Visible : Visibility.Collapsed;
        OriginalScroll.Visibility = text && original ? Visibility.Visible : Visibility.Collapsed;
        TablePage.Visibility = _tab == "table" ? Visibility.Visible : Visibility.Collapsed;
        QrPage.Visibility = _tab == "qr" ? Visibility.Visible : Visibility.Collapsed;
        ConversionPage.Visibility = conversion ? Visibility.Visible : Visibility.Collapsed;
        DocumentPage.Visibility = document ? Visibility.Visible : Visibility.Collapsed;

        // 只在内容 / 页真的变了时重渲染：翻开关不该把用户刚划好的选区清掉
        if (conversion &&
            (!ReferenceEquals(ConversionPage.Content, _conversion) || _renderedConversion != _tab))
        {
            ConversionPage.Render(_conversion, _tab == "html"
                ? "没有可转换成 HTML 的内容（这张截图没有识别到文字）。"
                : "没有可转换成 Markdown 的内容（这张截图没有识别到文字）。");
            _renderedConversion = _tab;
        }

        if (document && (!_documentRendered || !ReferenceEquals(DocumentView.Content, _conversion)))
        {
            DocumentView.Render(_conversion, "这张截图没有识别到文字，无法生成文档。");
            _documentRendered = true;
        }

        DocExportBtn.IsEnabled = !_conversion.IsEmpty;
        DocOpenBtn.IsEnabled = !_conversion.IsEmpty;
        DocInfo.Text = _conversion.IsEmpty
            ? "没有可导出的内容"
            : string.Format(CultureInfo.CurrentCulture, "{0} 块 · {1} 个字符",
                _conversion.Blocks.Count, _conversion.Length);

        emptyText.Visibility = text && !original && _layer.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        originalToggle.IsEnabled = text;
        fillToggle.IsEnabled = text && !original;
        previewBtn.IsEnabled = text && _image != null;

        copyBtn.Content = _tab switch
        {
            "table" => "复制表格",
            "qr" => "复制二维码内容",
            "markdown" => "复制 Markdown",
            "html" => "复制 HTML",
            "document" => "复制文档",
            _ => CopyLabelForText(),
        };

        copyBtn.IsEnabled = conversion || document
            ? !_conversion.IsEmpty
            : _result is { Ok: true } || _layer.Count > 0;

        // 翻译只服务文字页：别的页签把这一组收起（不放死按钮）
        UpdateTranslateBar();

        // 表格页时贴选区摆好的窗口可能装不下整张表：切过去先把画布宽度对齐视口
        if (_tab == "table")
        {
            TableScroll.UpdateLayout();
            TableCanvas.SetViewportWidth(TableScroll.ViewportWidth);
            RefreshTableCommands();
        }
    }

    // ============ 结果填充 ============

    private void FillQr(RecognizeResult result)
    {
        QrView.Text = result.QrText;
        QrInfo.Text = result.QrText.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, "格式 {0} · {1} 个字符", result.QrType,
                result.QrText.Length)
            : "这张截图里没有找到二维码。";
    }

    /// <summary>状态行：识别完成后的字数 / 选区字数（没有识别完就报引擎与用时）。</summary>
    private void RefreshStatus()
    {
        if (_result == null)
        {
            StatusText.Text = "正在识别…";
            return;
        }

        if (!_result.Ok)
        {
            StatusText.Text = "识别失败";
            return;
        }

        if (_tab == "table")
        {
            if (_tableDocument.IsEmpty)
            {
                StatusText.Text = "没有识别出表格";
                return;
            }

            TableRange range = TableCanvas.Selection;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "表格 {0} 行 × {1} 列 · 选中 {2} 格",
                _tableDocument.RowCount, _tableDocument.ColumnCount,
                range.IsValid ? range.RowCount * range.ColumnCount : 0);
            return;
        }

        if (_tab is "markdown" or "html")
        {
            string name = _tab == "html" ? "HTML" : "Markdown";
            if (_conversion.IsEmpty)
            {
                StatusText.Text = name + " · 没有可转换的内容";
                return;
            }

            string selected = ConversionPage.SelectedText;
            StatusText.Text = selected.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} · 已选中 {1} 个字符 · 共 {2} 块",
                    name, selected.Length, _conversion.Blocks.Count)
                : string.Format(CultureInfo.CurrentCulture, "{0} · {1} 块 · {2} 个字符 · 可划选后复制",
                    name, _conversion.Blocks.Count, _conversion.Length);
            return;
        }

        if (_tab == "document")
        {
            if (_conversion.IsEmpty)
            {
                StatusText.Text = "文档 · 没有可用的内容";
                return;
            }

            string selected = DocumentView.SelectedText;
            StatusText.Text = selected.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, "文档 · 已选中 {0} 个字符 · 共 {1} 块",
                    selected.Length, _conversion.Blocks.Count)
                : string.Format(CultureInfo.CurrentCulture, "文档 · {0} 块 · {1} 个字符 · 可划选后复制",
                    _conversion.Blocks.Count, _conversion.Length);
            return;
        }

        if (_translatedLines != null)
        {
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "译文 · {0} 行 · 目标 {1} · 点「原文」切回",
                _translatedLines.Count, TranslationLanguages.LabelOf(SelectedTarget));
            return;
        }

        if (TranslationError.Length > 0)
        {
            StatusText.Text = "翻译失败：" + TranslationError;
            return;
        }

        if (_model != null && _model.HasSelection)
        {
            int chars = _model.SelectionText().Length;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "已选中 {0} 个字符 · 共 {1} 行",
                chars, _layer.Count);
            return;
        }

        StatusText.Text = _layer.Count == 0
            ? "没有识别到文字"
            : string.Format(CultureInfo.CurrentCulture, "共 {0} 行 · 点一下选字，双击选整行，Ctrl+A 全选",
                _layer.Count);
    }

    // ============ 开关 ============

    private void Original_Changed(object sender, RoutedEventArgs e) => UpdateTabs();

    private void Fill_Changed(object sender, RoutedEventArgs e)
    {
        if (OcrCanvas == null) return;
        OcrCanvas.SetFillBackground(FillToggle.IsChecked == true);
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;

        if (_preview != null)
        {
            _preview.Close();
            _preview = null;
            return;
        }

        var preview = new CaptureOriginalPreviewWindow(_image, _scale);
        preview.Closed += (_, _) => _preview = null;

        ScreenGeometry? screen = CaptureRecordWindow.ScreenOf(_area);
        var work = screen == null
            ? SystemParameters.WorkArea
            : new Rect(screen.WorkLeft / _scale, screen.WorkTop / _scale,
                screen.WorkWidth / _scale, screen.WorkHeight / _scale);

        preview.Show();
        preview.PlaceBeside(new Rect(Left, Top, Width, Height), work);
        _preview = preview;
    }

    // ============ 键盘 / 输入 ============

    private bool TextEditable => _tab == "text" && OriginalToggle.IsChecked != true &&
                                 _translatedLines == null;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 表格单元格正在编辑时 Esc 只退出这一格（上游 QTableView 的语义），不关窗
            if (TableCanvas is { IsEditing: true })
            {
                TableCanvas.CancelEdit();
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
            return;
        }

        if (_tab == "table" && HandleTableKey(e))
        {
            e.Handled = true;
            return;
        }

        if (_tab == "table")
        {
            if (HandleTableKey(e)) e.Handled = true;
            return;
        }

        // Markdown / HTML / 文档：预览自己管滚动与选中，只补 Ctrl+A / Ctrl+C
        // （FlowDocumentScrollViewer 不带全选）
        if (_tab is "markdown" or "html" or "document")
        {
            CaptureConversionView view = _tab == "document" ? DocumentView : ConversionPage;
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && e.Key == Key.A)
            {
                view.SelectAllText();
                e.Handled = true;
                RefreshStatus();
            }
            else if (control && e.Key == Key.C)
            {
                CopyCurrent();
                e.Handled = true;
            }

            return;
        }

        if (_model == null || !TextEditable)
        {
            // 译文模式只读：只保留 Ctrl+C（复制译文），别的编辑键都不认
            if (_translatedLines != null && e.Key == Key.C &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopyCurrent();
                e.Handled = true;
            }

            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.A when ctrl:
                _model.SelectAll();
                break;
            case Key.C when ctrl:
                Copy(_model.HasSelection ? _model.SelectionText() : _model.AllText, "识别文字");
                break;
            case Key.X when ctrl:
                if (!_model.HasSelection) break;
                Copy(_model.SelectionText(), "识别文字");
                _model.DeleteSelection();
                break;
            case Key.V when ctrl:
                if (NativeClipboard.TryGetText(out string pasted) && pasted.Length > 0)
                    _model.InsertText(pasted);
                break;
            case Key.Z when ctrl:
                if (shift) _model.Redo();
                else _model.Undo();
                break;
            case Key.Y when ctrl:
                _model.Redo();
                break;
            case Key.Back:
                _model.Backspace();
                break;
            case Key.Delete:
                _model.DeleteForward();
                break;
            case Key.Left:
                _model.MoveCaret(-1, shift);
                break;
            case Key.Right:
                _model.MoveCaret(1, shift);
                break;
            case Key.Up:
                _model.MoveLine(-1, shift);
                break;
            case Key.Down:
                _model.MoveLine(1, shift);
                break;
            case Key.Enter:
                _model.InsertText("\n");
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_model == null || !TextEditable) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        if (char.IsControl(e.Text[0]) && e.Text != "\n") return;

        _model.InsertText(e.Text);
        e.Handled = true;
    }

    // ============ 复制 / 关闭 ============

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();

    private void CopyCurrent()
    {
        switch (_tab)
        {
            case "table":
                // 有选区就复制选中的格子，没有就整张表（HTML + 文本一起写）
                TableCopySelection();
                break;
            case "qr":
                Copy(_result?.QrText, "二维码内容");
                break;
            case "markdown":
                CopyConversion(asHtml: false);
                break;
            case "html":
                CopyConversion(asHtml: true);
                break;
            case "document":
                CopyDocument();
                break;
            default:
                // 译文模式：复制整段译文（逐行）
                if (_translatedLines != null)
                {
                    Copy(string.Join("\r\n", _translatedLines), "译文");
                    break;
                }

                // 有选区就复制选中的，没有就整份（与上游识别窗的复制语义一致）
                if (_model == null)
                {
                    Copy(_result?.Text, "识别文字");
                }
                else
                {
                    Copy(_model.HasSelection ? _model.SelectionText() : _model.AllText, "识别文字");
                }

                break;
        }
    }

    private static void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show($"已复制{what}", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    /// <summary>
    /// 复制 Markdown / HTML（照上游 <c>imageConversionClipboardMimeData</c>）：
    /// **选了就复制选中那段**，没选就复制整份源码；HTML 另外写一份 text/html 剪贴板格式。
    /// </summary>
    private void CopyConversion(bool asHtml)
    {
        if (_conversion.IsEmpty)
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        string name = asHtml ? "HTML" : "Markdown";
        string selected = ConversionPage.SelectedText;

        string text = selected.Length > 0 ? selected : (asHtml ? _conversion.Html : _conversion.Markdown);
        if (text.Length == 0)
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        bool ok;
        if (asHtml)
        {
            // 选中那段没有富文本结构，包一层段落再当 HTML 写；整份源码本身就是 HTML
            string html = selected.Length > 0
                ? "<p>" + EscapeHtml(selected).Replace("\r\n", "<br>").Replace("\n", "<br>") + "</p>"
                : _conversion.Html;
            ok = NativeClipboard.SetHtmlAndText(html, text);
        }
        else
        {
            ok = NativeClipboard.SetText(text);
        }

        if (ok) ToastService.Show($"已复制{name}", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开（快速点击）：没有可拖的循环，忽略
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // 关窗要掐掉还在飞的翻译请求，并把画布切回原文（窗口已关，但画布对象可能还被引用）
        try
        {
            _translationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经结束
        }

        _translationCts = null;
        _translatedLines = null;

        _preview?.Close();
        _preview = null;
    }
}
