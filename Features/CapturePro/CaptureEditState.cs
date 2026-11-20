using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一次截图「可重新编辑的内容」：当时画在上面的标注、撤销 / 重做栈、以及选区外观。
/// </summary>
/// <remarks>
/// 照上游 <c>CaptureHistoryDraft</c> 的 <c>canvasHistory</c> + <c>selection</c>：采集历史里点
/// 「重新编辑截图」会把这三样连同底图 / 选区一起摆回覆盖层，所以用户能接着改、能 Ctrl+Z 退回去，
/// 而不是面对一张烘好的成品图。新建一条采集时由 <see cref="CaptureSession"/> 从文档里取出。
/// </remarks>
internal sealed class CaptureEditState
{
    /// <summary>
    /// 落盘保留几份撤销快照。内存里是 50 份（<see cref="CaptureDocument.MaxUndo"/>），但落盘只留最近 20 份：
    /// 每份都是**一整份标注列表**，50 份会把 <c>catalog.json</c> 撑得很大，而 20 步撤销已经够日常改。
    /// </summary>
    public const int MaxUndoOnDisk = 20;

    /// <summary>成品上的标注（<see cref="MarkCodec"/> 的数组）；没画过就是 null。</summary>
    public JsonArray? Annotations { get; init; }

    /// <summary>标注的撤销栈（每份是一整份标注列表快照）。</summary>
    public JsonArray? Undo { get; init; }

    /// <summary>标注的重做栈。</summary>
    public JsonArray? Redo { get; init; }

    /// <summary>当时的选区外观。</summary>
    public CaptureStyleDto? Style { get; init; }

    /// <summary>从一份文档的当前状态造出来（标注为空时只记外观）。</summary>
    public static CaptureEditState Of(CaptureDocument document, IReadOnlyList<Mark[]> undo,
        IReadOnlyList<Mark[]> redo) => new()
    {
        Annotations = document.Marks.Count > 0 ? MarkCodec.Write(document.Marks) : null,
        Undo = undo.Count > 0
            ? MarkCodec.WriteHistory(undo.Skip(Math.Max(0, undo.Count - MaxUndoOnDisk)))
            : null,
        Redo = redo.Count > 0
            ? MarkCodec.WriteHistory(redo.Skip(Math.Max(0, redo.Count - MaxUndoOnDisk)))
            : null,
        Style = CaptureStyleDto.Of(document),
    };
}
