using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
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
        WirePreview();
        Place();
        Loaded += (_, _) =>
        {
            UpdateZoom();
            Start();
            if (_previewPending)
            {
                _previewPending = false;
                OpenPreview();
            }
        };
        SizeChanged += (_, _) => { UpdateZoom(); FollowOriginalPreview(); };
        LocationChanged += (_, _) => FollowOriginalPreview();
        PreviewKeyDown += OnPreviewKeyDown;
        TextInput += OnTextInput;

        // 从任务栏回来时可能已经落在屏幕外（换过分辨率 / 显示器）：夹回工作区
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal) EnsureOnScreen();
        };
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
        WirePreview();
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

}
