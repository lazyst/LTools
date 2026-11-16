using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图分组的名字规则：默认组名、清洗用户输入、忽略大小写判重 / 查重。
/// **纯函数、不碰窗口**，所以 <c>--selftest</c> 直接断言（见 capturePro 组）。
/// </summary>
/// <remarks>
/// 分组**只在内存**（贴图本身就不进配置，重启不恢复）：建组 = 往
/// <see cref="CapturePinManager"/> 的名单里加一个名字，组空了自动消失。
/// 规则照 Snow Shot：名字 ≤ 16 字、忽略大小写去重、内置的默认组不可删。
/// </remarks>
internal static class PinGroups
{
    /// <summary>默认组（新贴图都先落这里；它永远存在，也不能被删）。</summary>
    public const string Default = "默认";

    /// <summary>组名最长的字数。</summary>
    public const int MaxName = 16;

    /// <summary>
    /// 清洗一个组名：去首尾空白、内部连续空白压成一个、超长截到 <see cref="MaxName"/> 字。
    /// 全是空白 = <c>null</c>（调用方提示「名字不能为空」，别建一个空名字的组）。
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string name = string.Join(" ", parts);
        if (name.Length == 0) return null;
        return name.Length <= MaxName ? name : name[..MaxName];
    }

    /// <summary>两个组名是不是同一个组（忽略大小写，与 Windows 文件名的习惯一致）。</summary>
    public static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 在已有组里找同名的那个（返回它原本的写法；没有 = <c>null</c>）。
    /// 传进来的名字照样清洗一遍，所以「 外部 」「外部」都认得出是同一个组。
    /// </summary>
    public static string? Find(string? name, IEnumerable<string> groups)
    {
        string? target = Clean(name);
        if (target == null) return null;

        foreach (var group in groups)
            if (Same(group, target)) return group;
        return null;
    }
}
