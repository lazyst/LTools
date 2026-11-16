using LTools.Core.Actions;
using LTools.Features.Units.Data;

namespace LTools.Core.SelfTest;

/// <summary>
/// <c>data.list</c> 的聚合操作（<c>sum</c> / <c>avg</c>）：纯函数向量 + 失败语义 + 声明。
/// </summary>
/// <remarks>
/// 「一列数字求和」以前只能 <c>flow.forEach</c> + <c>data.calc</c> 或脚本；
/// 补这两个操作之后它就是一步。断言盯住两件容易改坏的事：
/// 空列表是 0（不报错），有非数字项**整步失败**并指出第几项。
/// </remarks>
internal static class ListSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("list", "data.list 求和 / 平均", Aggregate));
        cases.Add(new("list", "data.list 聚合的声明与失败语义", AggregateRules));
    }

    private static void Aggregate()
    {
        Check.Equal(6d, ListUnit.Aggregate(Items("1", "2", "3"), "sum"), "1+2+3");
        Check.Equal(2d, ListUnit.Aggregate(Items("1", "2", "3"), "avg"), "(1+2+3)/3");
        Check.Equal(-1.5, ListUnit.Aggregate(Items("-3", "1.5"), "sum"), "负号与小数");
        Check.Equal(0.5, ListUnit.Aggregate(Items("0.25", "0.75"), "avg"), "小数平均");
        Check.Equal(0d, ListUnit.Aggregate(Array.Empty<Value>(), "sum"), "空列表求和 = 0");
        Check.Equal(0d, ListUnit.Aggregate(Array.Empty<Value>(), "avg"), "空列表平均 = 0（不报错）");

        // 数字项本身就是数字值时也要能算（正则拿到的是文本，数据步骤拿到的是数字）
        Check.Equal(3d, ListUnit.Aggregate(new[] { Value.FromNumber(1), Value.FromNumber(2) }, "sum"),
            "数字类型的项");
    }

    private static void AggregateRules()
    {
        UnitSpec spec = StepRegistry.Default.Get("data.list").Spec;
        spec.Validate();

        InputSpec op = spec.Inputs.Single(i => i.Key == "operation");
        Check.True(op.Options!.Contains("sum") && op.Options!.Contains("avg"), "下拉里有 sum / avg");
        Check.Equal("list", string.Join(",", op.OptionParams!["sum"]), "sum 只显示「列表」");
        Check.Equal("list", string.Join(",", op.OptionParams!["avg"]), "avg 只显示「列表」");
        Check.False(op.OptionParams!["sum"].Contains("separator"), "sum 不显示分隔符");

        Check.Equal("求和", OptionLabels.For(op, "sum"), "sum 的中文名");
        Check.Equal("平均值", OptionLabels.For(op, "avg"), "avg 的中文名");

        string message = "";
        try { ListUnit.Aggregate(Items("1", "12元"), "sum"); }
        catch (ActionException ex) { message = ex.Message; }

        Check.True(message.Contains("第 2 项"), "非数字项要指出第几项：" + message);
        Check.True(message.Contains("12元"), "报错要带原始文本：" + message);
    }

    private static List<Value> Items(params string[] texts) =>
        texts.Select(Value.FromText).ToList();
}
