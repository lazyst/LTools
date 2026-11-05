using System;
using System.Globalization;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 速记动作 / 取值器的薄封装：写速记（<see cref="ActionType.quickNote"/>）与读速记（<see cref="ProbeKind.note"/>）。
/// 数据层复用 <see cref="NoteRepository"/>，目录与 CapsLock+N 的速记窗口完全一致（<see cref="AppPaths.NotesDir"/>）。
/// </summary>
/// <remarks>
/// 速记属于用户资产，不参与 <see cref="ImageLibrary"/> 那种按引用回收：写进去就一直在，
/// 只有用户自己在速记窗口里删除才会消失。
/// </remarks>
internal static class NoteActions
{
    /// <summary>结果里的时间格式（与速记列表一致，便于直接显示 / 比较）。</summary>
    public const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>写一条速记。标题留空时取正文第一行，分类留空 = 未分类。</summary>
    public static VarValue Write(ActionDto a)
    {
        string body = a.Text ?? "";
        if (string.IsNullOrWhiteSpace(body))
            throw new FlowException("写速记缺少正文");

        string title = (a.NoteTitle ?? "").Trim();
        if (title.Length == 0) title = FirstLine(body);
        string category = (a.NoteCategory ?? "").Trim();

        var entry = new NoteEntry
        {
            Title = title,
            Category = category,
            Body = body,
            Mtime = DateTime.Now,
        };

        var repo = new NoteRepository(AppPaths.NotesDir);
        string path = repo.Save(entry, null);
        string actualTitle = repo.Load(path)?.Title ?? title;

        return new VarValue
        {
            Text = path,
            File = path,
            Title = actualTitle,
            Time = entry.Mtime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Flag = true,
        };
    }

    /// <summary>
    /// 读速记：按分类 + 关键字（标题或正文包含，大小写不敏感）取最近修改的一条。
    /// 没有匹配不是错误——<c>found = false</c>、<c>count = 0</c>。
    /// </summary>
    public static VarValue Read(Probe probe, RunContext ctx)
    {
        string category = Template.Render(probe.NoteCategory ?? "", ctx).Trim();
        string keyword = Template.Render(probe.Text ?? "", ctx).Trim();

        var repo = new NoteRepository(AppPaths.NotesDir);
        var matches = repo.List(category.Length == 0 ? null : category,
                                keyword.Length == 0 ? null : keyword);

        if (matches.Count == 0)
            return new VarValue { Text = "", Flag = false, Number = 0 };

        var latest = matches[0];
        string body = repo.Load(latest.Path)?.Body ?? latest.Body;

        return new VarValue
        {
            Text = body,
            File = latest.Path,
            Title = latest.Title,
            Time = latest.Mtime.ToString(TimeFormat, CultureInfo.InvariantCulture),
            Flag = true,
            Number = matches.Count,
        };
    }

    /// <summary>正文第一行（去掉首尾空白，最多 40 字）作为默认标题。</summary>
    private static string FirstLine(string body)
    {
        string first = body.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return first.Length <= 40 ? first : first[..40];
    }
}
