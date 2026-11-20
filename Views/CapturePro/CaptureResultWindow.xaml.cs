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
    private readonly bool _quick;
    private bool _copyWhenReady;
    private bool _translateWhenReady;
    private bool _autoActionDone;

    private RecognizeResult? _result;
    private string _tab = "text";
    private bool _started;
    private double _scale = 1;

    private OcrTextEditModel? _model;
    private IReadOnlyList<OcrTextLine> _layer = Array.Empty<OcrTextLine>();
    private ConversionDocument _conversion = ConversionDocument.Empty;
    private string _renderedConversion = "";
    private bool _documentRendered;

    /// <summary>正常打开：先显示「正在识别…」，Loaded 之后在后台跑识别。</summary>
    /// <param name="copyWhenReady">
    /// 算完直接把全文写进剪贴板（照上游 <c>text_recognition_copy</c>：识别窗照开，只是省掉「再点一下复制」）。
    /// </param>
    /// <param name="translateWhenReady">
    /// 算完自动翻一遍（照上游 <c>captureAndTranslateText</c>：先「翻译」按钮那一步，没配端点会弹设置）。
    /// </param>
    /// <param name="quick">
    /// 这一窗是不是「快速识别」入口（<c>ltools.captureRecognize</c> / <c>captureRecognizeCopy</c>）开的：
    /// 只有它为真时 <c>quick_copy_text*</c> 两种自动动作才生效（照上游 <c>m_ocrFromQuickOcrAction</c>）。
    /// </param>
    internal CaptureResultWindow(BitmapSource image, string? engine, CaptureEntry? entry = null,
        Int32Rect area = default, bool copyWhenReady = false, bool translateWhenReady = false,
        bool quick = false)
    {
        InitializeComponent();

        _image = image;
        _engine = engine;
        _entry = entry;
        _area = area;
        _runOnLoad = true;
        _copyWhenReady = copyWhenReady;
        _translateWhenReady = translateWhenReady;
        _quick = quick;

        FillTranslateTargets();
        FillTransformOptions();
        Place();
        Loaded += (_, _) =>
        {
            UpdateZoom();
            Start();
        };
        SizeChanged += (_, _) => { UpdateZoom(); FollowOriginalPreview(); };
        LocationChanged += (_, _) => FollowOriginalPreview();
        PreviewKeyDown += OnPreviewKeyDown;
        TextInput += OnTextInput;
    }

    /// <summary>冒烟 / 自检用：直接灌一份已经算好的结果，不跑 OCR、不读屏幕。</summary>
    internal CaptureResultWindow(RecognizeResult result) : this(result, null)
    {
    }

    /// <summary>同上，但连原图一起给（「原文预览」小窗要有图才成立）。</summary>
    internal CaptureResultWindow(RecognizeResult result, BitmapSource? image)
    {
        InitializeComponent();

        _image = image;
        _runOnLoad = false;
        FillTranslateTargets();
        FillTransformOptions();
        Place();
        Apply(result);
    }

    /// <summary>打开结果窗（非模态：用户慢慢改 / 复制，流程该干嘛干嘛）。</summary>
    /// <param name="initialTab">
    /// 打开就停在哪一页（<c>table_recognition</c> 快捷键直接进表格页）。识别是异步的，
    /// 先切页不会丢内容：算完之后 <see cref="Apply"/> 按当前页重新铺一遍。
    /// </param>
    internal static void Show(BitmapSource image, string? engine, CaptureEntry? entry = null,
        Int32Rect area = default, bool copyWhenReady = false, bool translateWhenReady = false,
        bool quick = false, string? initialTab = null)
    {
        var window = new CaptureResultWindow(image, engine, entry, area, copyWhenReady,
            translateWhenReady, quick);
        window.Show();
        window.Activate();
        window.Focus();
        if (!string.IsNullOrEmpty(initialTab)) window.SelectTab(initialTab);
    }

    /// <summary>直接切到某一页（快捷键 <c>table_recognition</c> 用）。</summary>
    internal void SelectTab(string tag)
    {
        RadioButton? button = tag switch
        {
            "table" => TabTable,
            "qr" => TabQr,
            "document" => TabDocument,
            "markdown" => TabMarkdown,
            "html" => TabHtml,
            "latex" => TabLatex,
            _ => TabText,
        };
        if (button == null) return;

        _tab = button.Tag as string ?? tag;
        button.IsChecked = true;      // 已经选中时 Checked 不会再发，所以先写 _tab
        UpdateTabs();
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
        model.Changed += DropTransformsOnUserEdit;
        _model = model;

        int width = _image?.PixelWidth ?? Extent(_layer, true);
        int height = _image?.PixelHeight ?? Extent(_layer, false);
        _ocrWidth = width;
        _ocrHeight = height;
        OcrCanvas.Attach(model, _layer, width, height);
        OcrCanvas.SetSourceImage(_image);
        OcrCanvas.SetFillStyle(TextRecognitionStore.FillStyle);
        OcrCanvas.SetHighlightText(TextRecognitionStore.ShowTextHighlighting);
        OcrCanvas.SelectionChanged += RefreshStatus;
        OcrCanvas.LineEditRequested += _ => RefreshStatus();

        // 默认排版 / 标点先应用一次（上游 applyInitialTransforms：进文字编辑就生效）
        ApplyInitialTransforms();

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

        // 「识别并复制」：识别完直接把全文写进剪贴板（窗口照开，用户还能改 / 看原文）
        if (_copyWhenReady)
        {
            _copyWhenReady = false;
            if (result.Ok && _model != null)
            {
                if (_model.AllText.Length > 0) Copy(_model.AllText, "识别文字");
                else ToastService.Show("没有识别到文字", "识别结果", 1600, ToastKind.warning);
            }
        }

        // 「识别并翻译」（照上游 captureAndTranslateText）：算完自己翻一遍，用户不用再点「翻译」
        bool translating = _translateWhenReady;
        if (_translateWhenReady)
        {
            _translateWhenReady = false;
            if (result.Ok && _model is { LineCount: > 0 }) _ = ToggleTranslationAsync();
            else ToastService.Show("没有识别到可翻译的文字", "识别结果", 1800, ToastKind.warning);
        }

        // 「识别后自动执行」（照上游 handleAutomaticTextRecognitionAction）：
        // 翻译入口短路自动动作（上游 m_ocrTranslateAfterRecognition 直接 return）。
        if (!translating) RunAutomaticAction();

        // 「显示原图」（照上游 show_original_image，默认关）：在**同一个窗口**里切到原图，
        // 不再另开一个「原文预览」小窗（用户报过「点文字识别弹出两个窗口」）。
        if (TextRecognitionStore.ShowOriginalPreview && _image != null) OriginalToggle.IsChecked = true;
    }

    /// <summary>
    /// 文字识别完成后自动执行（照上游 <c>screenshot/auto_execute_after_text_recognition</c> 六值）。
    /// </summary>
    /// <remarks>
    /// <c>quick_*</c> 只在「快速识别」入口生效（上游 <c>m_ocrFromQuickOcrAction</c>）；
    /// 「并结束截图」在本项目里 = 复制完关掉结果窗——覆盖层在识别开始前就已经关闭，
    /// 这里没有第二个「截图会话」可结束（写进 <c>docs/CAPTURE_PARITY.md</c> §5）。
    /// </remarks>
    private void RunAutomaticAction()
    {
        if (_autoActionDone) return;
        _autoActionDone = true;

        string action = ScreenshotBehavior.AutoExecuteAfterTextRecognition;
        if (action == ScreenshotBehavior.OcrActionNone) return;
        if (ScreenshotBehavior.IsQuickOnlyOcrAction(action) && !_quick) return;

        switch (action)
        {
            case ScreenshotBehavior.OcrActionCopy:
            case ScreenshotBehavior.OcrActionQuickCopy:
                CopyRecognizedText();
                break;

            case ScreenshotBehavior.OcrActionCopyAndEnd:
            case ScreenshotBehavior.OcrActionQuickCopyAndEnd:
                CopyRecognizedText();
                Close();
                break;

            case ScreenshotBehavior.OcrActionEditMode:
                EnterTextEditMode();
                break;
        }
    }

    /// <summary>把当前文字层的全文写进剪贴板（自动动作的「复制文本」）。</summary>
    private void CopyRecognizedText()
    {
        if (_model is not { } model) return;
        if (model.AllText.Length > 0) Copy(model.AllText, "识别文字");
        else ToastService.Show("没有识别到文字", "识别结果", 1600, ToastKind.warning);
    }

    /// <summary>
    /// 照上游 <c>beginTextEditing</c>：切到文字页、收起原图、焦点给文字层、光标落在第一行开头。
    /// </summary>
    private void EnterTextEditMode()
    {
        if (_tab != "text")
        {
            _tab = "text";
            TabText.IsChecked = true;
            UpdateTabs();
        }

        if (_model is { LineCount: > 0 } model && _translatedLines == null)
        {
            model.SetCaret(0, 0);
            OcrCanvas.Focus();
            Keyboard.Focus(OcrCanvas);
        }

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
            FillToggle is not { } fillToggle ||
            CopyBtn is not { } copyBtn || EmptyText is not { } emptyText)
        {
            return;
        }

        bool text = _tab == "text";
        bool conversion = _tab is "markdown" or "html";
        bool document = _tab == "document";
        bool latex = _tab == "latex";

        ocrScroll.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        TablePage.Visibility = _tab == "table" ? Visibility.Visible : Visibility.Collapsed;
        QrPage.Visibility = _tab == "qr" ? Visibility.Visible : Visibility.Collapsed;
        ConversionPage.Visibility = conversion ? Visibility.Visible : Visibility.Collapsed;
        DocumentPage.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
        LatexPage.Visibility = latex ? Visibility.Visible : Visibility.Collapsed;

        // LaTeX 源码跟着「当前的识别文字」走（用户在文字页改过字就重新挑公式）
        if (latex)
        {
            RefreshLatex();
            LatexView.Focus();
        }

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

        emptyText.Visibility = text && _layer.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        originalToggle.IsEnabled = text;
        fillToggle.IsEnabled = text;

        copyBtn.Content = _tab switch
        {
            "table" => "复制表格",
            "qr" => "复制二维码内容",
            "markdown" => "复制 Markdown",
            "html" => "复制 HTML",
            "latex" => "复制公式源码",
            "document" => "复制文档",
            _ => CopyLabelForText(),
        };

        copyBtn.IsEnabled = latex
            ? LatexView.Text.Length > 0
            : conversion || document
                ? !_conversion.IsEmpty
                : _result is { Ok: true } || _layer.Count > 0;

        // 翻译只服务文字页：别的页签把这一组收起（不放死按钮）
        UpdateTranslateBar();
        UpdateTransformBar();

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

        if (_tab == "latex")
        {
            if (LatexView.Text.Length == 0)
            {
                StatusText.Text = "LaTeX · 没有可整理的源码";
                return;
            }

            int chars = LatexView.SelectionLength;
            StatusText.Text = chars > 0
                ? string.Format(CultureInfo.CurrentCulture, "LaTeX · 已选中 {0} 个字符", chars)
                : "LaTeX · 只给源码（本地不渲染公式）· 可划选后复制";
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

    /// <summary>
    /// 「原文预览」（照上游 <c>text_recognition/show_original_image_preview</c> + <c>setOriginalImagePreviewEnabled</c>）：
    /// 勾上就在识别窗旁边开一个**独立小窗**显示原图，不是把识别窗自己的画布换成原图。
    /// </summary>
    private void Original_Changed(object sender, RoutedEventArgs e)
    {
        if (OriginalToggle.IsChecked == true) ShowOriginalPreview();
        else CloseOriginalPreview();
    }

    private CaptureOriginalPreviewWindow? _preview;

    private void ShowOriginalPreview()
    {
        if (_image == null) return;

        if (_preview == null)
        {
            _preview = new CaptureOriginalPreviewWindow(_image, _scale);
            _preview.Closed += (_, _) =>
            {
                _preview = null;
                if (OriginalToggle.IsChecked == true) OriginalToggle.IsChecked = false;
            };
            _preview.Show();
        }

        FollowOriginalPreview();
        UpdateTabs();
    }

    private void CloseOriginalPreview()
    {
        CaptureOriginalPreviewWindow? preview = _preview;
        _preview = null;
        if (preview == null) return;
        try { preview.Close(); }
        catch (InvalidOperationException) { /* 已经关掉 */ }
        UpdateTabs();
    }

    /// <summary>把原文小窗贴到识别窗旁边（右侧优先，放不下翻左侧 / 上下），并夹进工作区。</summary>
    private void FollowOriginalPreview()
    {
        if (_preview == null || !_preview.IsVisible) return;

        Rect host = new(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        _preview.PlaceBeside(host, WorkArea());
    }

    /// <summary>识别窗所在显示器的工作区（DIP）。</summary>
    private Rect WorkArea()
    {
        ScreenGeometry? screen = CaptureRecordWindow.ScreenOf(_area);
        double scale = screen?.Scale ?? _scale;
        if (scale <= 0) scale = 1;
        if (screen == null) return SystemParameters.WorkArea;

        return new Rect(screen.WorkLeft / scale, screen.WorkTop / scale,
            screen.WorkWidth / scale, screen.WorkHeight / scale);
    }

    private void Fill_Changed(object sender, RoutedEventArgs e)
    {
        if (OcrCanvas == null) return;
        OcrCanvas.SetFillBackground(FillToggle.IsChecked == true);
    }

    /// <summary>
    /// 「保存图片」：照上游 <c>text_recognition/save_recognition_result_as_image</c>——
    /// 打开时保存带上当前识别 / 译文结果的图，关掉时只存原图。
    /// </summary>
    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        BitmapSource? image = BuildSavableImage();
        if (image == null)
        {
            ToastService.Show("没有可保存的图片", "保存图片", 1600, ToastKind.warning);
            return;
        }

        CaptureSave.TrySave(this, image, CaptureSave.DefaultName, default,
            _area.Width > 0 && _area.Height > 0 ? _area : null);
    }

    /// <summary>保存 / 复制用的成品图（尊重「保存识别结果图片」开关）。</summary>
    /// <remarks>
    /// 识别结果图 = **原截图 + 原位替换的文字**（照上游 <c>renderScreenshotRecognitionImage</c>：
    /// 先把选区原图铺满，再按行把底色盖上去、把文字按原位置画回来）。所以这里必须
    /// <c>includeSource: true</c>——传 <c>false</c> 出来的是「只有文字、裁到文字外接框」的一张图，
    /// 保存下来根本不是截图（用户报过「保存的不是截图」）。
    /// </remarks>
    internal BitmapSource? BuildSavableImage()
    {
        if (_image == null) return null;
        if (!TextRecognitionStore.SaveResultAsImage) return _image;

        if (_translatedLines != null)
            return RenderTranslationImage() ?? _image;

        if (_model == null || _layer.Count == 0) return _image;

        return OcrImageRenderer.Render(_image, _layer, _model.Lines, includeSource: true,
                   fillBackground: FillToggle.IsChecked == true,
                   fillStyle: TextRecognitionStore.FillStyle,
                   highlight: TextRecognitionStore.ShowTextHighlighting)
               ?? _image;
    }

    // ============ 键盘 / 输入 ============

    private bool TextEditable => _tab == "text" &&
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

        if (_tab == "latex")
        {
            // 只读文本框：全选 / 复制由这里给命令（没焦点时 TextBox 自己的 Ctrl+A 不会生效）
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && e.Key == Key.A)
            {
                LatexView.Focus();
                LatexView.SelectAll();
                e.Handled = true;
                RefreshStatus();
            }
            else if (control && e.Key == Key.C)
            {
                CopyLatex();
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
            case "latex":
                CopyLatex();
                break;
            default:
                // 译文模式：复制整段译文（逐行）；开了「复制成图片」就把渲染好的图放进剪贴板
                if (_translatedLines != null)
                {
                    if (TranslationStore.CopyAsImage && RenderTranslationImage() is { } image)
                    {
                        if (NativeClipboard.SetImage(image))
                            ToastService.Show("已复制译文图", "识别结果", 1600);
                        else
                            ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0,
                                ToastKind.error);
                        break;
                    }

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
        CloseOriginalPreview();
    }
}
