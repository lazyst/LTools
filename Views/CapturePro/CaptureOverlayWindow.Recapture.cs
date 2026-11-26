using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 重新截图（照 Snow Shot 的 recapture）：**只换底图**，选区、标注、撤销栈、样式全不动。
/// </summary>
/// <remarks>
/// <para>
/// 上游在 <c>screenshotcontroller.cpp</c> 的 <c>requestRecapture</c> / <c>prepareRecaptureWindows</c> +
/// <c>screenshotcaptureworkflow.cpp</c> 的 <c>startRecapture</c> / <c>finishRecapturePreparation</c>：
/// ① 先把覆盖层与工具条**从截屏里排除**（Windows 用 <c>WDA_EXCLUDEFROMCAPTURE</c> + 输入透明，
/// 不支持就整体藏起来并轮询等原生窗口真的不可见，超时算失败）；② 光标可见时先刷新光标图像；
/// ③ 异步重抓整个桌面；④ 结果只替换**显示层图像**（<c>applySnapshots</c> + 重建几何），
/// 选区 / 标注 / 样式 / 缩放都不动；⑤ 成功后作废识别会话、重跑自动二维码、重置自动打码与历史导航，
/// 失败保留旧图并提示。
/// </para>
/// <para>
/// 为什么要它：等页面刷新、弹窗关掉之后想重抓一张，不必丢掉已经画好的标注和框。
/// 入口在「选择」工具那一行（上游在移动工具那行：光标 / 重新截图 / 二维码），快捷键 Alt+R。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>排除之后等一帧，让 DWM 先把「不参与截图」这件事应用上去。</summary>
    private const int RecaptureSettleMs = 90;

    private bool _recaptureBusy;

    /// <summary>重抓进行中（按钮置灰、重复触发直接忽略）。</summary>
    internal bool RecaptureBusy => _recaptureBusy;

    /// <summary>
    /// 重新截图：重抓同一块桌面，只换底图。返回是否成功（失败已经给过提示，调用方不用再报）。
    /// </summary>
    internal async Task<bool> RecaptureAsync()
    {
        if (_recaptureBusy || _hwnd == IntPtr.Zero) return false;

        _recaptureBusy = true;
        SetRecaptureChrome(true);

        bool excluded = false;
        bool hidden = false;
        try
        {
            // ① 把自己从截屏里排除。系统不支持（Win10 2004 以前 / 被安全软件拦）就退回「藏一下再抓」。
            excluded = Win32.SetWindowDisplayAffinity(_hwnd, Win32.WdaExcludeFromCapture);
            if (!excluded)
            {
                Hide();
                hidden = true;
            }

            await Task.Delay(RecaptureSettleMs);

            // ② 重抓同一块虚拟桌面（物理像素，与第一次抓帧同一个矩形）；底图保持干净，
            //    光标是单独一份快照（下面 ④ 之后重抓），开关状态不变
            Int32Rect area = _doc.Monitor;
            BitmapSource shot = await Task.Run(() => ScreenShot.Capture(area));

            // ③ 尺寸变了（分辨率 / 显示器数量变了）：几何已经对不上，保留旧图、明确说一声
            if (shot.PixelWidth != _doc.Base.PixelWidth || shot.PixelHeight != _doc.Base.PixelHeight)
            {
                ToastService.Show(
                    $"屏幕尺寸从 {_doc.Base.PixelWidth}×{_doc.Base.PixelHeight} 变成 " +
                    $"{shot.PixelWidth}×{shot.PixelHeight}，重新截一张吧",
                    "重新截图失败", 4000, ToastKind.warning, key: "capture:recapture");
                return false;
            }

            // ④ 只换底图：选区 / 标注 / 撤销栈 / 样式都不动
            _doc.ReplaceBaseKeepingFrame(shot);
            _shot = shot;
            LoadCursorSnapshot(_doc.CursorVisible);   // 光标也跟着这一帧刷新（位置 / 形状都可能变了）
            AfterRecapture();
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Recapture", ex);
            ToastService.Show("重新截图失败：" + ex.Message, "重新截图",
                4000, ToastKind.error, key: "capture:recapture");
            return false;
        }
        finally
        {
            // ⑤ 撤掉排除 / 把窗口放回来（顺序：先恢复窗口再撤排除，避免闪一下）
            if (hidden && !IsVisible) Show();
            if (excluded) Win32.SetWindowDisplayAffinity(_hwnd, Win32.WdaNone);

            _recaptureBusy = false;
            SetRecaptureChrome(false);
        }
    }

    /// <summary>重抓期间把两个入口一起置灰（信息条上的「重新截图」与完成动作里的二维码）。</summary>
    private void SetRecaptureChrome(bool busy)
    {
        Toolbar.SetRecaptureBusy(busy);
        SelRecaptureBtn.Opacity = busy ? 0.45 : 1;
    }

    /// <summary>
    /// 换完底图之后要作废的东西（照上游 <c>finishRecapture</c>）：识别结果、自动二维码、
    /// 自动打码、智能选区都算在旧像素上，得按新底图重来。
    /// </summary>
    private void AfterRecapture()
    {
        Surface.InvalidateVisual();

        // 二维码：签名里含图像指纹，清掉就会重新扫一次
        ResetQr();
        ScheduleQrScan();

        // 自动打码：结果来自旧像素，重跑一遍识别
        if (_tool == CaptureTool.AutoFilter) SyncAutoFilter(detect: true);

        // 智能选区：预览框按窗口层级算，底图换了要重算一次
        if (_smartPreviewActive) UpdateSmartTarget(_smartTop, refine: true);

        UpdateChrome();
    }
}
