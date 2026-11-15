using System.Reflection;
using LTools.Core.Actions;
using LTools.Features.Units.Data;
using LTools.Features.Units.Web;

namespace LTools.Core.SelfTest;

/// <summary>
/// S3-A（`data.number` + `web.download`）：纯函数向量与声明断言。
/// 不碰键鼠 / 窗口 / 剪贴板 / **网络** / 用户配置；下载循环本身留真机 G3 点验。
/// </summary>
internal static class S3ASuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("s3a", "data.number 声明与进制向量", NumberBaseVectors));
        cases.Add(new("s3a", "data.number 舍入 / 取整与 data.calc 一致", Rounding));
        cases.Add(new("s3a", "data.number 千分位 / 百分比格式化", Formatting));
        cases.Add(new("s3a", "data.number 非法输入按前置条件报错", NumberPreconditions));
        cases.Add(new("s3a", "web.download 声明与干跑语义", DownloadDeclaration));
        cases.Add(new("s3a", "web.download 文件名 / 重名纯判定", DownloadNaming));
    }

    private static void NumberBaseVectors()
    {
        var ff = NumberUnit.Compute("255", "toBase", "16", 0, "round45");
        Check.Equal("FF", ff.Text, "255 → FF");
        Check.Equal(255d, ff.Number, "255 的数值输出");
        Check.Equal("FF", ff.Hex, "255 的十六进制输出");
        Check.Equal("11111111", ff.Binary, "255 的二进制输出");

        var back = NumberUnit.Compute("FF", "fromBase", "16", 0, "round45");
        Check.Equal(255d, back.Number, "FF → 255");
        Check.Equal("255", back.Text, "FF 解析出的十进制文本");

        var bin = NumberUnit.Compute("1010", "fromBase", "2", 0, "round45");
        Check.Equal(10d, bin.Number, "1010(2) → 10");
        Check.Equal("10", bin.Text, "1010(2) 解析出的十进制文本");

        Check.Equal("1010", NumberUnit.Compute("10", "toBase", "2", 0, "round45").Text, "10 → 1010(2)");
        Check.Equal("377", NumberUnit.Compute("255", "toBase", "8", 0, "round45").Text, "255 → 377(8)");
        Check.Equal("F", NumberUnit.Compute("15", "toBase", "16", 0, "round45").Text, "15 → F");

        UnitSpec spec = StepRegistry.Default.Get("data.number").Spec;
        spec.Validate();
        Check.Equal(UnitKind.pure, spec.Kind, "data.number 是纯动作单元");
        Check.Equal("文本与数据", spec.Category, "归「文本与数据」，不新建分类");

        InputSpec op = spec.Inputs.Single(i => i.Key == "operation");
        Check.True(op.OptionParams != null, "operation 用 OptionParams 收起参数");
        Check.True(op.OptionParams!["toBase"].Contains("base"), "toBase 显示 base");
        Check.False(op.OptionParams!["toBase"].Contains("digits"), "toBase 不显示 digits");
        Check.True(op.OptionParams!["round"].Contains("rounding"), "round 显示 rounding");
        Check.False(op.OptionParams!["ceil"].Contains("rounding"), "ceil 不显示 rounding");
        Check.False(op.OptionParams!["percent"].Contains("base"), "percent 不显示 base");

        foreach (string option in op.Options!)
            Check.False(string.Equals(OptionLabels.For(op, option), option, StringComparison.Ordinal),
                $"「{option}」有中文名");
        foreach (InputSpec input in spec.Inputs.Where(i => i.Options is { Count: > 0 }))
        foreach (string option in input.Options!)
            Check.False(string.Equals(OptionLabels.For(input, option), option, StringComparison.Ordinal),
                $"{input.Key} 的「{option}」有中文名");
    }

    private static void Rounding()
    {
        Check.Equal(3d, NumberUnit.Round(2.5, 0, "round45"), "0.5 进位");
        Check.Equal(-3d, NumberUnit.Round(-2.5, 0, "round45"), "负数 0.5 远离 0");
        Check.Equal(2d, NumberUnit.Round(2.5, 0, "banker"), "0.5 取偶");
        Check.Equal(4d, NumberUnit.Round(3.5, 0, "banker"), "3.5 取偶到 4");
        Check.Equal(2d, NumberUnit.Round(2.5, 0, "truncate"), "直接截断");
        Check.Equal(-2d, NumberUnit.Round(-2.5, 0, "truncate"), "负向截断朝 0");

        // data.calc.round 走 Math.Round（默认 ToEven），现在委托给 NumberUnit.Round(..., "banker")：
        // 逐点断言两者完全一致，保证既有单元语义没被改掉。
        foreach (var (v, d) in new[] { (2.5, 0), (3.5, 0), (1.25, 1), (1.35, 1), (-2.5, 0), (7.45, 1) })
            Check.Equal(Math.Round(v, d), NumberUnit.Round(v, d, "banker"),
                $"banker 与 Math.Round({v}, {d}) 一致");

        Check.Equal(3d, NumberUnit.Compute("2.5", "round", "10", 0, "round45").Number, "round45 走 0.5 进位");
        Check.Equal(2d, NumberUnit.Compute("2.5", "round", "10", 0, "banker").Number, "banker 走 0.5 取偶");
        Check.Equal(1.3d, NumberUnit.Compute("1.25", "round", "10", 1, "round45").Number, "按小数位进位");
        Check.Equal(1.2d, NumberUnit.Compute("1.25", "round", "10", 1, "banker").Number, "按小数位取偶");
        Check.Equal(2d, NumberUnit.Compute("1.2", "ceil", "10", 0, "round45").Number, "向上取整");
        Check.Equal(-1d, NumberUnit.Compute("-1.2", "ceil", "10", 0, "round45").Number, "负数向上取整");
        Check.Equal(1d, NumberUnit.Compute("1.2", "floor", "10", 0, "round45").Number, "向下取整");
        Check.Equal(-2d, NumberUnit.Compute("-1.2", "floor", "10", 0, "round45").Number, "负数向下取整");
    }

    private static void Formatting()
    {
        var thousands = NumberUnit.Compute("1234567.5", "thousands", "10", 2, "round45");
        Check.Equal("1,234,567.50", thousands.Text, "千分位（2 位小数）");
        Check.Equal(1234567.5d, thousands.Number, "格式化不改数值输出");

        string percent = NumberUnit.Compute("0.256", "percent", "10", 1, "round45").Text;
        Check.Contains(percent, "25.6", "百分比含 25.6");
        Check.Contains(percent, "%", "百分比带 % 号");
        Check.Equal(0.256d, NumberUnit.Compute("0.256", "percent", "10", 1, "round45").Number,
            "百分比不改数值输出");
    }

    private static void NumberPreconditions()
    {
        ActionException bad = Check.Throws<ActionException>(
            () => NumberUnit.Compute("abc", "toBase", "16", 0, "round45"), "非数字应报错");
        Check.Equal(ActionErrorKind.precondition, bad.Kind, "非数字标前置条件");

        Check.Throws<ActionException>(
            () => NumberUnit.Compute("ZZ", "fromBase", "16", 0, "round45"), "非法进制串应报错");
        Check.Throws<ActionException>(
            () => NumberUnit.Compute("1", "unknown", "16", 0, "round45"), "未知方式应报错");
        Check.Throws<ActionException>(
            () => NumberUnit.Compute("1", "toBase", "3", 0, "round45"), "非法进制应报错");
    }

    private static void DownloadDeclaration()
    {
        UnitSpec spec = StepRegistry.Default.Get("web.download").Spec;
        spec.Validate();
        Check.Equal(UnitKind.action, spec.Kind, "下载是有副作用的普通步骤");
        Check.Equal("网络", spec.Category, "归「网络」");
        Check.False(spec.RunsInDryRun, "干跑不下载（不碰网络）");

        foreach (string key in new[] { "url", "savePath", "saveName", "headers", "cookie", "autoRename", "timeoutMs" })
            Check.True(spec.Inputs.Any(i => i.Key == key), $"声明参数 {key}");
        foreach (string key in new[] { "file", "size", "md5", "etag", "found" })
            Check.True(spec.Outputs.Any(o => o.Key == key), $"声明输出 {key}");

        Check.True(spec.Inputs.Single(i => i.Key == "url").Required, "地址必填");
        Check.True(spec.Inputs.Single(i => i.Key == "savePath").Required, "存到目录必填");
        Check.True(spec.Inputs.Single(i => i.Key == "timeoutMs").Advanced,
            "超时是调优项，收进高级区（与 web.request 一致）");

        // 共用网络栈：web.download 不另建 HttpClient，发请求 / 请求头都走 web.request 暴露的静态方法。
        Check.True(typeof(WebRequestUnit).GetMethod("Send", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "web.request 暴露共享 Send");
        Check.True(typeof(WebRequestUnit).GetMethod("ApplyHeaders", BindingFlags.Static | BindingFlags.NonPublic) != null,
            "web.request 暴露共享 ApplyHeaders");
        Check.True(StepRegistry.Default.Get("web.request").Spec.Inputs.All(i => i.Key != "operation"),
            "web.request 没有加 operation（仍是唯一 HTTP 出口）");
    }

    private static void DownloadNaming()
    {
        Check.Equal("report.pdf",
            WebDownloadUnit.GuessName("https://x.test/files/report.pdf?token=1", null), "按网址最后一段取名");
        Check.Equal("中文.txt",
            WebDownloadUnit.GuessName("https://x.test/download",
                "attachment; filename*=UTF-8''%E4%B8%AD%E6%96%87.txt"), "RFC 5987 中文文件名");
        Check.Equal("a.txt",
            WebDownloadUnit.GuessName("https://x.test/download", "attachment; filename=\"a.txt\""),
            "普通 filename= 带引号");
        Check.Equal("download", WebDownloadUnit.GuessName("https://x.test/", null), "取不到名字的兜底");

        Check.Equal("evil.exe", WebDownloadUnit.SanitizeFileName("..\\..\\evil.exe"), "目录穿越只留文件名");
        Check.Equal("a_b.txt", WebDownloadUnit.SanitizeFileName("a:b.txt"), "非法字符换下划线");
        Check.Equal("download", WebDownloadUnit.SanitizeFileName(""), "空文件名兜底");

        string missing = Path.Combine(Path.GetTempPath(),
            "ltools-s3a-" + Guid.NewGuid().ToString("N") + ".bin");
        Check.Equal(missing, WebDownloadUnit.UniquePath(missing), "不存在的路径原样返回");

        string existing = typeof(WebDownloadUnit).Assembly.Location;
        string renamed = WebDownloadUnit.UniquePath(existing);
        Check.NotEqual(existing, renamed, "已存在时改名");
        Check.True(renamed.Contains(" ("), "追加 (N)");
        Check.False(File.Exists(renamed), "改出的新名字当前不存在");
    }
}
