using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 绘制行为设置（照上游 <c>drawing/*</c>）：工具键对照 / 清单归一 / 默认值 / 组首按钮。
/// </summary>
/// <remarks>
/// <para>
/// 默认值逐条对齐 <c>storage/configurationschema.cpp</c>：
/// 自动选中关、排除 <c>free-draw / pen-filter / text / speech-bubble</c>；
/// 禁用快速选择默认只有 <c>free-draw / pen-filter</c>；<c>delete_text_after_cursor</c> 开；
/// 「记住上次工具」「组首按钮常显」关。
/// </para>
/// <para>
/// 这里**不读也不写** <c>configs/settings.json</c>（设置页落到真配置的往返、真键 Delete、
/// 真鼠标快速选择由仓库外 <c>capture-overlay-smoke</c> 的 <c>drawing</c> 段验）。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void DrawingCases()
    {
        // ============ 16 个可选工具的键（上游 drawingSelectableToolOptions 顺序） ============
        Check.Equal(16, CaptureToolIds.Selectable.Count, "可列进设置的工具正好 16 个");
        Check.Equal("shape", CaptureToolIds.Selectable[0].Id, "第一个是「形状」");
        Check.Equal("speech-bubble", CaptureToolIds.Selectable[1].Id, "第二个是「气泡」（上游这个顺序）");
        Check.Equal("serial-number", CaptureToolIds.Selectable[^1].Id, "最后一个是「序号」");

        foreach (var (id, tool) in CaptureToolIds.Selectable)
        {
            Check.Equal(id, CaptureToolIds.Of(tool), $"{tool} → {id}（回得来）");
            Check.Equal(tool, CaptureToolIds.Parse(id), $"{id} → {tool}（回得来）");
            Check.True(CaptureToolIds.Name(id) is { Length: > 0 }, $"{id} 有中文名");
        }

        Check.True(CaptureToolIds.Parse(" polyline ") == null, "没列进设置的键不认（折线不在上游这 16 个里）");
        Check.True(CaptureToolIds.Parse("") == null, "空串不认");
        Check.True(CaptureToolIds.Parse(null) == null, "null 不认");
        Check.True(CaptureToolIds.Of(CaptureTool.Polyline) == null, "折线没有上游键（不进两份清单）");
        Check.True(CaptureToolIds.Of(CaptureTool.Eraser) == null, "橡皮没有上游键");
        Check.Equal("画笔", CaptureToolIds.Name("free-draw"), "free-draw 叫「画笔」");
        Check.Equal("画笔滤镜", CaptureToolIds.Name("pen-filter"), "pen-filter 叫「画笔滤镜」");

        // ============ Normalize：丢未知 / 去重 / 按上游顺序 ============
        var messy = new[] { "text", "shape", "text", "nope", "free-draw" };
        Check.Equal("shape|free-draw|text",
            string.Join("|", CaptureToolIds.Normalize(messy)),
            "按上游顺序排（不按用户写的顺序）、去重、丢掉认不出的");
        Check.Equal(0, CaptureToolIds.Normalize(null).Count, "null = 空清单");
        Check.Equal(0, CaptureToolIds.Normalize(Array.Empty<string>()).Count, "空清单");
        Check.Equal("shape|serial-number",
            string.Join("|", CaptureToolIds.Normalize(new[] { "serial-number", "shape" })),
            "两个键也按上游顺序");
        Check.Equal("形状 / 画笔",
            CaptureToolIds.Describe(new[] { "free-draw", "shape", "free-draw" }),
            "摘要用中文名，顺序与归一一致");
        Check.Equal(string.Empty, CaptureToolIds.Describe(null), "空清单摘要为空串");

        // ============ 默认值（上游 configurationschema） ============
        var file = new DrawingFile();
        Check.False(file.AutomaticallySelectNewlyCreatedElements ?? true, "自动选中默认关");
        Check.True(file.DeleteTextAfterCursor ?? false, "Delete 删光标后的字符默认开");
        Check.False(file.RememberLastUsedTool ?? true, "记住上次工具默认关");
        Check.False(file.AlwaysShowFirstToolbarGroupButton ?? true, "组首按钮常显默认关");

        Check.Equal("free-draw|pen-filter|text|speech-bubble",
            string.Join("|", file.AutomaticSelectionExcludedTools!),
            "自动选中排除默认 4 个（kSchemaStrings33）");
        Check.Equal("free-draw|pen-filter",
            string.Join("|", file.QuickSelectionDisabledTools!),
            "禁用快速选择默认 2 个（kSchemaStrings35）");

        Check.Equal("free-draw|pen-filter|text|speech-bubble",
            string.Join("|", DrawingSettings.DefaultAutoSelectExcluded),
            "常量与落盘默认值同源");
        Check.Equal("free-draw|pen-filter",
            string.Join("|", DrawingSettings.DefaultQuickSelectionDisabled),
            "常量与落盘默认值同源");

        // 默认清单本身是合法的（归一之后一个都不丢）
        Check.Equal(4, CaptureToolIds.Normalize(file.AutomaticSelectionExcludedTools).Count,
            "默认排除清单归一后仍是 4 个");
        Check.Equal(2, CaptureToolIds.Normalize(file.QuickSelectionDisabledTools).Count,
            "默认禁用清单归一后仍是 2 个");

        // ============ 组首按钮（上游 toolbarGroupEntryItemId / entryItemId） ============
        var group = new List<CaptureTool> { CaptureTool.Bracket, CaptureTool.Arrow };
        Check.Equal(CaptureTool.Arrow,
            CaptureToolbar.GroupEntry(group, CaptureTool.Bracket, alwaysShowFirst: true),
            "组首常显：固定显示这一格默认露的那个（position 最后一项 = 箭头）");
        Check.Equal(CaptureTool.Bracket,
            CaptureToolbar.GroupEntry(group, CaptureTool.Bracket, alwaysShowFirst: false),
            "关着：显示这一格上次点过的工具");
        Check.Equal(CaptureTool.Shape,
            CaptureToolbar.GroupEntry(new List<CaptureTool> { CaptureTool.Shape }, CaptureTool.Shape,
                alwaysShowFirst: true),
            "单工具格子不受影响");
        Check.Equal(CaptureTool.Line,
            CaptureToolbar.GroupEntry(new List<CaptureTool> { CaptureTool.Shape, CaptureTool.Line },
                CaptureTool.Line, alwaysShowFirst: false),
            "组里第二项也能当入口（关着时）");
    }
}
