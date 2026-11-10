using System.Collections.Generic;
using System.Linq;

namespace LTools.Features;

/// <summary>一个可选的键：<see cref="Value"/> 存进步骤（英文键名），<see cref="Label"/> 是界面上看到的写法。</summary>
internal sealed record KeyChoice(string Value, string Label);

/// <summary>按键分类（分组下拉的第一级）。</summary>
internal sealed record KeyGroup(string Name, IReadOnlyList<KeyChoice> Keys);

/// <summary>
/// 「模拟按键」能选的全部键，按键盘分区分成 8 组。
/// </summary>
/// <remarks>
/// <para>
/// 这是按键**唯一**的分类表：参数对话框的分组下拉、步骤摘要的中文名都从这里取，
/// 别在别处再写一份键名清单。键名本身仍由 <see cref="KeyStroke.TryResolveKey"/> 解析，
/// 所以这里多写一个键、少写一个键只影响「下拉里能不能选」，不影响老配置能不能跑。
/// </para>
/// <para>
/// 新增键：加进对应分组即可（值必须能被 <see cref="KeyStroke.TryResolveKey"/> 认出来）。
/// </para>
/// </remarks>
internal static class KeyCatalog
{
    public static IReadOnlyList<KeyGroup> Groups { get; } = Build();

    /// <summary>值 → 中文名（步骤摘要、参数提示用）。</summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } = Groups
        .SelectMany(group => group.Keys)
        .GroupBy(choice => choice.Value, System.StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Label, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>值所属的分组（载入已有步骤时用它定位两级下拉）。</summary>
    public static KeyGroup? GroupOf(string? value)
    {
        string key = (value ?? "").Trim();
        if (key.Length == 0) return null;

        foreach (var group in Groups)
            foreach (var choice in group.Keys)
                if (string.Equals(choice.Value, key, System.StringComparison.OrdinalIgnoreCase))
                    return group;

        return null;
    }

    /// <summary>显示名；不认识的键原样返回（老配置 / 手写值不至于显示成空白）。</summary>
    public static string LabelOf(string? value)
    {
        string key = (value ?? "").Trim();
        return Labels.TryGetValue(key, out string? label) ? label : key;
    }

    private static IReadOnlyList<KeyGroup> Build()
    {
        var letters = Enumerable.Range('a', 26)
            .Select(c => new KeyChoice(((char)c).ToString(), ((char)c).ToString().ToUpperInvariant()))
            .ToArray();

        var digits = Enumerable.Range(0, 10)
            .Select(n => new KeyChoice(n.ToString(), n.ToString()))
            .ToArray();

        var symbols = new[]
        {
            new KeyChoice("-", "减号 -"),
            new KeyChoice("=", "等号 ="),
            new KeyChoice("[", "左方括号 ["),
            new KeyChoice("]", "右方括号 ]"),
            new KeyChoice("\\", "反斜杠 \\"),
            new KeyChoice(";", "分号 ;"),
            new KeyChoice("'", "单引号 '"),
            new KeyChoice(",", "逗号 ,"),
            new KeyChoice(".", "句点 ."),
            new KeyChoice("/", "斜杠 /"),
            new KeyChoice("`", "反引号 `"),
        };

        var functionKeys = Enumerable.Range(1, 24)
            .Select(n => new KeyChoice("f" + n, "F" + n))
            .ToArray();

        var editing = new[]
        {
            new KeyChoice("enter", "Enter 回车"),
            new KeyChoice("tab", "Tab"),
            new KeyChoice("esc", "Esc"),
            new KeyChoice("space", "空格"),
            new KeyChoice("backspace", "Backspace 退格"),
            new KeyChoice("delete", "Delete 删除"),
            new KeyChoice("insert", "Insert 插入"),
            new KeyChoice("home", "Home"),
            new KeyChoice("end", "End"),
            new KeyChoice("pageup", "PageUp"),
            new KeyChoice("pagedown", "PageDown"),
            new KeyChoice("up", "↑ 上"),
            new KeyChoice("down", "↓ 下"),
            new KeyChoice("left", "← 左"),
            new KeyChoice("right", "→ 右"),
        };

        var numpad = new List<KeyChoice>();
        for (int n = 0; n <= 9; n++)
            numpad.Add(new KeyChoice("numpad" + n, "小键盘 " + n));
        numpad.Add(new KeyChoice("numpadmul", "小键盘 *"));
        numpad.Add(new KeyChoice("numpadadd", "小键盘 +"));
        numpad.Add(new KeyChoice("numpadsub", "小键盘 -"));
        numpad.Add(new KeyChoice("numpaddot", "小键盘 ."));
        numpad.Add(new KeyChoice("numpaddiv", "小键盘 /"));

        var modifiers = new[]
        {
            new KeyChoice("ctrl", "Ctrl"),
            new KeyChoice("shift", "Shift"),
            new KeyChoice("alt", "Alt"),
            new KeyChoice("win", "Win"),
        };

        var system = new[]
        {
            new KeyChoice("printscreen", "PrintScreen 截屏"),
            new KeyChoice("scrolllock", "ScrollLock"),
            new KeyChoice("pause", "Pause"),
            new KeyChoice("apps", "菜单键"),
            new KeyChoice("capslock", "CapsLock"),
            new KeyChoice("numlock", "NumLock"),
        };

        return new[]
        {
            new KeyGroup("字母", letters),
            new KeyGroup("数字", digits),
            new KeyGroup("符号", symbols),
            new KeyGroup("功能键", functionKeys),
            new KeyGroup("编辑与导航", editing),
            new KeyGroup("小键盘", numpad),
            new KeyGroup("修饰键", modifiers),
            new KeyGroup("系统键", system),
        };
    }
}
