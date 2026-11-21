using System;
using System.Collections.Generic;
using System.IO;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;

namespace LTools.Core.SelfTest;

/// <summary>
/// 面板的「复制引用 / 复制为副本 / 粘贴」里能纯逻辑化的部分：
/// 副本命名、剪贴板条目文案、以及「副本把 files/ 一起带走」。
/// </summary>
/// <remarks>
/// 真机行为（菜单项、粘到哪个分组、跨场景粘贴）靠仓库内的 <c>tests/PanelClipboardSmoke</c>。
/// </remarks>
internal static class PanelClipboardSuite
{
    /// <summary>自测用的源动作 Id：不用随机 ULID，失败信息里好认。</summary>
    private const string SourceId = "01j0panelselftestsource";

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("panelClipboard", "副本命名：X 副本 / X 副本 2 / 已有重名继续往后编", CopyNames));
        cases.Add(new("panelClipboard", "副本命名：剥掉结尾的「副本 / 副本 N」再编号", CopyTrim));
        cases.Add(new("panelClipboard", "剪贴板条目：引用 / 副本的菜单标题与说明", ClipLabels));
        cases.Add(new("panelClipboard", "剪贴板状态：放进去 / 清空", ClipState));
        cases.Add(new("panelClipboard", "复制为副本：新 Id + 新名字 + files/ 一起带走", DuplicateKeepsFiles));
    }

    private static void CopyNames()
    {
        Check.Equal("截图 副本", CopyNaming.Next("截图"), "没有重名直接加「 副本」");

        Check.Equal("截图 副本 2", CopyNaming.Next("截图", new[] { "截图 副本" }), "已占用时编到 2");
        Check.Equal("截图 副本 3", CopyNaming.Next("截图", new[] { "截图 副本", "截图 副本 2" }), "接着编到 3");
        Check.Equal("截图 副本 2", CopyNaming.Next("截图", new[] { "截图 副本", "  截图 副本  " }), "重复项只算一个");
        Check.Equal("截图 副本", CopyNaming.Next("截图", new[] { "别的动作 副本" }), "不相干的名字不影响");

        // 复制一个副本：剥掉后缀再编号，不出「X 副本 副本」；源名字本身也算已占用
        Check.Equal("截图 副本 2", CopyNaming.Next("截图 副本"), "复制副本本身");
        Check.Equal("截图 副本", CopyNaming.Next("截图 副本 2", new[] { "截图 副本 2" }), "取第一个没被占用的后缀");
        Check.Equal("截图 副本 3", CopyNaming.Next("截图 副本 2", new[] { "截图 副本", "截图 副本 2" }),
            "副本 1、2 都被占时编到 3");

        Check.Equal("截图 副本 4", CopyNaming.Next("截图", new[] { "截图 副本", "截图 副本 2", "截图 副本 3" }),
            "1–3 都被占时编到 4");

        Check.Equal("未命名动作 副本", CopyNaming.Next("   "), "空名字也给个能看的名字");
    }

    private static void CopyTrim()
    {
        Check.Equal("截图", CopyNaming.Trim("截图"), "普通名字原样");
        Check.Equal("截图", CopyNaming.Trim("截图 副本"), "剥「 副本」");
        Check.Equal("截图", CopyNaming.Trim("截图 副本 2"), "剥「 副本 N」");
        Check.Equal("截图", CopyNaming.Trim("截图 副本 副本"), "连剥两层");
        Check.Equal("未命名动作", CopyNaming.Trim(""), "空串兜底");
        Check.Equal("A 副本 B", CopyNaming.Trim("A 副本 B"), "中间有「副本」不动");
        Check.Equal("A 副本 x", CopyNaming.Trim("A 副本 x"), "结尾不是数字的「副本 x」不动");
    }

    private static void ClipLabels()
    {
        var reference = new ActionClipEntry(ActionClipKind.Reference, "someactionid", "截图");
        Check.Equal("粘贴「截图」", reference.PasteLabel, "引用：粘贴标题");
        Check.Equal("引用「截图」", reference.Describe, "引用：说明");

        var copy = new ActionClipEntry(ActionClipKind.Copy, "someactionid", "截图");
        Check.Equal("粘贴「截图」的副本", copy.PasteLabel, "副本：粘贴标题");
        Check.Equal("「截图」的副本", copy.Describe, "副本：说明");

        // 内置功能带参数的整串引用照原样带着走，且认得出是功能
        var feature = new ActionClipEntry(ActionClipKind.Reference, "common.capture#quality=high", "截图");
        Check.True(feature.IsFeature, "内置功能引用要认出来（没有副本）");
        Check.False(reference.IsFeature, "动作 Id 不是内置功能");
    }

    private static void ClipState()
    {
        ActionClipboard.Clear();
        Check.False(ActionClipboard.Has, "一开始是空的");

        var entry = ActionClipboard.CopyReference("someactionid");
        Check.True(ActionClipboard.Has, "复制引用后有东西");
        Check.True(ReferenceEquals(entry, ActionClipboard.Entry), "拿到的就是那一项");
        Check.Equal(ActionClipKind.Reference, ActionClipboard.Entry!.Kind, "类型 = 引用");

        var copy = ActionClipboard.CopyCopy("someactionid");
        Check.Equal(ActionClipKind.Copy, copy.Kind, "换成副本后类型跟着变");
        Check.Equal(ActionClipKind.Copy, ActionClipboard.Entry!.Kind, "剪贴板里只剩最后复制的那一份");

        ActionClipboard.Clear();
        Check.False(ActionClipboard.Has, "清空后没有东西");
    }

    /// <summary>副本必须把 <c>files/</c> 一起搬走（只 Clone 动作 JSON 会让模板图 / 脚本全断链）。</summary>
    private static void DuplicateKeepsFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-selftest-dup-" + Guid.NewGuid().ToString("N")[..8]);
        string anchor = Path.Combine(root, "configs", "LTools.json");
        var dto = new ActionDto { Id = SourceId, Name = "复制我", Icon = "app" };
        ActionRegistry.Register(dto);

        try
        {
            string files = ActionStore.FilesDir(anchor, SourceId);
            Directory.CreateDirectory(files);
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
            File.WriteAllBytes(Path.Combine(files, "tpl.png"), png);
            Directory.CreateDirectory(Path.Combine(files, "sub"));
            File.WriteAllText(Path.Combine(files, "sub", "run.ahk"), "MsgBox 1");

            var copy = ActionDuplicator.Duplicate(anchor, SourceId, Array.Empty<string>());
            Check.True(copy != null, "复制成功");
            if (copy == null) return;

            Check.Equal("复制我 副本", copy.Name, "名字加了「 副本」");
            Check.True(copy.Id != SourceId, "换了新 Id");
            Check.True(Directory.Exists(ActionStore.FilesDir(anchor, copy.Id)), "副本建了自己的 files/");
            Check.True(File.Exists(Path.Combine(ActionStore.FilesDir(anchor, copy.Id), "tpl.png")), "模板图跟着走");
            Check.True(File.Exists(Path.Combine(ActionStore.FilesDir(anchor, copy.Id), "sub", "run.ahk")),
                "子目录里的脚本也跟着走");
            Check.Equal(png.Length,
                new FileInfo(Path.Combine(ActionStore.FilesDir(anchor, copy.Id), "tpl.png")).Length,
                "模板图内容长度一致");

            // 源动作有自己的名字：不重名时就是「副本」，重名时继续往后编
            var again = ActionDuplicator.Duplicate(anchor, SourceId, new[] { "复制我 副本" });
            Check.Equal("复制我 副本 2", again?.Name, "已有副本时编到 2");

            // 源不存在：不造副本
            Check.True(ActionDuplicator.Duplicate(anchor, "01j0nosuchaction0000000000") == null, "源不存在返回 null");
        }
        catch (Exception ex)
        {
            Check.True(false, "复制为副本抛异常：" + ex.Message);
        }
        finally
        {
            ActionRegistry.Remove(SourceId);
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录，删不掉也无所谓 */ }
        }
    }
}
