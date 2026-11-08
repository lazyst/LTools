using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>读取屏幕某一点的颜色。</summary>
public sealed class PixelUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.pixel",
        Name = "取像素颜色",
        Category = "屏幕",
        Icon = "color",
        Description = "读取虚拟桌面物理坐标处像素的颜色（#RRGGBB）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("x", "X", ValueKind.number, InputEditor.number, Required: true),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number, Required: true),
        },
        Outputs = new[]
        {
            new OutputSpec("color", "颜色", ValueKind.text, "color", Description: "颜色，形如 #RRGGBB"),
            new OutputSpec("r", "红", ValueKind.number, Description: "红色分量（0–255）"),
            new OutputSpec("g", "绿", ValueKind.number, Description: "绿色分量（0–255）"),
            new OutputSpec("b", "蓝", ValueKind.number, Description: "蓝色分量（0–255）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        int x = (int)Math.Round(args.Number("x"));
        int y = (int)Math.Round(args.Number("y"));
        string color = ScreenCapture.GetPixel(x, y);

        ctx.SetOutput("color", Value.FromText(color));

        int r = 0, g = 0, b = 0;
        string hex = color.TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            r = (rgb >> 16) & 0xFF;
            g = (rgb >> 8) & 0xFF;
            b = rgb & 0xFF;
            ctx.SetOutput("r", Value.FromNumber(r));
            ctx.SetOutput("g", Value.FromNumber(g));
            ctx.SetOutput("b", Value.FromNumber(b));
        }

        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(color)),
            ("color", Value.FromText(color)),
            ("r", Value.FromNumber(r)),
            ("g", Value.FromNumber(g)),
            ("b", Value.FromNumber(b))));
    }
}
