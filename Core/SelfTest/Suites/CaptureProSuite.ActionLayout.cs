using System.Linq;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图工具条「完成动作」布局（P9-d4，照上游 <c>screenshot_toolbar/action_tools_layout</c> 与
/// <c>screenshottoolbarlayoutmodel.h</c> 的 <c>normalizedLayout</c> / <c>defaultPositions</c>）：
/// 默认分组、键往返、归一（未知键 / 重复 / 空组）、隐藏与位置的互斥、落盘往返、
/// 以及「编辑模式里哪些动作会自动跳过」。
/// </summary>
/// <remarks>
/// 真窗口那一段（工具条按布局排按钮 / 编辑器改完立刻落盘）在仓库外 <c>capture-overlay-smoke</c>
/// 的 <c>actionlayout</c> 段；这里只碰纯数据。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ActionLayoutCases()
    {
        // ============ 默认布局（照上游 actionDefaultPositions） ============

        var defaults = CaptureActionLayout.Default();
        Check.Equal(9, defaults.Groups.Count, "默认分 9 叠（中间一条竖线不算一叠）");
        Check.Equal(13, defaults.VisibleCount, "默认 13 个动作全在工具条上");
        Check.Equal(0, defaults.Hidden.Count, "默认一个都不收起来");
        Check.Equal(1, defaults.Positions.Count(p => p.IsSeparator), "默认只有一条竖线（照上游：取消前面）");
        Check.Equal("recognize,qrcode", string.Join(",", defaults.Groups[0].Select(CaptureActionLayout.KeyOf)),
            "第一叠：识别 + 二维码");
        Check.Equal("cancel", string.Join(",", defaults.Groups[^2].Select(CaptureActionLayout.KeyOf)),
            "竖线后面是「取消」");
        Check.Equal("copy", string.Join(",", defaults.Groups[^1].Select(CaptureActionLayout.KeyOf)),
            "最后一叠：复制（与取消相邻、中间没有竖线，照上游）");
        Check.Equal(
            "识别文字 显示二维码 │ 录屏 │ 贴图到屏幕 实时贴图 │ 识别并翻译 │ 长截图 │ 打印 快速保存 另存为 │ 更多 │ | │ 取消 │ 复制",
            defaults.Describe(), "摘要把每一叠与中文名都写出来（竖线写 |）");

        // ============ 键往返 ============

        foreach (var tool in CaptureActionLayout.Catalog)
            Check.Equal(tool, CaptureActionLayout.ToolOf(CaptureActionLayout.KeyOf(tool)),
                $"{CaptureActionLayout.LabelOf(tool)} 的键能认回来");
        Check.True(CaptureActionLayout.ToolOf("not-a-tool") is null, "认不出的键给 null（不做兼容 / 别名）");
        Check.True(CaptureActionLayout.ToolOf("") is null, "空键给 null");
        Check.Equal(13, CaptureActionLayout.Catalog.Count, "目录 13 个动作");

        // ============ 归一：未知键丢掉 / 重复只算一次 / 空组丢掉 ============

        var parsed = CaptureActionLayout.Normalize(
            new[] { "recognize,qrcode,not-a-tool", "", "qrcode,record-screen", "pin-to-screen" },
            new[] { "print", "print", "recognize", "bogus" },
            appendMissing: false);

        Check.Equal(3, parsed.Groups.Count, "未知键 / 空行丢掉后剩 3 组");
        Check.Equal("recognize,qrcode", string.Join(",", parsed.Groups[0].Select(CaptureActionLayout.KeyOf)),
            "重复的 qrcode 只留第一次");
        Check.Equal("record-screen", string.Join(",", parsed.Groups[1].Select(CaptureActionLayout.KeyOf)),
            "第二组只剩 record-screen（qrcode 已在第一组算过）");
        Check.Equal("pin-to-screen", string.Join(",", parsed.Groups[2].Select(CaptureActionLayout.KeyOf)),
            "第三组原样");
        Check.True(parsed.Hidden.Contains(CaptureActionTool.Print), "隐藏的 print 认出来了");
        Check.False(parsed.Hidden.Contains(CaptureActionTool.Recognize),
            "既在位置里又在隐藏里 → 按位置算（照上游 normalizedLayout）");
        Check.False(parsed.Hidden.Contains(CaptureActionTool.QrCode), "认不出的隐藏键丢掉");
        Check.Equal(5, parsed.VisibleCount + parsed.Hidden.Count,
            "appendMissing = false：只认写下来的那几个");

        // Parse = Normalize + 把目录里新增、两处都没提到的动作按默认分组补回来
        var appended = CaptureActionLayout.Parse(
            new[] { "recognize,qrcode,not-a-tool", "", "qrcode,record-screen", "pin-to-screen" },
            new[] { "print", "print", "recognize", "bogus" });
        Check.True(appended.Groups.SelectMany(g => g).Contains(CaptureActionTool.Copy),
            "没提到的 copy 自动补回来（新版加动作不用改配置文件）");
        Check.Equal(13, appended.VisibleCount + appended.Hidden.Count,
            "补回来之后每个动作要么可见要么隐藏");
        Check.Equal(3, appended.Groups.Count,
            "补回来的动作并进最后一叠（3 叠原有，不再各起一叠）");
        Check.True(appended.Describe().Contains("复制"), "摘要里有补回来的动作");

        // 全空 = 默认布局（首次启动 / 老配置）
        var empty = CaptureActionLayout.Parse(null, null);
        Check.Equal(9, empty.Groups.Count, "两个字段都空 → 默认布局");
        Check.Equal(0, empty.Hidden.Count, "两个字段都空 → 一个都不隐藏");
        Check.Equal(9, CaptureActionLayout.Parse(new string[0], new string[0]).Groups.Count,
            "空集合也按默认布局");

        // ============ 落盘往返 ============

        var lines = defaults.ToLines();
        var hidden = defaults.ToHidden();
        Check.Equal(10, lines.Count, "一行一叠（竖线也占一行）");
        Check.Equal("recognize,qrcode", lines[0], "第一行就是第一叠");
        Check.Equal("-", lines[7], "第八行是那条竖线");
        Check.Equal(0, hidden.Count, "没有隐藏项时是空集合");
        Check.Equal(defaults.Describe(), CaptureActionLayout.Parse(lines, hidden).Describe(),
            "写出去再读回来完全一致");

        // ============ FromGroups（设置页编辑器保存走这里） ============

        var built = CaptureActionLayout.FromGroups(new[]
        {
            new[] { CaptureActionTool.Copy, CaptureActionTool.Cancel },
            new CaptureActionTool[0],
            new[] { CaptureActionTool.Copy, CaptureActionTool.Print },
        }, new[] { CaptureActionTool.Record, CaptureActionTool.Copy });

        Check.Equal(2, built.Groups.Count, "空组丢掉");
        Check.Equal(2, built.Groups[0].Count, "第一组两个");
        Check.Equal(1, built.Groups[1].Count, "第二组只剩 print（copy 重复）");
        Check.True(built.Hidden.Contains(CaptureActionTool.Record), "隐藏项认出来");
        Check.False(built.Hidden.Contains(CaptureActionTool.Copy),
            "既在位置里又在隐藏里 → 按位置算");
        Check.Equal(4, built.Hidden.Count + built.VisibleCount,
            "FromGroups 不补新动作（编辑器里的清单就是全部）");

        Check.Equal(9, CaptureActionLayout.FromGroups(new CaptureActionTool[0][], new CaptureActionTool[0])
            .Groups.Count, "什么都不给 = 默认布局");

        // ============ 编辑模式里用不到的动作自动跳过 ============

        Check.True(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Recognize, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式有识别");
        Check.True(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Copy, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式有复制");
        Check.True(CaptureToolbar.ActionAvailableIn(CaptureActionTool.SaveAs, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式有另存为");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Scroll, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有长截图");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Record, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有录屏");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Live, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有实时贴图");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.More, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有「更多」");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Cancel, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有取消（Esc 只退裁剪）");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.QrCode, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有二维码开关");
        Check.False(CaptureToolbar.ActionAvailableIn(CaptureActionTool.Translate, CaptureToolbar.ToolbarMode.Edit),
            "编辑模式没有「识别并翻译」（图片编辑窗里没有识别流程）");

        foreach (var tool in CaptureActionLayout.Catalog)
            Check.True(CaptureToolbar.ActionAvailableIn(tool, CaptureToolbar.ToolbarMode.Capture),
                $"截图模式全都能用：{CaptureActionLayout.LabelOf(tool)}");
    }
}
