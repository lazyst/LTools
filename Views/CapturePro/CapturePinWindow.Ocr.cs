using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图上的文字：两套形态共用一份识别结果。
/// </summary>
/// <remarks>
/// <para>
/// **文字层**（<c>show_text_recognition_results</c>，P4-j）：白纸页作底、每行按原图行框铺回去，
/// 可拖动选择、复制；上游是把识别内容窗按原图几何嵌进贴图并隐藏画布内容，LTools 用同一块
/// <see cref="CaptureOcrCanvas"/> 实现，坐标 = 源图像素 × (<c>_zoom / _scale</c>)。
/// </para>
/// <para>
/// **隐形文字层**（<c>text_selection_on_recognition_results = always</c>，P9-d2）：不换底、不画字，
/// 原图看起来一点没变，但文字能直接拖选 + 复制（照上游 <c>RenderingMode::SelectionOnly</c>）。
/// 两套形态共用同一份 <see cref="OcrTextEditModel"/>，切换时只是换画布的渲染模式。
/// </para>
/// <para>
/// **识别只跑一次**：贴图出现在屏幕上之后按 <see cref="PinSettings.AutomaticTextRecognition"/>
/// 静默预取（照上游 <c>automatic_text_recognition</c>），点菜单 / 划选都不必再等。
/// </para>
/// <para>
/// **鼠标可达的出口**：贴图不抢焦点（<c>WS_EX_NOACTIVATE</c>），键盘 Ctrl+C 走不到这里，
/// 所以复制放在图下方的状态条（文字层）、选区旁的小按钮（隐形层）与右键菜单上。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    private PinOcrPhase _ocrPhase = PinOcrPhase.Idle;
    private bool _ocrMode;
    private bool _ocrSubscribed;

    /// <summary>最近一次识别结果（退出文字层后仍留着，再点「显示文字」不必重跑）。</summary>
    private OcrTextEditModel? _ocrModel;
    private IReadOnlyList<OcrTextLine> _ocrLayer = Array.Empty<OcrTextLine>();
    private string _ocrText = "";

    /// <summary>文字层自己的平移（与 <c>PanTransform</c> 同步，但各自一份，别让两个元素共用一个变换）。</summary>
    private TranslateTransform? _ocrPan;

    /// <summary>识别轮次：退出 / 关窗 / 换图都会加一，在途结果回来时据此丢弃。</summary>
    private int _ocrRun;

    // —— 隐形文字层（识别结果可直接划选）——

    private bool _hiddenTextSelection;
    private int _autoRun;                    // 静默预取自己的轮次（与显式识别互不打断）
    private bool _autoOcrStarted;
    private string _hiddenText = "";         // 当前选中的文字（chip 显隐 / 冒烟断言用）

    // —— 快速文字标注（P9-d3，照上游 screenshotpinnedtextannotation.cpp）——

    /// <summary>标注文档（源文本一变就整体作废；贴回时按记录里的载荷恢复）。</summary>
    private PinTextAnnotationDocument? _annotations;

    /// <summary>贴回时带过来的载荷：等识别结果回来、算完指纹再校验。</summary>
    private string _annotationPending = "";

    /// <summary>上一次见到的文本签名（区分「只是选区变了」与「文字被改了」）。</summary>
    private string _annotationTextSignature = "";

    /// <summary>样式面板在编辑哪一种工具（<c>null</c> = 还没选过）。</summary>
    private PinTextAnnotationTool? _annotationTool;

    /// <summary>样式面板开着没有。</summary>
    private bool _annotationStyleOpen;

    /// <summary>冒烟夹具：给一份结果就不跑真 OCR（与 <see cref="CapturePinWindow.LiveFrame"/> 同一套做法）。</summary>
    internal RecognizeResult? OcrFixture = null;

    /// <summary>冒烟夹具：关掉自动识别（真跑 OCR 会让冒烟变慢、依赖系统语言包）。</summary>
    internal static bool AutoOcrEnabled = true;

    /// <summary>文字层开着没有（贴图右键菜单里叫「显示文字」）。</summary>
    internal bool OcrMode => _ocrMode;

    /// <summary>隐形文字层铺着没有（原图上能直接划选文字）。</summary>
    internal bool HiddenTextSelection => _hiddenTextSelection;

    /// <summary>隐形文字层当前选中的文字（空串 = 没选）。</summary>
    internal string HiddenSelectionText => _hiddenText;

    /// <summary>菜单 / 状态条用的阶段（超出识别上限时直接是「尺寸过大」）。</summary>
    internal PinOcrPhase OcrPhase =>
        OcrCanRecognize ? _ocrPhase : PinOcrPhase.TooLarge;

    /// <summary>这张图能不能识别：静态内容 + 不超过上游的像素上限。</summary>
    internal bool OcrCanRecognize =>
        !_live && PinOcr.WithinPixelLimit(_content.PixelWidth, _content.PixelHeight);

    /// <summary>最近一次识别出来的全文（空串 = 还没识别 / 没找到）。</summary>
    internal string OcrText => _ocrText;

    /// <summary>文字层里的行数（0 = 没有文字层）。</summary>
    internal int OcrLineCount => _ocrModel?.LineCount ?? 0;

    /// <summary>快速文字标注的载荷（空串 = 没有标注；冒烟断言 / 历史落盘用）。</summary>
    internal string AnnotationsPayload => _annotations?.ToPayload() ?? "";

    /// <summary>标注文档（冒烟直接查 run / 样式）。</summary>
    internal PinTextAnnotationDocument? Annotations => _annotations;

    /// <summary>当前选区按行拆成的源文本区间（冒烟断言 / 工具栏用）。</summary>
    internal IReadOnlyList<PinTextRange> AnnotationSelection => OcrCanvas.SelectionRanges();

    /// <summary>样式面板正在编辑的工具（<c>null</c> = 没开）。</summary>
    internal PinTextAnnotationTool? AnnotationTool => _annotationTool;

    // ============ 自动识别（静默预取） ============

    /// <summary>
    /// 贴图出现在屏幕上之后跑一次静默识别（照上游 <c>automatic_text_recognition</c>）。
    /// 只在设置开着、尺寸没过上限、还没有结果时跑；结果回来铺隐形文字层（设置允许时）。
    /// </summary>
    internal void StartAutoRecognize()
    {
        if (_autoOcrStarted) return;
        if (!AutoOcrEnabled || !PinSettings.AutomaticTextRecognition) return;   // 关着：等设置打开再来一次
        if (!OcrCanRecognize) return;

        _autoOcrStarted = true;

        if (_ocrModel != null && _ocrPhase == PinOcrPhase.Ready)
        {
            SyncHiddenTextLayer();
            return;
        }

        _ = RecognizeAsync(silent: true);
    }

    // ============ 文字层开关 ============

    /// <summary>菜单 / 指针动作 / 快捷键的入口：没铺文字层就识别，铺着就退出。</summary>
    internal void ToggleOcrMode()
    {
        if (_ocrMode)
        {
            ExitOcrMode();
            return;
        }

        EnterOcrMode();
    }

    /// <summary>
    /// 进入文字层：照上游 <c>activateRecognitionMode</c>，先退出点击穿透 / 收顶部 / 缩略图，
    /// 再识别（有缓存就直接铺）。
    /// </summary>
    internal void EnterOcrMode()
    {
        if (_live) return;                      // 实时贴图的内容每 200ms 换一次，识别了也留不住
        if (!OcrCanRecognize)
        {
            ToastService.Show($"图片尺寸过大，最多 {PinOcr.MaxPixels / 1_000_000} 百万像素",
                "贴图文字", 3200, ToastKind.warning, key: "pin-ocr");
            return;
        }

        if (_clickThrough) SetClickThrough(false);
        ExitHideToTop();
        if (_thumb) SetThumbnail(false);

        HideHiddenTextLayer();                  // 文字层接管这块画布
        _ocrMode = true;
        EnsureOcrSubscribed();
        ApplyOcrChrome();

        if (_ocrModel != null && _ocrPhase == PinOcrPhase.Ready)
        {
            ShowOcrLayer();
            RefreshOcrBar();
            return;
        }

        _ = RecognizeAsync();
    }

    /// <summary>退出文字层（窗口回到可拖动 / 可缩放的贴图）。识别结果留着，下次秒开。</summary>
    internal void ExitOcrMode()
    {
        if (!_ocrMode) return;

        _ocrMode = false;
        _ocrRun++;                              // 在途识别结果作废
        if (_ocrPhase == PinOcrPhase.Busy) _ocrPhase = PinOcrPhase.Idle;

        OcrCanvas.Visibility = Visibility.Collapsed;
        OcrCanvas.SetSelectionOnly(false);
        OcrBar.Visibility = Visibility.Collapsed;

        ApplyCursor();
        RefreshHoverChrome();
        SyncHiddenTextLayer();                  // 有结果就换回隐形层（设置允许时）
    }

    /// <summary>换图之后文字层对不上了：退出并把缓存清干净（标注 / 拖放替换都要调）。</summary>
    private void ResetOcrCache()
    {
        ExitOcrMode();
        HideHiddenTextLayer();
        _annotationPending = "";                // 换图 = 换了源，贴回载荷也作废
        ClearAnnotations(persist: true);        // 源文本没了，标注也留不住（照上游 clearTextAnnotations）
        _ocrPhase = PinOcrPhase.Idle;
        _ocrModel = null;
        _ocrLayer = Array.Empty<OcrTextLine>();
        _ocrText = "";
        OcrCanvas.Visibility = Visibility.Collapsed;
        OcrCanvas.SetSelectionOnly(false);
        OcrBar.Visibility = Visibility.Collapsed;
    }

    // ============ 识别 ============

    /// <summary>后台跑一次识别（用的是「新建 OCR 取值」同一套引擎偏好）。</summary>
    /// <param name="silent">
    /// 静默预取：不显示「正在识别…」、不弹空结果提示，结果只用来铺隐形文字层。
    /// </param>
    private async Task RecognizeAsync(bool silent = false)
    {
        int run = silent ? ++_autoRun : ++_ocrRun;
        if (!silent)
        {
            _ocrPhase = PinOcrPhase.Busy;
            RefreshOcrBar();
        }

        var image = _content;
        string? engine = OcrEnginePreference.DefaultForNewProbe;
        var fixture = OcrFixture;

        RecognizeResult result;
        try
        {
            result = fixture ?? await Task.Run(() => CaptureRecognize.Run(image, engine));
        }
        catch (Exception ex)
        {
            // CaptureRecognize 自己说不抛，这里只是兜底：把后台异常变成一条状态，不炸窗口
            CrashLog.Write("CapturePro.PinOcr", ex);
            if (silent ? run != _autoRun : run != _ocrRun) return;
            if (!silent && _ocrMode)
            {
                _ocrPhase = PinOcrPhase.Empty;
                ToastService.Show("文字识别失败：" + ex.Message, "贴图文字", 3600, ToastKind.error,
                    key: "pin-ocr");
                RefreshOcrBar();
            }

            return;
        }

        if (silent ? run != _autoRun : run != _ocrRun) return;
        if (!IsVisible) return;
        if (!silent && !_ocrMode) return;

        StoreOcrResult(result, silent);
    }

    /// <summary>
    /// 把一份识别结果铺到贴图上（识别线程回来时调；冒烟直接灌结果，不跑真 OCR）。
    /// </summary>
    internal void ApplyOcrResult(RecognizeResult result)
    {
        if (!_ocrMode) return;
        StoreOcrResult(result, silent: false);
    }

    /// <summary>把一份识别结果只铺进隐形文字层（冒烟夹具用：不经过异步）。</summary>
    internal void ApplyHiddenOcrResult(RecognizeResult result) => StoreOcrResult(result, silent: true);

    private void StoreOcrResult(RecognizeResult result, bool silent)
    {
        var layer = result.TextLayer.Count > 0 ? result.TextLayer : OcrTextLayer.FromLines(result.Lines);
        if (layer.Count == 0)
        {
            _ocrModel = null;
            _ocrLayer = Array.Empty<OcrTextLine>();
            _ocrText = "";
            ClearAnnotations(persist: true);    // 没有文字就没有标注

            if (silent)
            {
                SyncHiddenTextLayer();
                return;
            }

            _ocrPhase = PinOcrPhase.Empty;
            OcrCanvas.Visibility = Visibility.Collapsed;
            ToastService.Show(PinOcr.EmptyHint, "贴图文字", 2600, ToastKind.warning, key: "pin-ocr");
            RefreshOcrBar();
            return;
        }

        if (_ocrModel != null) _ocrModel.Changed -= OnOcrModelChanged;
        var model = new OcrTextEditModel();
        model.Load(layer.Select(line => line.Text));
        model.Changed += OnOcrModelChanged;

        _ocrModel = model;
        _ocrLayer = layer;
        _ocrText = model.AllText;
        _ocrPhase = PinOcrPhase.Ready;

        BindAnnotations();                      // 源文本到位：绑定 / 校验标注（贴回时按指纹恢复）

        if (silent)
        {
            SyncHiddenTextLayer();
            return;
        }

        ShowOcrLayer();
        RefreshOcrBar();
    }

    /// <summary>把缓存的结果铺成文字层（白纸底 + 每行文字 + 状态条）。</summary>
    private void ShowOcrLayer()
    {
        if (_ocrModel == null || _ocrLayer.Count == 0) return;

        OcrCanvas.SetSelectionOnly(false);
        OcrCanvas.Attach(_ocrModel, _ocrLayer, _content.PixelWidth, _content.PixelHeight);
        OcrCanvas.SetSourceImage(_content);
        OcrCanvas.SetFillBackground(true);
        OcrCanvas.SetFillStyle(TextRecognitionStore.FillStyle);
        OcrCanvas.SetHighlightText(TextRecognitionStore.ShowTextHighlighting);
        OcrCanvas.SetAnnotations(_annotations);
        ApplyOcrGeometry();
        OcrCanvas.Visibility = Visibility.Visible;
        OcrCanvas.InvalidateVisual();
    }

    // ============ 隐形文字层 ============

    /// <summary>
    /// 按设置铺 / 收隐形文字层（照上游 <c>synchronizeHiddenTextSelection</c>）：
    /// 只有「始终可划选」+ 不在文字层 / 缩略图 / 取景框 / 实时贴图里 + 已有结果时才铺。
    /// </summary>
    /// <remarks>
    /// 有标注时即使「可划选」关着也要铺：标注画在这层上（照上游：标注与划选是两种用途，
    /// 关掉划选不该把已经画好的标注藏起来）。那种情况下画布不接鼠标，点击照旧拖动窗口。
    /// </remarks>
    internal void SyncHiddenTextLayer()
    {
        bool selectable = PinSettings.SelectsOnImage;
        bool hasAnnotations = _annotations is { Empty: false };

        bool enabled = (selectable || hasAnnotations) && !_ocrMode && !_thumb && !_live && !_viewport &&
                       IsVisible && _ocrModel != null && _ocrPhase == PinOcrPhase.Ready &&
                       _ocrLayer.Count > 0;

        if (!enabled)
        {
            HideHiddenTextLayer();
            return;
        }

        EnsureOcrSubscribed();

        if (!_hiddenTextSelection)
        {
            _hiddenTextSelection = true;
            OcrCanvas.Attach(_ocrModel!, _ocrLayer, _content.PixelWidth, _content.PixelHeight);
            OcrCanvas.SetFillBackground(false);
            OcrCanvas.SetSelectionOnly(true);
            OcrCanvas.SetAnnotations(_annotations);
            OcrCanvas.Visibility = Visibility.Visible;
        }

        OcrCanvas.IsHitTestVisible = selectable;   // 不能划选时把点击让给窗口（拖动 / 缩放）
        ApplyOcrGeometry();
        RefreshHiddenBar();
    }

    /// <summary>收起隐形文字层（不清识别结果）。</summary>
    private void HideHiddenTextLayer()
    {
        _annotationStyleOpen = false;
        HiddenStylePanel.Visibility = Visibility.Collapsed;

        if (!_hiddenTextSelection)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
            return;
        }

        _hiddenTextSelection = false;
        _hiddenText = "";
        _ocrModel?.ClearSelection();
        HiddenBar.Visibility = Visibility.Collapsed;
        OcrCanvas.IsHitTestVisible = true;
        OcrCanvas.SetSelectionOnly(false);
        OcrCanvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 选区变了 / 几何变了：把工具栏摆到选区右下（放不下翻上方），并按设置决定
    /// 「复制」之外那四个标注开关显不显示（照上游 <c>screenshotquicktextannotationtoolbar</c>）。
    /// </summary>
    private void RefreshHiddenBar()
    {
        if (!_hiddenTextSelection)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
            HiddenStylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _hiddenText = _ocrModel?.SelectionText() ?? "";
        Rect? bounds = _hiddenText.Length > 0 ? OcrCanvas.SelectionBounds() : null;

        // 四个标注开关只在「隐形层可划选 + 设置开着 + 已经有源文本」时出现
        bool annotate = bounds != null && _annotations != null && PinSettings.SelectsOnImage &&
                        PinSettings.QuickAnnotationForSelectedText;

        SetAnnotationButtonsVisible(annotate);
        if (annotate) SyncAnnotationButtons();
        else _annotationStyleOpen = false;

        HiddenBarText.Text = _hiddenText.Length > 0 ? $"已选 {_hiddenText.Length} 字" : "已选文字";

        if (bounds == null)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
            HiddenStylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        HiddenBar.Visibility = Visibility.Visible;
        HiddenBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = HiddenBar.DesiredSize;

        Point topLeft = OcrCanvas.TranslatePoint(new Point(bounds.Value.X, bounds.Value.Y), this);
        Point bottomRight = OcrCanvas.TranslatePoint(new Point(bounds.Value.Right, bounds.Value.Bottom), this);

        double x = bottomRight.X - size.Width;
        double y = bottomRight.Y + 6;
        if (y + size.Height > Height - 6) y = topLeft.Y - size.Height - 6;   // 下方放不下翻上方

        x = Math.Clamp(x, 6, Math.Max(6, Width - size.Width - 6));
        y = Math.Clamp(y, 6, Math.Max(6, Height - size.Height - 6));
        System.Windows.Controls.Canvas.SetLeft(HiddenBar, x);
        System.Windows.Controls.Canvas.SetTop(HiddenBar, y);

        HiddenStylePanel.Visibility = annotate && _annotationStyleOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (HiddenStylePanel.Visibility == Visibility.Visible) PositionAnnotationStylePanel();
    }

    /// <summary>样式面板贴在工具栏上方（上方放不下翻下方），左右跟着工具栏夹进窗口。</summary>
    private void PositionAnnotationStylePanel()
    {
        HiddenStylePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = HiddenStylePanel.DesiredSize;

        double barX = System.Windows.Controls.Canvas.GetLeft(HiddenBar);
        double barY = System.Windows.Controls.Canvas.GetTop(HiddenBar);
        double barHeight = HiddenBar.ActualHeight > 0 ? HiddenBar.ActualHeight : HiddenBar.DesiredSize.Height;

        double x = Math.Clamp(barX, 6, Math.Max(6, Width - size.Width - 6));
        double y = barY - size.Height - 4;
        if (y < 6) y = barY + barHeight + 4;
        y = Math.Clamp(y, 6, Math.Max(6, Height - size.Height - 6));

        System.Windows.Controls.Canvas.SetLeft(HiddenStylePanel, x);
        System.Windows.Controls.Canvas.SetTop(HiddenStylePanel, y);
    }

    /// <summary>隐形文字层上按下去的位置是不是文字（是就让给划选，不拖动窗口）。</summary>
    private bool HiddenTextHit(Point canvasPoint) =>
        _hiddenTextSelection && OcrCanvas.HitTest(canvasPoint).Line >= 0;

    /// <summary>复制隐形文字层里选中的那段（chip 按钮 / 右键菜单）。</summary>
    internal void CopyHiddenSelection()
    {
        string text = _ocrModel?.SelectionText() ?? "";
        if (text.Length == 0) return;

        NativeClipboard.SetText(text);
        ToastService.Show($"已复制 {text.Length} 字", "贴图文字", 1800, ToastKind.success,
            key: "pin-ocr-copy");
    }

    /// <summary>取消隐形文字层的选区（chip 上的 ✕）。</summary>
    internal void ClearHiddenSelection()
    {
        _ocrModel?.ClearSelection();
        RefreshHiddenBar();
    }

    // ============ 快速文字标注（P9-d3） ============

    /// <summary>
    /// 源文本到位 / 变了：绑定标注文档并按指纹校验（照上游 <c>synchronizeTextAnnotations</c>）。
    /// 贴回时带过来的载荷在这里才落地——指纹对不上就整份丢掉，绝不画到别的字上。
    /// </summary>
    private void BindAnnotations()
    {
        if (_ocrModel == null || _ocrLayer.Count == 0) return;

        _annotations ??= new PinTextAnnotationDocument();
        OcrCanvas.SetAnnotations(_annotations);

        _annotationTextSignature = TextSignature(_ocrModel);
        string fingerprint = PinTextAnnotationDocument.FingerprintOf(_ocrLayer, _ocrModel.Lines);

        if (_annotationPending.Length > 0)
        {
            string payload = _annotationPending;
            _annotationPending = "";
            _annotations.Restore(payload);
        }

        _annotations.BindSource(PinTextAnnotationDocument.SourceOcr, fingerprint);
        OcrCanvas.InvalidateAnnotations();
    }

    /// <summary>文本签名（每行原文拼起来）：只用来判断「文字有没有被改」。</summary>
    private static string TextSignature(OcrTextEditModel model) => string.Join("\n", model.Lines);

    /// <summary>
    /// 贴回时把记录里的标注载荷交给这张贴图（识别结果回来后才按指纹校验，见 <see cref="BindAnnotations"/>）。
    /// </summary>
    internal void RestoreAnnotations(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return;

        _annotationPending = payload;
        _annotations ??= new PinTextAnnotationDocument();
        OcrCanvas.SetAnnotations(_annotations);
        if (_ocrModel != null && _ocrLayer.Count > 0) BindAnnotations();
    }

    /// <summary>
    /// 丢掉全部标注（换图 / 文字被改）；<paramref name="persist"/> = 同步写回历史记录。
    /// </summary>
    /// <remarks>
    /// **不动 <see cref="_annotationPending"/>**：贴回时「先交载荷、后出识别结果」，
    /// 这中间如果来一份空结果（还没识别出文字 / 识别失败），把待校验的载荷一起清掉，
    /// 等真正的结果回来就再也恢复不了。载荷只在「换了图」（<see cref="ResetOcrCache"/>）或
    /// 「真的校验过」（<see cref="BindAnnotations"/>）时作废。
    /// </remarks>
    private void ClearAnnotations(bool persist)
    {
        _annotationTextSignature = "";
        _annotationStyleOpen = false;
        _annotationTool = null;
        HiddenStylePanel.Visibility = Visibility.Collapsed;

        if (_annotations == null || _annotations.Empty) return;

        _annotations.Clear();
        OcrCanvas.InvalidateAnnotations();
        if (persist) PersistAnnotations();
    }

    /// <summary>把标注载荷写回贴图历史（没记历史的贴图 / 实时贴图不写）。</summary>
    private void PersistAnnotations()
    {
        if (PinId.Length == 0 || _live) return;
        PinStore.UpdateAnnotations(PinId, _annotations?.ToPayload() ?? "");
    }

    /// <summary>
    /// 给当前选区切换一种标注（工具栏按钮）。返回是否真的改了内容。
    /// 冒烟直接调它，不用伪造鼠标事件。
    /// </summary>
    internal bool AnnotateSelection(PinTextAnnotationTool tool)
    {
        if (_annotations == null || _ocrModel == null) return false;

        IReadOnlyList<PinTextRange> selection = OcrCanvas.SelectionRanges();
        if (selection.Count == 0) return false;

        PinTextAnnotationStyle style = PinSettings.AnnotationStyle(tool);
        if (!_annotations.Toggle(selection, tool, style)) return false;

        _annotationTool = tool;
        OcrCanvas.InvalidateAnnotations();
        PersistAnnotations();
        SyncAnnotationButtons();
        RefreshHiddenBar();
        SyncHiddenTextLayer();                 // 只有标注、不能划选时也要把画布铺出来
        return true;
    }

    /// <summary>选区里这一种工具是不是整段都已有标注（按钮的激活态）。</summary>
    internal bool AnnotationActive(PinTextAnnotationTool tool) =>
        _annotations?.HasStyle(OcrCanvas.SelectionRanges(), tool, requireFull: true) == true;

    /// <summary>这一种工具当前的样式（样式面板 / 冒烟断言用）。</summary>
    internal PinTextAnnotationStyle AnnotationStyleOf(PinTextAnnotationTool tool) =>
        PinSettings.AnnotationStyle(tool);

    /// <summary>改一种工具的样式，并把选区里已有的同类标注一起改掉（照上游 <c>updateStyle</c>）。</summary>
    internal void SetAnnotationStyle(PinTextAnnotationTool tool, PinTextAnnotationStyle style,
        int properties = PinTextAnnotationDocument.PropertyAll)
    {
        PinSettings.SetAnnotationStyle(tool, style);
        _annotationTool = tool;

        if (_annotations != null && _annotations.UpdateStyle(OcrCanvas.SelectionRanges(), tool, style, properties))
        {
            OcrCanvas.InvalidateAnnotations();
            PersistAnnotations();
        }

        if (_annotationStyleOpen && _annotationTool == tool) BuildAnnotationStylePanel(tool);
        SyncAnnotationButtons();
    }

    private IEnumerable<(Button Button, PinTextAnnotationTool Tool)> AnnotationButtons()
    {
        yield return (HiddenHighlightBtn, PinTextAnnotationTool.Highlight);
        yield return (HiddenWavyBtn, PinTextAnnotationTool.WavyUnderline);
        yield return (HiddenUnderlineBtn, PinTextAnnotationTool.Underline);
        yield return (HiddenStrikeBtn, PinTextAnnotationTool.Strikethrough);
    }

    private void SetAnnotationButtonsVisible(bool on)
    {
        Visibility visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, _) in AnnotationButtons()) button.Visibility = visibility;
        HiddenStyleBtn.Visibility = visibility;
    }

    /// <summary>按选区回显四个按钮的激活态（<c>Tag="Active"</c> 走 <c>PinBarBtn</c> 的模板触发器）。</summary>
    private void SyncAnnotationButtons()
    {
        IReadOnlyList<PinTextRange> selection = OcrCanvas.SelectionRanges();
        foreach (var (button, tool) in AnnotationButtons())
        {
            bool active = _annotations?.HasStyle(selection, tool, requireFull: true) == true;
            button.Tag = active ? "Active" : null;
        }
    }

    /// <summary>样式面板的内容（高亮只有颜色；三种线标注再加线宽 / 线型 / 笔触）。</summary>
    private void BuildAnnotationStylePanel(PinTextAnnotationTool tool)
    {
        PinTextAnnotationStyle style = PinSettings.AnnotationStyle(tool);
        var body = HiddenStyleBody;
        body.Children.Clear();

        body.Children.Add(new TextBlock
        {
            Text = $"{PinSettings.AnnotationToolLabel(tool)}样式",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var colors = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        ColorPresetFamily family = tool == PinTextAnnotationTool.Highlight
            ? ColorPresetFamily.Highlight
            : ColorPresetFamily.Stroke;
        foreach (Color color in ColorPresets.Get(family))
        {
            Color picked = color;
            var border = new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
            };
            var swatch = new Button
            {
                Style = (Style)FindResource("PinSwatchBtn"),
                Content = border,
                Tag = SameColor(color, style.Color) ? "Active" : null,
                ToolTip = ColorPresets.Format(color),
            };
            swatch.Click += (_, _) => SetAnnotationStyle(tool,
                PinSettings.AnnotationStyle(tool) with { Color = picked },
                PinTextAnnotationDocument.PropertyColor);
            colors.Children.Add(swatch);
        }

        body.Children.Add(colors);

        if (tool != PinTextAnnotationTool.Highlight)
        {
            body.Children.Add(OptionRow("线宽", new (string Label, bool Active, Action Apply)[]
            {
                WidthOption(1, style, tool), WidthOption(2, style, tool), WidthOption(3, style, tool),
                WidthOption(4, style, tool), WidthOption(6, style, tool), WidthOption(8, style, tool),
            }));

            body.Children.Add(OptionRow("线型", new (string, bool, Action)[]
            {
                StrokeOption("实线", StrokeKind.Solid, style, tool),
                StrokeOption("虚线", StrokeKind.Dashed, style, tool),
                StrokeOption("点线", StrokeKind.Dotted, style, tool),
            }));

            body.Children.Add(OptionRow("笔触", new (string, bool, Action)[]
            {
                RenderingOption("干净", StrokeRendering.Architect, style, tool),
                RenderingOption("手绘", StrokeRendering.Artist, style, tool),
                RenderingOption("涂鸦", StrokeRendering.Cartoonist, style, tool),
            }));
        }
    }

    private (string, bool, Action) WidthOption(double width, PinTextAnnotationStyle style,
        PinTextAnnotationTool tool) =>
        ($"{width:0}", Math.Abs(style.Width - width) < 0.01,
            () => SetAnnotationStyle(tool,
                PinSettings.AnnotationStyle(tool) with { Width = width },
                PinTextAnnotationDocument.PropertyWidth));

    private (string, bool, Action) StrokeOption(string label, StrokeKind kind,
        PinTextAnnotationStyle style, PinTextAnnotationTool tool) =>
        (label, style.StrokeStyle == kind,
            () => SetAnnotationStyle(tool, PinSettings.AnnotationStyle(tool) with { StrokeStyle = kind },
                PinTextAnnotationDocument.PropertyStrokeStyle));

    private (string, bool, Action) RenderingOption(string label, StrokeRendering rendering,
        PinTextAnnotationStyle style, PinTextAnnotationTool tool) =>
        (label, style.RenderingStyle == rendering,
            () => SetAnnotationStyle(tool, PinSettings.AnnotationStyle(tool) with { RenderingStyle = rendering },
                PinTextAnnotationDocument.PropertyRenderingStyle));

    private FrameworkElement OptionRow(string label, (string Label, bool Active, Action Apply)[] options)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Width = 32,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAA)),
        });

        foreach (var (text, active, apply) in options)
        {
            var button = new Button
            {
                Style = (Style)FindResource("PinBarBtn"),
                Content = text,
                Width = 32,
                Padding = new Thickness(0),
                Tag = active ? "Active" : null,
                Margin = new Thickness(0, 0, 2, 0),
            };
            button.Click += (_, _) => apply();
            row.Children.Add(button);
        }

        return row;
    }

    private static bool SameColor(Color left, Color right) =>
        left.R == right.R && left.G == right.G && left.B == right.B && left.A == right.A;

    // ============ 界面 ============

    private void EnsureOcrSubscribed()
    {
        if (_ocrSubscribed) return;
        _ocrSubscribed = true;
        OcrCanvas.SelectionChanged += OnOcrSelectionChanged;
    }

    private void OnOcrSelectionChanged()
    {
        RefreshOcrBar();
        RefreshHiddenBar();
    }

    private void OnOcrModelChanged()
    {
        // 选区变化与文字改写都会走到这里：只有文字真的变了才丢标注（照上游 contentsChanged）。
        if (_ocrModel != null)
        {
            string signature = TextSignature(_ocrModel);
            if (signature != _annotationTextSignature)
            {
                _annotationTextSignature = signature;
                if (_annotations is { Empty: false }) ClearAnnotations(persist: true);
                BindAnnotations();              // 指纹跟着新文本走（同一份源重复绑定是空操作）
            }
        }

        RefreshOcrBar();
        RefreshHiddenBar();
    }

    /// <summary>文字层的显隐与状态条（阶段一变就调）。</summary>
    private void ApplyOcrChrome()
    {
        OcrCanvas.Visibility = _ocrMode && _ocrModel != null ? Visibility.Visible : Visibility.Collapsed;
        OcrBar.Visibility = _ocrMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshOcrBar();
    }

    /// <summary>
    /// 文字层 / 隐形层跟着贴图的缩放与平移走（同一套几何：源图像素 × _zoom ÷ _scale）。
    /// </summary>
    private void ApplyOcrGeometry()
    {
        if (!_ocrMode && !_hiddenTextSelection) return;

        // 位置 = 与图片相同的偏移；缩放 = 物理像素 → DIP（图本身是按 _baseWidth = 像素 ÷ _scale 铺的）
        _ocrPan ??= new TranslateTransform();
        _ocrPan.X = _pan.X;
        _ocrPan.Y = _pan.Y;
        OcrCanvas.RenderTransform = _ocrPan;
        OcrCanvas.SetZoom(_zoom / Math.Max(0.01, _scale));

        if (_hiddenTextSelection) RefreshHiddenBar();
    }

    private void RefreshOcrBar()
    {
        if (!_ocrMode)
        {
            OcrBar.Visibility = Visibility.Collapsed;
            return;
        }

        // 条比贴图还宽就会溢出被裁掉：限宽 + 自动换行（贴图常比这条状态条窄）
        OcrBar.MaxWidth = Math.Max(140, Width - 12);

        int selected = _ocrModel?.SelectionText().Length ?? 0;
        OcrBarText.Text = PinOcr.BarText(OcrPhase, OcrLineCount, selected);
        OcrCopySelectionBtn.IsEnabled = _ocrModel is { HasSelection: true };
        OcrCopyAllBtn.IsEnabled = _ocrText.Length > 0;
        OcrBar.Visibility = Visibility.Visible;
    }

    // ============ 复制 ============

    /// <summary>复制拖选中的文字（右键菜单与状态条共用）。</summary>
    private void CopyOcrSelection()
    {
        string text = _ocrModel?.SelectionText() ?? "";
        if (text.Length == 0) return;

        NativeClipboard.SetText(text);
        ToastService.Show($"已复制 {text.Length} 字", "贴图文字", 1800, ToastKind.success,
            key: "pin-ocr-copy");
    }

    /// <summary>复制识别出的全部文字。</summary>
    private void CopyOcrAll()
    {
        if (_ocrText.Length == 0) return;

        NativeClipboard.SetText(_ocrText);
        ToastService.Show($"已复制全文（{_ocrText.Length} 字）", "贴图文字", 1800, ToastKind.success,
            key: "pin-ocr-copy");
    }

    private void OcrCopySelection_Click(object sender, RoutedEventArgs e) => CopyOcrSelection();

    private void OcrCopyAll_Click(object sender, RoutedEventArgs e) => CopyOcrAll();

    private void OcrExit_Click(object sender, RoutedEventArgs e) => ExitOcrMode();

    private void HiddenCopy_Click(object sender, RoutedEventArgs e) => CopyHiddenSelection();

    private void HiddenClear_Click(object sender, RoutedEventArgs e) => ClearHiddenSelection();

    /// <summary>四个标注开关：整段已有就整段去掉，否则整段加上（照上游 <c>toggle</c>）。</summary>
    private void AnnotateTool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;
        AnnotateSelection(PinSettings.AnnotationToolOf(key));
    }

    /// <summary>「样式」：给最近用过的那种工具开 / 关样式面板（还没用过 = 高亮）。</summary>
    private void AnnotateStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_annotations == null) return;

        if (_annotationStyleOpen)
        {
            _annotationStyleOpen = false;
            HiddenStylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _annotationTool ??= PinTextAnnotationTool.Highlight;
        _annotationStyleOpen = true;
        BuildAnnotationStylePanel(_annotationTool.Value);
        RefreshHiddenBar();
    }

    /// <summary>关窗前把在途识别作废（手柄 / 状态条都是本窗口的控件，不用单独收）。</summary>
    private void ShutdownOcr()
    {
        _ocrRun++;
        _autoRun++;
        _ocrMode = false;
        _hiddenTextSelection = false;
    }
}
