using LTools.Core.Actions;
using LTools.Views;

namespace LTools.Core.SelfTest;

/// <summary>
/// P3「编辑体验」里的纯逻辑：步骤行状态 chip、摘要参数上限、自动存盘的三条路径、撤销快照深拷贝。
/// </summary>
/// <remarks>
/// <para>
/// 这一组刻意不碰 WPF：chip 判定返回的是纯数据（<see cref="StepChip"/>），
/// 摘要与自动存盘都是静态函数，撤销快照是普通对象——所有断言都能在 <c>--selftest</c> 里直接跑。
/// </para>
/// <para>
/// 自动存盘用自测自己的临时 root（<see cref="EditorAutosave.DirFor"/> 的 <c>root</c> 参数），
/// 绝不碰用户数据根下的 <c>cache/editor/</c>。
/// </para>
/// </remarks>
internal static class PuritySuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("purity", "步骤行 chip 矩阵", Chips));
        cases.Add(new("purity", "chip 时长格式", ChipDuration));
        cases.Add(new("purity", "摘要最多取 3 个参数", SummaryCap));
        cases.Add(new("purity", "自动存盘写入 / 读回 / 删除", AutosaveRoundTrip));
        cases.Add(new("purity", "自动存盘过期判定", AutosaveFreshness));
        cases.Add(new("purity", "撤销快照深拷贝", UndoDeepClone));
    }

    // ============ chip ============

    private static void Chips()
    {
        // 干净的一步：一个 chip 都没有（行尾不占地方，ChipsVisibility 收起）。
        Check.Equal(0, StepRowChips.Describe(Step.Of("flow.delay")).Count, "普通步骤不该有 chip");

        // 每个开关单独出现时都对得上。
        Check.Equal("跳过", Single(Step.Of("flow.delay").Skip()).Text, "跳过");
        Check.Equal("失败继续", Single(Step.Of("flow.delay").ContinueOnFail()).Text, "失败继续");
        Check.Equal("⟳3", Single(Step.Of("flow.delay").RetryTimes(3, 800)).Text, "重试");
        Check.Equal("延迟 2s", Single(Step.Of("flow.delay").Wait(2000)).Text, "延迟");

        var note = Step.Of("flow.delay");
        note.Note = "  等一下再点  ";
        StepChip noteChip = Single(note);
        Check.Equal("备注", noteChip.Text, "备注 chip 的标题只是「备注」");
        Check.Equal("等一下再点", noteChip.Tip, "备注内容去空白后放进悬停说明");

        // 顺序固定：跳过 → 失败继续 → 重试 → 延迟 → 备注。
        var all = Step.Of("flow.delay").Skip().ContinueOnFail().RetryTimes(2, 500).Wait(1500);
        all.Note = "收尾";
        var chips = StepRowChips.Describe(all);
        Check.Equal(5, chips.Count, "五项都设上就是五个 chip");
        Check.Equal("跳过|失败继续|⟳2|延迟 1.5s|备注",
            string.Join("|", chips.Select(c => c.Text)), "chip 顺序固定");

        // 提醒色只给「会影响执行结果」的三项；延迟与备注是中性色。
        Check.Equal("True|True|True|False|False",
            string.Join("|", chips.Select(c => c.Warn)), "只有跳过 / 失败继续 / 重试用提醒色");

        // 悬停说明要把具体数字说清楚，别只说「重试」。
        Check.Contains(chips[2].Tip, "2 次", "重试 chip 的说明带次数");
        Check.Contains(chips[2].Tip, "500 毫秒", "重试 chip 的说明带间隔");
        Check.Contains(chips[3].Tip, "1500 毫秒", "延迟 chip 的说明带毫秒数");

        // 空白备注不算备注（否则每个动过的步骤都会多一个空 chip）。
        var blank = Step.Of("flow.delay");
        blank.Note = "   ";
        Check.Equal(0, StepRowChips.Describe(blank).Count, "空白备注不产生 chip");
    }

    private static void ChipDuration()
    {
        Check.Equal("250ms", StepRowChips.Duration(250), "不到 1 秒按毫秒");
        Check.Equal("500ms", StepRowChips.Duration(500), "500 毫秒");
        Check.Equal("999ms", StepRowChips.Duration(999), "刚好不到 1 秒仍是毫秒");
        Check.Equal("1s", StepRowChips.Duration(1000), "整秒不写小数");
        Check.Equal("1.5s", StepRowChips.Duration(1500), "半秒保留一位小数");
        Check.Equal("2s", StepRowChips.Duration(2000), "2 秒");
        Check.Equal("12.3s", StepRowChips.Duration(12300), "带小数的秒");
    }

    // ============ 摘要 ============

    private static void SummaryCap()
    {
        // web.request（POST）同时可见 4 个非默认参数：url / method / body / headers。
        Step step = Step.Of("web.request")
            .WithText("url", "https://example.com/api")
            .WithText("method", "post")
            .WithText("body", "{\"a\":1}")
            .WithText("headers", "Authorization: Bearer x");

        string summary = StepSummary.Describe(step);
        Check.Contains(summary, "https://example.com/api", "摘要第一个参数是地址");
        Check.Contains(summary, "POST 提交", "枚举参数按中文名进摘要");
        Check.Contains(summary, "{\"a\":1}", "摘要第三个参数是请求体");
        Check.NotContains(summary, "Authorization", "第 4 个参数被上限截掉");

        // GET 只剩 2 个可见参数：不该为了凑 3 个而带上不可见的请求体 / 请求头。
        Step get = Step.Of("web.request").WithText("url", "https://example.com/api");
        string shortSummary = StepSummary.Describe(get);
        Check.Contains(shortSummary, "https://example.com/api", "GET 摘要带地址");
        Check.NotContains(shortSummary, "请求体", "不可见参数不进摘要");
    }

    // ============ 自动存盘 ============

    private static void AutosaveRoundTrip()
    {
        string root = TempRoot();
        try
        {
            const string id = "01arz3ndektsv4rrffq69g5fav";
            string path = EditorAutosave.FileFor(id, root);

            Check.False(File.Exists(path), "一开始没有自动存盘文件");
            Check.True(EditorAutosave.TryRead(id, root) == null, "没有文件时读回 null");

            var program = new ActionProgram
            {
                Id = id,
                Name = "抓取行情",
                Icon = "globe",
                Description = "每天开盘前跑一次",
            };
            program.Variables.Add(new VariableDef { Key = "url", Type = ValueKind.text });
            program.Steps.Add(Step.Of("web.request").WithText("url", "https://example.com/api"));

            Check.True(EditorAutosave.Write(id, program, root), "写入成功");
            Check.True(File.Exists(path), "文件落在 cache/editor/<id>.autosave.json");
            Check.Equal(id + ".autosave.json", Path.GetFileName(path), "文件名就是 Id + 后缀");

            ActionProgram? back = EditorAutosave.TryRead(id, root);
            Check.True(back != null, "读回来不是 null");
            Check.Equal("抓取行情", back?.Name, "名称读回来了");
            Check.Equal("每天开盘前跑一次", back?.Description, "说明读回来了");
            Check.Equal(1, back?.Variables.Count ?? 0, "变量表读回来了");
            Check.Equal(1, back?.Steps.Count ?? 0, "步骤读回来了");
            Check.Equal("web.request", back?.Steps[0].Unit, "步骤的动作单元读回来了");
            Check.Equal("https://example.com/api", back?.Steps[0].In["url"].Literal.AsText(), "步骤参数读回来了");

            Check.True(EditorAutosave.TimeText(id, root).Length > 0, "能给出「它在什么时候存的」");

            EditorAutosave.Delete(id, root);
            Check.False(File.Exists(path), "删除后文件不在了");
            Check.True(EditorAutosave.TryRead(id, root) == null, "删除后读回 null");
            Check.Equal("", EditorAutosave.TimeText(id, root), "删除后时间文案是空串");

            // Id 为空：不写、不读、不删，也不抛（新动作还没保存时就是这种状态）。
            Check.False(EditorAutosave.Write("", program, root), "Id 为空不写");
            Check.True(EditorAutosave.TryRead("", root) == null, "Id 为空读回 null");
            EditorAutosave.Delete("", root);
            Check.Equal(0, Directory.Exists(EditorAutosave.DirFor(root))
                ? Directory.GetFiles(EditorAutosave.DirFor(root)).Length
                : 0, "Id 为空没有留下文件");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void AutosaveFreshness()
    {
        string root = TempRoot();
        try
        {
            const string id = "01arz3ndektsv4rrffq69g5fav";
            var program = new ActionProgram { Id = id, Name = "草稿" };
            EditorAutosave.Write(id, program, root);

            string autoPath = EditorAutosave.FileFor(id, root);
            File.SetLastWriteTimeUtc(autoPath, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            // 正式文件不存在：这份自动存盘就是唯一的内容，算「新」。
            Check.True(EditorAutosave.IsNewerThan(id, "", root), "没有正式文件时自动存盘算新");

            string source = Path.Combine(root, "action.json");
            File.WriteAllText(source, "{}");
            File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Check.True(EditorAutosave.IsNewerThan(id, source, root), "正式文件更旧时应恢复");

            File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
            Check.False(EditorAutosave.IsNewerThan(id, source, root), "正式文件更新时自动存盘已过期");

            // 没有自动存盘文件：永远算「不新」，不会弹「恢复吗」。
            Check.False(EditorAutosave.IsNewerThan("01otherid0000000000000000", source, root),
                "没有自动存盘时不提示恢复");

            // 正式文件路径为空当成「不存在」。
            Check.True(EditorAutosave.IsNewerThan(id, "", root), "空路径按不存在处理");
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ============ 撤销 ============

    private static void UndoDeepClone()
    {
        var state = new EditorUndoState(
            new List<Step> { Step.Of("flow.delay").WithText("ms", "1000") },
            new List<VariableDraft>
            {
                new("count", ValueKind.number, VariableDirection.local, "1", "手动创建", false, "count"),
            },
            "删除步骤");

        EditorUndoState clone = state.DeepClone();

        Check.Equal(state.Steps.Count, clone.Steps.Count, "步骤数量一致");
        Check.Equal(state.Variables.Count, clone.Variables.Count, "变量数量一致");
        Check.Equal("删除步骤", clone.Label, "标签一起复制");

        // 步骤是深拷贝：改克隆里的参数不影响原来那份。
        Check.False(ReferenceEquals(state.Steps[0], clone.Steps[0]), "步骤对象不是同一个");
        clone.Steps[0].WithText("ms", "2000");
        Check.Equal("1000", state.Steps[0].In["ms"].Literal.AsText(), "改克隆不影响原快照");

        // 变量行是不可变 record，但列表本身要各自一份。
        Check.False(ReferenceEquals(state.Variables, clone.Variables), "变量列表不是同一个");
        clone.Variables.Add(new VariableDraft("x", ValueKind.text, VariableDirection.local, "", "手动创建", false, "x"));
        Check.Equal(1, state.Variables.Count, "往克隆里加变量不影响原快照");

        // 原始快照再拷一份仍保持内容（撤销 / 重做互相搬运时反复调）。
        Check.Equal("count", state.DeepClone().Variables[0].Name, "再拷一份内容不变");
    }

    // ============ 工具 ============

    private static StepChip Single(Step step)
    {
        var chips = StepRowChips.Describe(step);
        Check.Equal(1, chips.Count, "应正好有一个 chip（单元 " + step.Unit + "）");
        return chips.Count > 0 ? chips[0] : new StepChip("", "", false);
    }

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "ltools-selftest", "autosave-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响结论。
        }
    }
}

/// <summary>自测里的步骤快捷构造（只为让上面的断言读起来像一句话）。</summary>
internal static class PurityStepExtensions
{
    public static Step Skip(this Step step)
    {
        step.Disabled = true;
        return step;
    }

    public static Step ContinueOnFail(this Step step)
    {
        step.OnFail = FailMode.@continue;
        return step;
    }

    public static Step RetryTimes(this Step step, int times, int delayMs)
    {
        step.Retry = times;
        step.RetryDelayMs = delayMs;
        return step;
    }

    public static Step Wait(this Step step, int delayMs)
    {
        step.DelayMs = delayMs;
        return step;
    }
}
