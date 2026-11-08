using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 悬浮动作 / 悬浮分组的**偏好**（设计见 <c>docs/FLOATING.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 悬浮项本身**无状态**（与 Quicker 一致）：按钮只在本次运行存在，退出即消失，
/// 不写进 <c>LTools.json</c>、下次启动也不恢复——所以这里只有偏好，没有 Items。
/// 运行期列表在 <c>Features.Floating.FloatingLibrary.Items</c>。
/// </para>
/// <para>
/// 保留「一个动作只允许一个悬浮实例」：由 <c>FloatingLibrary</c> 在内存里按动作 Id 去重，
/// 删除动作时 <c>ConfigStore.RemoveAction → FloatingManager.RefreshAll</c> 立即摘掉成员。
/// </para>
/// </remarks>
public sealed class FloatingConfig
{
    /// <summary>全局偏好。</summary>
    public FloatingPreferencesDto Preferences { get; set; } = new();
}

/// <summary>悬浮功能的全局偏好（设置「面板管理 → 常规」里改）。</summary>
public sealed class FloatingPreferencesDto
{
    /// <summary>中键点击关闭悬浮项（与 Quicker 一致，可关）。</summary>
    public bool MiddleClickClose { get; set; } = true;

    /// <summary>鼠标悬停时显示完整动作名（名称被截断时有用）。</summary>
    public bool ShowTooltip { get; set; } = true;

    /// <summary>拖动时吸附到屏幕工作区边缘。</summary>
    public bool SnapToEdges { get; set; } = true;

    /// <summary>吸附 / 合并阈值（DIP）：拖到别的悬浮项这么近就贴上去并成组。</summary>
    public double SnapDistance { get; set; } = 12;

    /// <summary>新建悬浮项的按钮尺寸（DIP，图标区的边长）。</summary>
    public double DefaultSize { get; set; } = 44;

    /// <summary>新建悬浮项的不透明度（0.3~1）。</summary>
    public double DefaultOpacity { get; set; } = 1.0;

    /// <summary>悬浮项数量上限（防呆；Quicker 是 256，这里只防误操作）。</summary>
    public int MaxItems { get; set; } = 32;
}

/// <summary>
/// 一个悬浮项（**运行期对象，不落盘**）：单个动作（<see cref="Members"/> 只有一项）
/// 或一个分组（多项）。两者共用同一种窗口，成组只是成员变多，拆组就是拆成员。
/// </summary>
/// <remarks>
/// 悬浮项**全局常驻**（不按前台进程显隐）+ **无状态**（重启不恢复，见 <see cref="FloatingConfig"/>）。
/// </remarks>
public sealed class FloatingItemDto
{
    public string Id { get; set; } = "";

    /// <summary>分组名；单个悬浮动作为空。</summary>
    public string Name { get; set; } = "";

    /// <summary>窗口左上角 X（DIP，虚拟桌面坐标，可为负）。</summary>
    public double X { get; set; }

    /// <summary>窗口左上角 Y（DIP，虚拟桌面坐标，可为负）。</summary>
    public double Y { get; set; }

    /// <summary>按钮图标区的边长（DIP）；按钮本身是正方形，边长 = Size + 名称行。</summary>
    public double Size { get; set; } = 44;

    /// <summary>不透明度（0.3~1）。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>锁定位置：禁止拖动，避免误触。</summary>
    public bool PositionLocked { get; set; }

    /// <summary>按钮排布方向：false = 横向一排（默认），true = 竖着一列。</summary>
    public bool Vertical { get; set; }

    /// <summary>成员（至少一个；为空视为待回收）。</summary>
    public List<FloatingMemberDto> Members { get; set; } = new();
}

/// <summary>悬浮项里的一个按钮：只存动作 Id，显示名 / 图标每次现查（改动作后自动跟随）。</summary>
public sealed class FloatingMemberDto
{
    public string ActionId { get; set; } = "";
}
