using System.Text.Encodings.Web;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 把值序列化成 JSON 文本（列表 / 对象 → JSON）。是「值 → JSON 文本」的唯一出口：
/// <c>data.json</c> 只管反方向（JSON 文本 → 值）。
/// </summary>
/// <remarks>
/// 为什么不给 <c>data.json</c> 加一个 <c>operation</c>：它的输入键叫 <c>text</c> 且必填，
/// 加反向之后这个「必填」要按操作分叉，连带可见性、必填判断、摘要、步骤文本四处都要分叉；
/// 而「序列化」一句话就能说清，单独一个单元更便宜，索引里也照样能按 <c>produces:"text"</c> 找到。
/// </remarks>
public sealed class JsonEncodeUnit : IStepUnit
{
    private static readonly JsonSerializerOptions Compact = Options(indented: false);
    private static readonly JsonSerializerOptions Indented = Options(indented: true);

    private static JsonSerializerOptions Options(bool indented) => new()
    {
        WriteIndented = indented,
        // 中文不转成 \uXXXX：写进日志 / 贴进对话还得能读。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new ValueJsonConverter() },
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.jsonEncode",
        Name = "JSON 序列化",
        Category = "文本与数据",
        Icon = "save",
        Description = "把列表 / 对象 / 数字 / 布尔转成 JSON 文本（文本会带上引号，这是 JSON 的写法）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("value", "值", Required: true,
                Description: "要序列化的值：整串 ${列表} / ${对象} 保留原类型；直接写文本得到带引号的 JSON 字符串"),
            new InputSpec("indented", "缩进", ValueKind.@bool, InputEditor.boolean,
                Description: "勾上 = 带换行与缩进（给人看）；不勾 = 紧凑一行（给接口 / 日志用）"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "JSON 文本", ValueKind.text, "json",
                Description: "序列化结果：列表 / 对象 / 数字 / 布尔按 JSON 写法，文本带引号"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        Value value = args.Has("value") ? args.Get("value") : Value.Empty;
        ctx.SetOutput("text", Value.FromText(Encode(value, args.Bool("indented"))));
    }

    /// <summary>
    /// 值 → JSON 文本。纯函数，自检直接调它。
    /// </summary>
    /// <remarks>
    /// 值模型只有 text / number / bool / list / object 五种，<see cref="ValueJsonConverter"/> 正好一一对应；
    /// 空值按空字符串序列化（<c>""</c>），不会抛错。
    /// </remarks>
    internal static string Encode(Value value, bool indented) =>
        JsonSerializer.Serialize(value, indented ? Indented : Compact);
}
