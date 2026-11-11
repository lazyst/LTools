namespace LTools.Core;

/// <summary>
/// 左键双击托盘图标时做什么（设置 → 通用 → 双击托盘图标）。
/// 全部是「打开设置窗口并停在某页」：一个是普通打开（停上次页），其余跳到指定页。
/// </summary>
/// <remarks>
/// 值按设置页下拉顺序固定（显式赋值，别靠声明顺序），存进 <c>configs/settings.json</c> 的英文键；
/// 界面文案由 <c>Views/ConfigHelper.xaml</c> 的 ComboBox 负责，别把中文写进配置。
/// 「面板管理」是二级下拉分组，跳父项 tag（<c>superpanel</c>）会落到该组上次停留的子页。
/// </remarks>
public enum TrayDoubleClickAction
{
    /// <summary>打开设置，停在上次记住的导航页（普通「设置」入口）。</summary>
    settings = 0,

    /// <summary>打开设置并跳到「动作管理」。</summary>
    actions = 1,

    /// <summary>打开设置并跳到「快捷键」。</summary>
    hotkeys = 2,

    /// <summary>打开设置并跳到「键鼠宏」。</summary>
    macros = 3,

    /// <summary>打开设置并跳到「自动触发」。</summary>
    triggers = 4,

    /// <summary>打开设置并跳到「面板管理」（落在该分组上次停留的子页）。</summary>
    superpanel = 5,
}
