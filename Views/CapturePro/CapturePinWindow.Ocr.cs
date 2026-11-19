using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
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
        OcrCanvas.SetFillBackground(true);
        ApplyOcrGeometry();
        OcrCanvas.Visibility = Visibility.Visible;
        OcrCanvas.InvalidateVisual();
    }

    // ============ 隐形文字层 ============

    /// <summary>
    /// 按设置铺 / 收隐形文字层（照上游 <c>synchronizeHiddenTextSelection</c>）：
    /// 只有「始终可划选」+ 不在文字层 / 缩略图 / 取景框 / 实时贴图里 + 已有结果时才铺。
    /// </summary>
    internal void SyncHiddenTextLayer()
    {
        bool enabled = PinSettings.SelectsOnImage && !_ocrMode && !_thumb && !_live && !_viewport &&
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
            OcrCanvas.Visibility = Visibility.Visible;
        }

        ApplyOcrGeometry();
        RefreshHiddenBar();
    }

    /// <summary>收起隐形文字层（不清识别结果）。</summary>
    private void HideHiddenTextLayer()
    {
        if (!_hiddenTextSelection)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
            return;
        }

        _hiddenTextSelection = false;
        _hiddenText = "";
        _ocrModel?.ClearSelection();
        HiddenBar.Visibility = Visibility.Collapsed;
        OcrCanvas.SetSelectionOnly(false);
        OcrCanvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>选区变了 / 几何变了：把「复制」小按钮摆到选区右下（放不下翻上方）。</summary>
    private void RefreshHiddenBar()
    {
        if (!_hiddenTextSelection)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
            return;
        }

        _hiddenText = _ocrModel?.SelectionText() ?? "";
        Rect? bounds = _hiddenText.Length > 0 ? OcrCanvas.SelectionBounds() : null;
        if (bounds == null)
        {
            HiddenBar.Visibility = Visibility.Collapsed;
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

    /// <summary>关窗前把在途识别作废（手柄 / 状态条都是本窗口的控件，不用单独收）。</summary>
    private void ShutdownOcr()
    {
        _ocrRun++;
        _autoRun++;
        _ocrMode = false;
        _hiddenTextSelection = false;
    }
}
