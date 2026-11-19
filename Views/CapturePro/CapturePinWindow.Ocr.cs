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
/// 贴图上的「显示文字识别结果」（P4-j，照上游 <c>show_text_recognition_results</c>）：
/// 在这张图上跑一次 OCR，把文字按原图行框铺回去，可以拖动选择、复制。
/// </summary>
/// <remarks>
/// <para>
/// 上游的做法是把识别内容窗（<c>ScreenshotRecognitionWindow</c>）按原图几何嵌进贴图，并隐藏画布内容；
/// LTools 用同一块 <see cref="CaptureOcrCanvas"/> 实现：**白纸页作底**（上游识别内容页也是容器底色，
/// 不画原图），再叠每行估出来的底色与文字，坐标 = 源图像素 × (<c>_zoom / _scale</c>)，
/// 与贴图内容严丝合缝。退出文字层就回到原图。
/// </para>
/// <para>
/// **OCR 模式里拖动 / 滚轮缩放 / 收顶部都先退出或让路**（照上游 <c>m_ocrMode</c> 对
/// <c>windowDragEligible</c> 与 <c>applyWheelScale</c> 的门禁）：拖动留给文字层划选，
/// 滚轮不该把图缩掉——否则刚选中的字会跑位。
/// </para>
/// <para>
/// **鼠标可达的出口**：贴图不抢焦点（<c>WS_EX_NOACTIVATE</c>），键盘 Ctrl+C 走不到这里，
/// 所以复制放在图下方的状态条与右键菜单上（「复制所选文字」「复制全部文字」）。
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

    /// <summary>冒烟夹具：给一份结果就不跑真 OCR（与 <see cref="CapturePinWindow.LiveFrame"/> 同一套做法）。</summary>
    internal RecognizeResult? OcrFixture = null;

    /// <summary>文字层开着没有（贴图右键菜单里叫「显示文字」）。</summary>
    internal bool OcrMode => _ocrMode;

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

    // ============ 开关 ============

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

        _ocrMode = true;
        EnsureOcrSubscribed();
        ApplyOcrChrome();

        if (_ocrModel != null && _ocrPhase == PinOcrPhase.Ready)
        {
            OcrCanvas.Visibility = Visibility.Visible;
            ApplyOcrContent();
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
        OcrBar.Visibility = Visibility.Collapsed;

        ApplyCursor();
        RefreshHoverChrome();
    }

    /// <summary>换图之后文字层对不上了：退出并把缓存清干净（标注 / 拖放替换都要调）。</summary>
    private void ResetOcrCache()
    {
        ExitOcrMode();
        _ocrPhase = PinOcrPhase.Idle;
        _ocrModel = null;
        _ocrLayer = Array.Empty<OcrTextLine>();
        _ocrText = "";
        OcrCanvas.Visibility = Visibility.Collapsed;
        OcrBar.Visibility = Visibility.Collapsed;
    }

    // ============ 识别 ============

    /// <summary>后台跑一次识别（用的是「新建 OCR 取值」同一套引擎偏好）。</summary>
    private async Task RecognizeAsync()
    {
        int run = ++_ocrRun;
        _ocrPhase = PinOcrPhase.Busy;
        RefreshOcrBar();

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
            if (run == _ocrRun && _ocrMode)
            {
                _ocrPhase = PinOcrPhase.Empty;
                ToastService.Show("文字识别失败：" + ex.Message, "贴图文字", 3600, ToastKind.error,
                    key: "pin-ocr");
                RefreshOcrBar();
            }

            return;
        }

        if (run != _ocrRun || !_ocrMode || !IsVisible) return;
        ApplyOcrResult(result);
    }

    /// <summary>
    /// 把一份识别结果铺到贴图上（识别线程回来时调；冒烟直接灌结果，不跑真 OCR）。
    /// </summary>
    internal void ApplyOcrResult(RecognizeResult result)
    {
        if (!_ocrMode) return;
        _ocrRun++;

        var layer = result.TextLayer.Count > 0 ? result.TextLayer : OcrTextLayer.FromLines(result.Lines);
        if (layer.Count == 0)
        {
            _ocrModel = null;
            _ocrLayer = Array.Empty<OcrTextLine>();
            _ocrText = "";
            _ocrPhase = PinOcrPhase.Empty;
            OcrCanvas.Visibility = Visibility.Collapsed;
            ToastService.Show(PinOcr.EmptyHint, "贴图文字", 2600, ToastKind.warning, key: "pin-ocr");
            RefreshOcrBar();
            return;
        }

        var model = new OcrTextEditModel();
        model.Load(layer.Select(line => line.Text));
        model.Changed += RefreshOcrBar;

        _ocrModel = model;
        _ocrLayer = layer;
        _ocrText = model.AllText;
        _ocrPhase = PinOcrPhase.Ready;

        OcrCanvas.Attach(model, layer, _content.PixelWidth, _content.PixelHeight);
        OcrCanvas.SetFillBackground(true);
        OcrCanvas.Visibility = Visibility.Visible;
        ApplyOcrContent();
        RefreshOcrBar();
        OcrCanvas.InvalidateVisual();
    }

    // ============ 界面 ============

    private void EnsureOcrSubscribed()
    {
        if (_ocrSubscribed) return;
        _ocrSubscribed = true;
        OcrCanvas.SelectionChanged += RefreshOcrBar;
    }

    /// <summary>文字层的显隐与状态条（阶段一变就调）。</summary>
    private void ApplyOcrChrome()
    {
        OcrCanvas.Visibility = _ocrMode && _ocrModel != null ? Visibility.Visible : Visibility.Collapsed;
        OcrBar.Visibility = _ocrMode ? Visibility.Visible : Visibility.Collapsed;
        RefreshOcrBar();
    }

    /// <summary>文字层跟着贴图的缩放与平移走（同一套几何：源图像素 × _zoom ÷ _scale）。</summary>
    private void ApplyOcrContent()
    {
        if (!_ocrMode) return;

        // 位置 = 与图片相同的偏移；缩放 = 物理像素 → DIP（图本身是按 _baseWidth = 像素 ÷ _scale 铺的）
        _ocrPan ??= new TranslateTransform();
        _ocrPan.X = _pan.X;
        _ocrPan.Y = _pan.Y;
        OcrCanvas.RenderTransform = _ocrPan;
        OcrCanvas.SetZoom(_zoom / Math.Max(0.01, _scale));
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

    /// <summary>关窗前把在途识别作废（手柄 / 状态条都是本窗口的控件，不用单独收）。</summary>
    private void ShutdownOcr()
    {
        _ocrRun++;
        _ocrMode = false;
    }
}
