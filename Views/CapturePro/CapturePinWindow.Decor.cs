using System;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图自己的边框与阴影：开关、状态色、独立的阴影窗口。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>pin_to_screen/show_border_by_default</c> / <c>show_shadow_by_default</c> 与六个状态色：
/// 普通 / 活动（选中、拖入拖出）/ 锁定 各一套边框色与阴影色。开关是**每张贴图自己的状态**
/// （<see cref="PinRecord.ShowBorder"/> / <see cref="PinRecord.ShowShadow"/>），右键菜单可切、贴回时恢复；
/// 全局那两项只是「新建时默认怎么显示」（<see cref="PinSettings"/>）。
/// </para>
/// <para>
/// 阴影是独立分层窗口（<see cref="PinShadowWindow"/>）：贴图窗口的宽高就是内容宽高，
/// 往里加内边距会把拖动 / 缩放 / 吸附 / 导出的几何全部推翻。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    /// <summary>这张贴图现在画不画边框（右键菜单可切，存进贴图历史）。</summary>
    internal bool ShowBorder => _showBorder;

    /// <summary>这张贴图现在画不画阴影（右键菜单可切，存进贴图历史）。</summary>
    internal bool ShowShadow => _showShadow;

    /// <summary>改「显示边框」；<paramref name="persist"/> = 顺手写回贴图历史（贴回时用）。</summary>
    internal void SetShowBorder(bool on, bool persist = true)
    {
        if (_showBorder == on) return;
        _showBorder = on;
        ApplyFrame();
        if (persist) PersistDecoration();
    }

    /// <summary>改「显示阴影」；<paramref name="persist"/> = 顺手写回贴图历史。</summary>
    internal void SetShowShadow(bool on, bool persist = true)
    {
        if (_showShadow == on) return;
        _showShadow = on;
        SyncShadow();
        if (persist) PersistDecoration();
    }

    /// <summary>贴回时按记录恢复两个开关（不写盘：记录本来就是它）。</summary>
    internal void SetDecoration(bool showBorder, bool showShadow)
    {
        _showBorder = showBorder;
        _showShadow = showShadow;
        ApplyFrame();
        SyncShadow();
    }

    private void PersistDecoration()
    {
        if (PinId.Length == 0) return;
        PinStore.UpdateDecoration(PinId, _showBorder, _showShadow);
    }

    // ============ 状态色 ============

    /// <summary>活动态：选中 / 拖入拖出（照上游 <c>border_active_color</c> 的用法）。</summary>
    private bool DecorActive => _selected || _fileDragActive || _exporting;

    /// <summary>边框色：活动 &gt; 锁定 &gt; 普通；局部显示用普通色的 40% 透明度。</summary>
    private Color BorderTint()
    {
        if (DecorActive) return PinSettings.BorderActiveColor;
        if (_locked) return PinSettings.LockedBorderColor;

        Color normal = PinSettings.BorderColor;
        return _viewport ? Color.FromArgb((byte)(normal.A * 0.4), normal.R, normal.G, normal.B) : normal;
    }

    /// <summary>阴影色：与边框同一套状态优先级。</summary>
    private Color ShadowTint()
    {
        if (DecorActive) return PinSettings.ShadowActiveColor;
        return _locked ? PinSettings.LockedShadowColor : PinSettings.ShadowColor;
    }

    // ============ 阴影窗口 ============

    /// <summary>
    /// 让阴影窗口对齐贴图当前矩形 / 状态。隐藏态、没句柄、图自带阴影时一律收起。
    /// </summary>
    private void SyncShadow()
    {
        if (!_showShadow || !IsVisible || _hwnd == IntPtr.Zero || _appearance.SuppressesFrame)
        {
            HideShadow();
            return;
        }

        _shadow ??= new PinShadowWindow();
        if (!_shadow.IsVisible) _shadow.Show();

        var box = new Rect(Left, Top, Math.Max(1, Width), Math.Max(1, Height));
        _shadow.Follow(box, _thumb ? 0 : _appearance.RadiusDip(_scale, _zoom), ShadowTint(), _scale,
            Topmost, _hwnd);
    }

    private void HideShadow()
    {
        if (_shadow != null && _shadow.IsVisible) _shadow.Hide();
    }

    /// <summary>关窗时把阴影窗口一起带走（它是独立 HWND，不关会留在屏幕上）。</summary>
    private void ShutdownShadow()
    {
        if (_shadow == null) return;
        _shadow.Close();
        _shadow = null;
    }
}
