using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>时间：当前时间 / 按格式输出 / 偏移。</summary>
public sealed class TimeUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.time",
        Name = "时间",
        Category = "文本与数据",
        Icon = "clock",
        Description = "取当前时间并按格式输出；可加偏移毫秒",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("format", "格式", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.timeFormat | PickerKind.inline | PickerKind.clear,
                Default: Value.FromText("yyyy-MM-dd HH:mm:ss")),
            new InputSpec("offsetMs", "偏移毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "时间文本", ValueKind.text, "time", Description: "格式化后的时间文本"),
            new OutputSpec("timestamp", "Unix 毫秒", ValueKind.number, Description: "Unix 时间戳（秒）"),
            new OutputSpec("date", "日期", ValueKind.text, Description: "日期部分（yyyy-MM-dd）"),
            new OutputSpec("clock", "时分秒", ValueKind.text, Description: "时间部分（HH:mm:ss）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var now = DateTime.Now.AddMilliseconds(args.Number("offsetMs"));
        string format = args.Text("format");
        if (format.Length == 0) format = "yyyy-MM-dd HH:mm:ss";

        ctx.SetOutput("text", Value.FromText(now.ToString(format, CultureInfo.InvariantCulture)));
        ctx.SetOutput("timestamp", Value.FromNumber(
            new DateTimeOffset(now).ToUnixTimeMilliseconds()));
        ctx.SetOutput("date", Value.FromText(now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        ctx.SetOutput("clock", Value.FromText(now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
    }
}
