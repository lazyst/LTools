using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Units.Input;
using LTools.Features.Units.Screen;
using LTools.Views;

namespace LTools.Core.SelfTest;

/// <summary>
/// 「找多个目标」的纯逻辑部分：找图的重叠去重、找字的一行多次 / 词边界 / 排序，
/// 以及新参数（找全部 / 最多几个 / 命中顺序 / 点第几个）的可见性与类型声明。
/// </summary>
/// <remarks>
/// 不截图、不 OCR、不开窗口：真图 + 真识别靠仓库内的冒烟。这里守着的是
/// 「一个目标不会被算成两个」「两个挨着的目标不会被合成一个」这两条最容易改错的规则。
/// </remarks>
internal static class FindTargetsSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("findTargets", "找图：同一目标的框算重叠，隔开的算两个", OverlapRule));
        cases.Add(new("findTargets", "找图：跨缩放去重保留高分，limit 生效", DedupeHits));
        cases.Add(new("findTargets", "找字：一行里出现两次就是两个命中", SameLineTwice));
        cases.Add(new("findTargets", "找字：多行按从上到下、从左到右排序", SortByReading));
        cases.Add(new("findTargets", "找字：完全一致只认完整的词，包含仍命中", ExactWord));
        cases.Add(new("findTargets", "找字：没有词框的行退回整行中心", LineWithoutWords));
        cases.Add(new("findTargets", "找字：引擎报了两遍的同一块只留一个", DuplicateLines));
        cases.Add(new("findTargets", "参数：找全部才显示最多几个 / 命中顺序", AllGatesOptions));
        cases.Add(new("findTargets", "声明：命中列表 / 个数与点第几个", Declarations));
    }

    // ============ 找图 ============

    private static void OverlapRule()
    {
        var a = new ImageHit { X = 100, Y = 100, W = 40, H = 20, Score = 0.90 };
        var near = new ImageHit { X = 105, Y = 100, W = 40, H = 20, Score = 0.95 };
        var far = new ImageHit { X = 200, Y = 100, W = 40, H = 20, Score = 0.92 };

        Check.True(ImageProbe.Overlaps(a, near), "中心差 5（同一个图标在两个缩放里各命中一次）算重叠");
        Check.False(ImageProbe.Overlaps(a, far), "相隔 100 像素不算重叠");
        Check.False(ImageProbe.Overlaps(a, far), "左右对称：反过来也算不重叠");

        // 上下相邻的列表项：中心差 = 模板高 + 间距，不重叠
        var below = new ImageHit { X = 100, Y = 130, W = 40, H = 20, Score = 0.9 };
        Check.False(ImageProbe.Overlaps(a, below), "上下紧挨的列表项是两个目标");
    }

    private static void DedupeHits()
    {
        var hits = new List<ImageHit>
        {
            new() { X = 100, Y = 100, W = 40, H = 20, Score = 0.90 },
            new() { X = 103, Y = 101, W = 40, H = 20, Score = 0.96 },   // 同一个目标，分数更高
            new() { X = 200, Y = 100, W = 40, H = 20, Score = 0.92 },
            new() { X = 300, Y = 100, W = 40, H = 20, Score = 0.88 },
        };

        IReadOnlyList<ImageHit> kept = ImageProbe.Dedupe(hits, ImageProbe.MaxHits);
        Check.Equal(3, kept.Count, "四个原始命中其实是三个目标");
        Check.Equal(0.96, kept[0].Score, "重叠的那一对保留分数高的");
        Check.Equal(103, kept[0].X, "保留的是高分那条的位置");
        Check.Equal(200, kept[1].X, "其余按分数降序");
        Check.Equal(300, kept[2].X, "最后一个也在");

        Check.Equal(2, ImageProbe.Dedupe(hits, 2).Count, "limit 收口");
        Check.Equal(0, ImageProbe.Dedupe(new List<ImageHit>(), 10).Count, "什么都没有时返回空列表");
    }

    // ============ 找字 ============

    private static void SameLineTwice()
    {
        OcrLineBox line = TextLine("确定 确定", 0, 0, 100, 20,
            Word("确定", 0, 0, 30, 20),
            Word("确定", 50, 0, 30, 20));

        IReadOnlyList<TextHit> hits = TextLocator.CollectLines(new[] { line }, "确定", exact: false);
        Check.Equal(2, hits.Count, "一行里出现两次 = 两个目标（以前只取第一个）");
        Check.Equal(15, hits[0].X, "第一个的中心");
        Check.Equal(65, hits[1].X, "第二个的中心");
        Check.Equal("确定", hits[1].Text, "命中文字跟着每一项走");
    }

    private static void SortByReading()
    {
        OcrLineBox lower = TextLine("确定", 0, 100, 40, 20, Word("确定", 0, 100, 40, 20));
        OcrLineBox upper = TextLine("确定", 0, 10, 40, 20, Word("确定", 0, 10, 40, 20));

        // 故意把靠下的放前面：结果必须自己按位置排好
        IReadOnlyList<TextHit> hits = TextLocator.CollectLines(new[] { lower, upper }, "确定", exact: false);
        Check.Equal(2, hits.Count, "两行都命中");
        Check.Equal(20, hits[0].Y, "第一项是最上面那行（Y 是文字中心：行 top 10 + 高 20/2）");
        Check.Equal(110, hits[1].Y, "下面的排在后面");

        Check.True(TextLocator.TryLocate(new[] { lower, upper }, "确定", out int x, out int y, out _),
            "单命中路径仍然找得到");
        Check.Equal(20, y, "单命中取的也是最上面那个");
        Check.Equal(20, x, "X 是命中文字中心");
    }

    private static void ExactWord()
    {
        // 「保存并关闭」是一个词：找「保存」在包含模式下算，完全一致下不算
        OcrLineBox oneWord = TextLine("保存并关闭", 0, 0, 80, 20,
            Word("保存并关闭", 0, 0, 80, 20));

        Check.Equal(1, TextLocator.CollectLines(new[] { oneWord }, "保存", exact: false).Count,
            "包含：「保存」命中「保存并关闭」");
        Check.Equal(0, TextLocator.CollectLines(new[] { oneWord }, "保存", exact: true).Count,
            "完全一致：不再命中更长的词");

        // 「保存 (Ctrl+S)」是两个词：完全一致仍然命中前一个
        OcrLineBox twoWords = TextLine("保存 (Ctrl+S)", 0, 0, 110, 20,
            Word("保存", 0, 0, 30, 20),
            Word("(Ctrl+S)", 40, 0, 70, 20));

        IReadOnlyList<TextHit> exact = TextLocator.CollectLines(new[] { twoWords }, "保存", exact: true);
        Check.Equal(1, exact.Count, "完全一致：命中的是一个完整的词");
        Check.Equal(15, exact[0].X, "位置是那个词的中心");
        Check.Equal("保存", exact[0].Text, "命中的文字就是这个词");

        Check.Equal(0, TextLocator.CollectLines(new[] { twoWords }, "存", exact: true).Count,
            "完全一致：半个词也不算");
        Check.Equal(1, TextLocator.CollectLines(new[] { twoWords }, "存", exact: false).Count,
            "包含：半个词照旧命中");
    }

    private static void LineWithoutWords()
    {
        OcrLineBox line = TextLine("保存", 10, 20, 60, 30);   // 没有词框

        IReadOnlyList<TextHit> hits = TextLocator.CollectLines(new[] { line }, "保存", exact: true);
        Check.Equal(1, hits.Count, "没有词框时按整行文本判断");
        Check.Equal(40, hits[0].X, "点整行中心 X");
        Check.Equal(35, hits[0].Y, "点整行中心 Y");
        Check.Equal(60, hits[0].W, "宽度用整行");

        Check.Equal(0, TextLocator.CollectLines(new[] { line }, "另存为", exact: false).Count,
            "整行里没有就是不命中");
    }

    private static void DuplicateLines()
    {
        // 同一个按钮被引擎（或两套模型）报了两遍：位置几乎一样，只该留一个
        OcrLineBox a = TextLine("确定", 0, 0, 40, 20, Word("确定", 0, 0, 40, 20));
        OcrLineBox b = TextLine("确定", 1, 1, 40, 20, Word("确定", 1, 1, 40, 20));

        Check.Equal(1, TextLocator.CollectLines(new[] { a, b }, "确定", exact: false).Count,
            "重叠的两条只留一个");
    }

    // ============ 参数与声明 ============

    private static void AllGatesOptions()
    {
        var spec = new FindImageUnit().Spec;
        InputSpec all = spec.Inputs.First(i => i.Key == "all");
        InputSpec limit = spec.Inputs.First(i => i.Key == "limit");
        InputSpec order = spec.Inputs.First(i => i.Key == "order");

        Check.Equal("false", all.Default?.AsText(), "默认只找第一个（老动作行为不变）");

        var step = new Step { Unit = spec.Key };
        Check.False(StepSummary.IsVisible(limit, step, spec), "没勾「找全部」时不显示「最多几个」");
        Check.False(StepSummary.IsVisible(order, step, spec), "没勾「找全部」时不显示「命中顺序」");
        Check.True(StepSummary.IsVisible(all, step, spec), "「找全部」自己始终可见");

        step.In["all"] = StepArg.FromBool(true);
        Check.True(StepSummary.IsVisible(limit, step, spec), "勾上「找全部」就显示「最多几个」");
        Check.True(StepSummary.IsVisible(order, step, spec), "勾上「找全部」就显示「命中顺序」");

        // 找字那边是同一套门（findText 没有顺序，只有数量）
        var textSpec = new FindTextUnit().Spec;
        var textStep = new Step { Unit = textSpec.Key };
        Check.False(StepSummary.IsVisible(textSpec.Inputs.First(i => i.Key == "limit"), textStep, textSpec),
            "找字默认也不显示「最多几个」");
        textStep.In["all"] = StepArg.FromBool(true);
        Check.True(StepSummary.IsVisible(textSpec.Inputs.First(i => i.Key == "limit"), textStep, textSpec),
            "找字勾上「找全部」就显示");
    }

    private static void Declarations()
    {
        var image = new FindImageUnit().Spec;
        Check.Equal(ValueKind.list, image.Outputs.First(o => o.Key == "list").Type, "找图有命中列表");
        Check.Equal("matchList", image.Outputs.First(o => o.Key == "list").DefaultVariable,
            "默认变量名 matchList");
        Check.Equal(ValueKind.number, image.Outputs.First(o => o.Key == "count").Type, "找图有命中个数");
        Check.Equal(ValueKind.number, image.Inputs.First(i => i.Key == "timeoutMs").Type,
            "等待毫秒仍是数字");

        var text = new FindTextUnit().Spec;
        Check.Equal(ValueKind.list, text.Outputs.First(o => o.Key == "list").Type, "找字有命中列表");
        Check.Equal("textList", text.Outputs.First(o => o.Key == "list").DefaultVariable,
            "默认变量名 textList");
        Check.Equal(ValueKind.number, text.Outputs.First(o => o.Key == "count").Type, "找字有命中个数");

        // 命中类动作单元都有「直到找到」开关：勾上就一直找，等待时间那一行收起
        foreach (string key in new[] { "screen.findImage", "screen.findText", "input.clickImage", "input.clickText" })
        {
            if (!StepRegistry.Default.TryGet(key, out IStepUnit unit))
            {
                Check.True(false, key + " 已注册");
                continue;
            }

            UnitSpec spec = unit.Spec;
            InputSpec? until = spec.Inputs.FirstOrDefault(i => i.Key == "untilFound");
            Check.True(until != null, key + " 有「直到找到」");
            Check.False(until!.Advanced, key + " 的「直到找到」不是高级参数");
            Check.Equal(ValueKind.@bool, until.Type, key + " 的「直到找到」是开关");

            InputSpec timeout = spec.Inputs.First(i => i.Key == "timeoutMs");
            var off = new Step { Unit = key };
            off.In["untilFound"] = StepArg.FromBool(false);
            Check.True(StepSummary.IsVisible(timeout, off, spec), key + " 不勾「直到找到」时显示等待时间");

            var on = new Step { Unit = key };
            on.In["untilFound"] = StepArg.FromBool(true);
            Check.False(StepSummary.IsVisible(timeout, on, spec), key + " 勾「直到找到」时收起等待时间");
        }

        // 点击类：默认点第一个，行为与以前一致
        Check.Equal("1", new ClickImageUnit().Spec.Inputs.First(i => i.Key == "index").Default?.AsText(),
            "图片点击默认点第一个");
        Check.Equal("1", new ClickTextUnit().Spec.Inputs.First(i => i.Key == "index").Default?.AsText(),
            "文字点击默认点第一个");
        Check.Equal("contains", new ClickTextUnit().Spec.Inputs.First(i => i.Key == "match").Default?.AsText(),
            "文字点击默认包含匹配");
    }

    // ============ 夹具 ============

    private static OcrWordBox Word(string text, int x, int y, int w, int h) =>
        new() { Text = text, X = x, Y = y, W = w, H = h };

    private static OcrLineBox TextLine(string text, int x, int y, int w, int h, params OcrWordBox[] words) =>
        new() { Text = text, X = x, Y = y, W = w, H = h, Words = words };
}
