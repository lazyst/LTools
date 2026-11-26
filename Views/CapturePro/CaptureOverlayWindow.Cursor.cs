using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「显示光标」那一条：抓一张鼠标指针快照灌进文档，覆盖层里能随时开关
/// （照上游 <c>screenshot/capture_cursor</c> + 工具条 <c>m_cursorButton</c> + 快捷键
/// <c>toggle_cursor_visibility</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把光标当成**冻结画面的一层**（<c>cursorPatch</c> + <c>cursorPixelRect</c>），
/// 位置就是抓帧那一刻的指针；加 <c>cursorVisible</c> 决定画不画。所以 LTools 也不再把指针
/// 直接烤进底图——底图保持干净，指针单独存一份，开关、落历史、重新截图都不打架。
/// </para>
/// <para>
/// 成品（复制 / 保存 / 贴图 / 采集历史）与预览共用 <see cref="CaptureDocument.DrawCursor"/>，
/// **没有第二套合成**。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>
    /// 抓一张光标快照（形状 + 热点）灌进文档；<paramref name="visible"/> = 新截图默认画不画。
    /// 取不到光标（远端会话 / 系统把指针藏了）时开关置灰，成品里自然也没有。
    /// </summary>
    internal void LoadCursorSnapshot(bool visible)
    {
        CursorCapture.CursorFrame? frame = null;
        Point at = default;

        try
        {
            frame = CursorCapture.TryRead(out at);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "读取鼠标指针失败：" + ex.Message);
        }

        _doc.SetCursor(frame, frame == null ? null : at, visible && frame != null);
        Surface.InvalidateVisual();
        SyncCursorChrome();
    }

    /// <summary>把文档里的光标状态同步到选区信息条（有没有 / 画不画）。</summary>
    private void SyncCursorChrome()
    {
        SelCursorBtn.Opacity = _doc.CursorAvailable ? 1 : 0.45;
        SelCursorBtn.ToolTip = _doc.CursorAvailable
            ? "显示光标（`）：把鼠标指针画进成品"
            : "这一张没取到鼠标指针（远端会话 / 光标已隐藏）";
        SelCursorIcon.Fill = new SolidColorBrush(_doc.CursorVisible ? BarPrimary : Colors.White);
    }

    /// <summary>
    /// 「重新编辑截图」：换成那条记录当时的光标小图（没有就清掉）。位置是相对整屏底图的偏移。
    /// </summary>
    internal void LoadCursorFromHistory(BitmapSource? image, Point offset, bool visible)
    {
        _doc.SetCursorImage(image, offset, visible);
        Surface.InvalidateVisual();
        SyncCursorChrome();
    }

    /// <summary>打开 / 关掉「成品里画鼠标指针」。没有光标快照时什么也不做。</summary>
    internal void SetCursorVisible(bool visible)
    {
        if (!_doc.CursorAvailable) return;

        _doc.SetCursorVisible(visible);
        Surface.InvalidateVisual();
        SyncCursorChrome();
    }

    /// <summary>快捷键 <c>toggle_cursor_visibility</c>。</summary>
    private void ToggleCursorVisibility() => SetCursorVisible(!_doc.CursorVisible);
}
