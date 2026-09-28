using System.Collections.Generic;

namespace CapsLockPro.Features;

/// <summary>
/// 内置图标目录（对应计划 §3.1）。使用 Windows 自带 Segoe MDL2 Assets 字体
/// （Win10/11 均可用），动作存图标名称 Id；渲染时取对应字形码点。
/// 名称→码点映射在此固定；后续可改用 Segoe Fluent Icons（Win11）扩展。
/// </summary>
internal static class IconCatalog
{
    /// <summary>名称 → Segoe MDL2 Assets 字形字符。</summary>
    private static readonly Dictionary<string, string> _glyphs = new()
    {
        // —— 动作类型常用 ——
        ["app"]        = "\uE737",  // 窗口/应用
        ["file"]       = "\uE8A5",  // 文档
        ["folder"]     = "\uE8B7",  // 文件夹
        ["link"]       = "\uE71B",  // 链接
        ["terminal"]   = "\uE756",  // 命令提示符
        ["note"]       = "\uE70B",  // 编辑/速记
        ["search"]     = "\uE721",  // 搜索
        ["magnifier"]  = "\uE714",  // 放大镜
        ["pin"]        = "\uE740",  // 固定/置顶
        ["help"]       = "\uE897",  // 帮助
        ["settings"]   = "\uE713",  // 设置齿轮
        ["composite"]  = "\uE72C",  // 刷新/流程

        // —— 通用导航 ——
        ["home"]       = "\uE80F",
        ["back"]       = "\uE72B",
        ["forward"]    = "\uE72A",
        ["up"]         = "\uE74A",
        ["refresh"]    = "\uE72C",
        ["globe"]      = "\uE774",

        // —— 工具/应用 ——
        ["star"]       = "\uE734",
        ["calculator"] = "\uE1D0",
        ["calendar"]   = "\uE787",
        ["clock"]      = "\uE823",
        ["camera"]     = "\uE722",
        ["mail"]       = "\uE715",
        ["contact"]    = "\uE77B",
        ["phone"]      = "\uE717",
        ["message"]    = "\uE8BD",
        ["music"]      = "\uE8D6",
        ["video"]      = "\uE8B2",
        ["image"]      = "\uE8B9",
        ["print"]      = "\uE749",

        // —— 文件操作 ——
        ["download"]   = "\uE896",
        ["upload"]     = "\uE898",
        ["save"]       = "\uE105",
        ["delete"]     = "\uE74D",
        ["add"]        = "\uE710",
        ["edit"]       = "\uE70F",
        ["copy"]       = "\uE8C8",
        ["paste"]      = "\uE77F",

        // —— 其他 ——
        ["filter"]     = "\uE71C",
        ["power"]      = "\uE7E8",
    };

    /// <summary>默认图标（动作未指定图标时使用）。</summary>
    public const string Default = "app";

    /// <summary>按名称取字形字符；找不到返回默认图标字形。</summary>
    public static string GetGlyph(string? name)
    {
        if (!string.IsNullOrEmpty(name) && _glyphs.TryGetValue(name!, out var g))
            return g;
        return _glyphs[Default];
    }

    /// <summary>尝试取字形；名称不存在返回 false（glyph 为 null）。</summary>
    public static bool TryGet(string name, out string? glyph) => _glyphs.TryGetValue(name, out glyph);

    /// <summary>全部图标名称（供编辑器图标网格展示）。</summary>
    public static IReadOnlyCollection<string> Names => _glyphs.Keys;
}
