using System.Collections.Generic;
using System.IO;
using LTools.Features;

namespace LTools.Features.Migration.Legacy;

/// <summary>
/// 旧动作（schema v1）的只读展示：列表摘要、外部资源引用。
/// </summary>
/// <remarks>
/// <para>
/// 新动作只显示 <see cref="ActionDto.Program"/> 的步骤数，用不到这里的任何字段；
/// 只有「还没被迁移过、也没有程序体」的历史动作会走到这条路径（例如备份导入预览、
/// 设置里的动作清单）。迁移完成后这些分支不会再触发。
/// </para>
/// <para>
/// 这些读法集中在一个文件里，是为了让 <see cref="ActionDtoLegacy"/> 的字段除迁移器之外
/// 只有一处引用，日后要彻底删掉旧字段时只需要处理本文件。
/// </para>
/// </remarks>
internal static class LegacyActionSummary
{
    /// <summary>旧动作的列表摘要（与编辑器 / 备份预览的显示一致）。</summary>
    public static string Summarize(ActionDto a, IReadOnlyDictionary<string, string>? macroNames) => a.SourceType switch
    {
        "launchApp" => a.Target ?? "",
        "openFile" or "openFolder" => a.Path ?? "",
        "openUrl" => a.Url ?? "",
        "runCommand" => string.IsNullOrEmpty(a.Cmd) ? "" : $"{a.Cmd}",
        "sendText" => BriefText(a.Text),
        "setClipboard" => BriefText(a.Text),
        "toast" => BriefText(a.Text),
        "quickNote" => QuickNoteSummary(a),
        "window" => WindowSummary(a),
        "clickText" => ClickTextSummary(a),
        "imageClick" => ImageClickSummary(a),
        "sendKeys" => a.Items != null ? $"{a.Items.Count} 条输入" : "",
        "macro" => MacroLabel(a.MacroId, macroNames),
        "internal" => a.Command ?? "",
        "composite" => CompositeSummary(a),
        _ => "",
    };

    /// <summary>旧动作的中文类型名（新动作由调用方显示「动作」）。</summary>
    public static string TypeLabel(ActionDto a) => a.SourceType switch
    {
        "launchApp" => "启动程序",
        "openFile" => "打开文件",
        "openFolder" => "打开文件夹",
        "openUrl" => "打开网址",
        "runCommand" => "运行命令",
        "sendText" => "发送文本",
        "setClipboard" => "写入剪贴板",
        "toast" => "提示弹窗",
        "quickNote" => "写速记",
        "window" => "窗口操作",
        "clickText" => "文字点击",
        "imageClick" => "图片点击",
        "sendKeys" => "模拟按键",
        "mouse" => "鼠标操作",
        "macro" => "键鼠宏",
        "internal" => "内部动作",
        "composite" => "组合动作",
        _ => a.SourceType,
    };

    /// <summary>组合动作摘要：统计含嵌套在内的节点数（旧 Steps 也能显示）。</summary>
    private static string CompositeSummary(ActionDto a)
    {
        if (a.Flow == null && a.Steps == null) return "";
        int count = CountNodes(FlowMigration.Ensure(a));
        return count > 0 ? $"{count} 个步骤" : "";
    }

    /// <summary>窗口操作摘要：「切到前台 · 记事本」/「改尺寸 800×600 · 记事本」。</summary>
    private static string WindowSummary(ActionDto a)
    {
        WindowActionKind op = a.WindowOp ?? WindowActionKind.activate;
        string target = !string.IsNullOrWhiteSpace(a.WindowTitle) ? a.WindowTitle!.Trim()
                      : !string.IsNullOrWhiteSpace(a.WindowClass) ? a.WindowClass!.Trim()
                      : "?";
        if (op == WindowActionKind.resize && a.WindowRect is { Length: >= 4 } r)
            return $"{WindowActionLabel.Short(op)} {r[2]}×{r[3]} · {target}";
        return $"{WindowActionLabel.Short(op)} · {target}";
    }

    /// <summary>文字点击摘要：「前台窗口 · 保存」。</summary>
    private static string ClickTextSummary(ActionDto a)
    {
        string text = string.IsNullOrWhiteSpace(a.Text) ? "(未填文字)" : a.Text!.Trim();
        return $"{RegionLabel(a.Region)} · {text}";
    }

    /// <summary>图片点击摘要：「前台窗口 · save.png」。</summary>
    private static string ImageClickSummary(ActionDto a)
    {
        string image = string.IsNullOrWhiteSpace(a.Image)
            ? "(未选模板图)"
            : Path.GetFileName(a.Image!.Trim());
        return $"{RegionLabel(a.Region)} · {image}";
    }

    /// <summary>写速记摘要：「工作 · 标题」/ 正文前 40 字。</summary>
    private static string QuickNoteSummary(ActionDto a)
    {
        string title = a.NoteTitle?.Trim() ?? "";
        if (title.Length == 0) return BriefText(a.Text);
        return string.IsNullOrWhiteSpace(a.NoteCategory) ? title : $"{a.NoteCategory!.Trim()} · {title}";
    }

    private static string RegionLabel(ImageRegion? region) => (region ?? ImageRegion.foreground) switch
    {
        ImageRegion.screen => "整个屏幕",
        ImageRegion.rect => "固定区域",
        _ => "前台窗口",
    };

    private static int CountNodes(List<FlowNode> nodes)
    {
        int count = 0;
        foreach (var n in nodes)
        {
            count++;
            if (n.If != null)
            {
                count += CountNodes(n.If.Then);
                if (n.If.Else != null) count += CountNodes(n.If.Else);
            }
            if (n.Loop != null) count += CountNodes(n.Loop.Body);
        }
        return count;
    }

    private static string MacroLabel(string? id, IReadOnlyDictionary<string, string>? names)
    {
        if (string.IsNullOrWhiteSpace(id)) return "（未选择宏）";
        if (names != null && names.TryGetValue(id!, out var name)) return name;
        return id!;
    }

    /// <summary>文本摘要：首行截断（超长加省略号），供动作列表 Summary 列。</summary>
    private static string BriefText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0];
        return oneLine.Length > 40 ? oneLine[..40] + "…" : oneLine;
    }
}
