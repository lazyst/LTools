using System;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 把贴图拖出成文件（照上游 <c>screenshotpinnedinteraction.cpp</c> 的
/// <c>exportDragEnabledAt</c> / <c>handleExportDrag</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 入口只有一个：<c>Ctrl</c>+左键按下后再挪过 4 DIP（不挪就是「多选」）。局部显示里这条手势
/// 仍是「移动窗口」（那里 <c>Ctrl</c> 拖动有别的含义，照上游 <c>!m_viewportMode</c> 的口径），
/// 实时贴图 / 点击穿透态不参加。
/// </para>
/// <para>
/// 锁定的贴图**可以**拖出：锁只锁位置与尺寸，导出不改几何。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    private bool _exporting;

    /// <summary>正在往外拖内容（拖放接收要拒绝自己）。</summary>
    internal bool Exporting => _exporting;

    /// <summary>开始一次拖出（由 <see cref="Window_MouseMove"/> 在 Ctrl+拖动时调）。</summary>
    internal void BeginExportDrag()
    {
        if (_exporting || _live || _clickThrough) return;

        _exporting = true;
        ApplyFrame();
        try
        {
            string name = $"贴图-{DateTime.Now:yyyyMMdd-HHmmss}";
            PinDragExport.Start(this, _content, name);
        }
        finally
        {
            _exporting = false;
            ApplyFrame();
        }
    }
}
