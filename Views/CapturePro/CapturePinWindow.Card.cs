using System.Windows;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图卡片上的复制热区：颜色取样板那三枚「复制」图标（HEX / RGB / HSL）。
/// </summary>
/// <remarks>
/// 照上游 <c>ColorSwatchCopyButton</c>：命中的是图标那两笔（<c>Qt::OddEvenFill</c>，重叠处不算），
/// 缩略图里不响应（方块太小、点哪儿都是还原）。点一下就把那行的规范 CSS 放进剪贴板，
/// 上游没有成功提示，这里也不弹 toast。
/// </remarks>
public partial class CapturePinWindow
{
    /// <summary>窗口内这个点（DIP）落在哪枚复制按钮上；图片贴图 / 缩略图 / 实时贴图返回 <c>null</c>。</summary>
    internal PinCardCopy? CopyHitAt(Point windowPoint)
    {
        if (_copies.Count == 0 || _thumb || _live || _zoom <= 0) return null;

        // 热区在卡片的 100% 坐标里：先去掉平移再除以缩放
        var card = new Point((windowPoint.X - _pan.X) / _zoom, (windowPoint.Y - _pan.Y) / _zoom);
        foreach (var copy in _copies)
        {
            if (copy.Hit(card)) return copy;
        }
        return null;
    }

    /// <summary>点中复制按钮就把那行规范 CSS 放进剪贴板（返回是否命中）。</summary>
    internal bool CopyAt(Point windowPoint)
    {
        var copy = CopyHitAt(windowPoint);
        if (copy == null) return false;
        NativeClipboard.SetText(copy.Text);
        return true;
    }
}
