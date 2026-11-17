using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录制期可以用的标注工具（照 Snow Shot 录屏工具条的第一行）。
/// </summary>
/// <remarks>
/// 只列「真的会落到 <see cref="Mark"/> 上」的那几个：选择 / 矩形 / 箭头 / 画笔 / 荧光笔 /
/// 文字 / 序号 / 滤镜（马赛克）/ 橡皮 / 水印。截图覆盖层另有椭圆、折线、聚光等，
/// 录屏面板不摆（Snow Shot 的录屏条也没有）。
/// </remarks>
internal enum RecordTool
{
    Select,
    Rect,
    Arrow,
    Pen,
    Highlighter,
    Text,
    Serial,
    Filter,
    Eraser,
    Watermark,
}

/// <summary>录屏工具条上每个标注工具的图标键与说明（面板与 <c>--selftest</c> 共用）。</summary>
internal static class RecordTools
{
    /// <summary>按显示顺序排列的全部标注工具（面板第一行就是照这个顺序建的）。</summary>
    public static readonly IReadOnlyList<RecordTool> All = new[]
    {
        RecordTool.Select, RecordTool.Rect, RecordTool.Arrow, RecordTool.Pen, RecordTool.Highlighter,
        RecordTool.Text, RecordTool.Serial, RecordTool.Filter, RecordTool.Eraser, RecordTool.Watermark,
    };

    /// <summary>图标资源键（<c>Views/CapturePro/CaptureIcons.xaml</c> 里的 <c>IconXxx</c>）。</summary>
    public static string IconOf(RecordTool tool) => tool switch
    {
        RecordTool.Select => "IconSelect",
        RecordTool.Rect => "IconRect",
        RecordTool.Arrow => "IconArrow",
        RecordTool.Pen => "IconPen",
        RecordTool.Highlighter => "IconHighlighter",
        RecordTool.Text => "IconText",
        RecordTool.Serial => "IconSerial",
        RecordTool.Filter => "IconFilter",
        RecordTool.Eraser => "IconEraser",
        RecordTool.Watermark => "IconWatermark",
        _ => "IconSelect",
    };

    /// <summary>悬停提示（Snow Shot 的 tooltip 文案：说清它会落成什么）。</summary>
    public static string TipOf(RecordTool tool) => tool switch
    {
        RecordTool.Select => "选择 / 移动标注",
        RecordTool.Rect => "矩形标注",
        RecordTool.Arrow => "箭头标注",
        RecordTool.Pen => "画笔",
        RecordTool.Highlighter => "荧光笔",
        RecordTool.Text => "文字标注",
        RecordTool.Serial => "序号标注（自动累加）",
        RecordTool.Filter => "马赛克 / 滤镜",
        RecordTool.Eraser => "橡皮：点掉一条标注",
        RecordTool.Watermark => "水印",
        _ => "",
    };

    /// <summary>拖出来的形状至少要这么大才算数（DIP）；画笔 / 荧光笔不看这个。</summary>
    public const double MinimumSize = 4;

    /// <summary>这些工具是「点一下就落一条」，不靠拖。</summary>
    public static bool IsClickTool(RecordTool tool) =>
        tool is RecordTool.Serial or RecordTool.Text or RecordTool.Watermark or RecordTool.Eraser
            or RecordTool.Select;
}
