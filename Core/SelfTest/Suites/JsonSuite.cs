using System.Text.Json;
using LTools.Core.Actions;
using LTools.Features.Units.Data;

namespace LTools.Core.SelfTest;

/// <summary>
/// <c>data.jsonEncode</c>（值 → JSON 文本）：纯函数向量 + 往返 + 声明。
/// </summary>
/// <remarks>
/// 这个单元是 <c>data.json</c>（JSON 文本 → 值）的反方向。断言盯住三件容易改坏的事：
/// 文本要带引号（这是 JSON 语义）、中文不转成 <c>\uXXXX</c>、
/// 序列化结果再解析回来是同一个值（与 <c>ValueJsonConverter</c> 严格互逆）。
/// </remarks>
internal static class JsonSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("json", "data.jsonEncode 值与 JSON 文本互转", Vectors));
        cases.Add(new("json", "data.jsonEncode 声明与缩进开关", Declaration));
    }

    private static void Vectors()
    {
        Check.Equal("[\"a\",\"b\"]", Encode(Value.FromList([Value.FromText("a"), Value.FromText("b")])), "文本列表");
        Check.Equal("[]", Encode(Value.FromList()), "空列表");
        Check.Equal("{}", Encode(Value.Object()), "空对象");
        Check.Equal("{\"k\":1,\"t\":\"v\"}", Encode(Value.Object(("k", Value.FromNumber(1)), ("t", Value.FromText("v")))),
            "对象保留键序");
        Check.Equal("3", Encode(Value.FromNumber(3)), "整数不写成 3.0");
        Check.Equal("1.5", Encode(Value.FromNumber(1.5)), "小数");
        Check.Equal("true", Encode(Value.FromBool(true)), "布尔");
        Check.Equal("false", Encode(Value.FromBool(false)), "假");
        Check.Equal("\"abc\"", Encode(Value.FromText("abc")), "文本带引号");
        Check.Equal("\"\"", Encode(Value.Empty), "空文本 = 空串");
        Check.Equal("\"中文\"", Encode(Value.FromText("中文")), "中文不转义成 \\uXXXX");
        Check.Equal("\"a\\\"b\\nc\"", Encode(Value.FromText("a\"b\nc")), "引号与换行按 JSON 转义");

        // 往返：序列化 → 解析 → 再序列化，文本必须一致（与 data.json 共用的 ValueJsonConverter）
        Value nested = Value.Object(
            ("list", Value.FromList([Value.FromNumber(1), Value.FromBool(false)])),
            ("text", Value.FromText("x\ty")),
            ("obj", Value.Object(("n", Value.FromNumber(-2.25)))));
        string once = Encode(nested);
        Check.Equal(once, Encode(Decode(once)), "嵌套值往返不变");
        Check.Equal(ValueKind.@object, Decode(once).Kind, "解析回来还是对象");
    }

    private static void Declaration()
    {
        UnitSpec spec = StepRegistry.Default.Get("data.jsonEncode").Spec;
        spec.Validate();

        Check.Equal("data.jsonEncode", spec.Key, "单元键");
        Check.Equal(UnitKind.pure, spec.Kind, "是取值单元");
        Check.True(spec.RunsInDryRun, "干跑照跑（不注入键鼠）");

        InputSpec value = spec.Inputs.Single(i => i.Key == "value");
        Check.True(value.Type is null, "值不限类型（列表 / 对象原样进来）");
        Check.True(value.Required, "值必填（空值序列化成 \"\" 会误导）");
        Check.Contains(value.Description, "${列表}", "说明里写清整串变量保留类型");

        InputSpec indented = spec.Inputs.Single(i => i.Key == "indented");
        Check.Equal(ValueKind.@bool, indented.Type, "缩进是开关");
        Check.False(indented.Required, "缩进不必填");
        Check.Equal(PickerKind.none, indented.PickerSet, "开关参数不该挂来源菜单");

        OutputSpec text = spec.Outputs.Single(o => o.Key == "text");
        Check.Equal(ValueKind.text, text.Type, "输出是文本");
        Check.Equal("json", OutputNaming.Suggest(spec.Key, text), "默认变量名 = json");

        // 缩进只影响排版，不影响语义
        string pretty = JsonEncodeUnit.Encode(Value.FromList([Value.FromNumber(1)]), indented: true);
        Check.Contains(pretty, "\n", "缩进版有换行");
        Check.False(Encode(Value.FromList([Value.FromNumber(1)])).Contains('\n'), "紧凑版不换行");
        Check.Equal(Encode(Decode(pretty)), Encode(Value.FromList([Value.FromNumber(1)])), "缩进版解析回同一个值");
    }

    private static string Encode(Value value) => JsonEncodeUnit.Encode(value, indented: false);

    private static Value Decode(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return ValueJsonConverter.FromElement(doc.RootElement);
    }
}
