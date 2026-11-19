using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的快捷键派发：与截图覆盖层读**同一份表**（<see cref="CaptureShortcutTable"/>），
/// 所以用户在「设置 → 通用 → 截图 Pro · 快捷键」里改一次，两边一起变。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageEditingWindow</c>：绘图工具走 <c>drawing_shortcuts/*</c>，
/// 撤销 / 重做是 <c>CanvasHistoryShortcuts</c> 的固定绑定（读 <c>screenshot_shortcuts/undo|redo</c>）。
/// 这里只认这两类；复制 / 另存 / 打印 / 裁剪这些窗口自己的命令仍走原来的固定键。
/// </remarks>
public partial class ImageEditWindow
{
    /// <summary>查表并执行；命中返回 true（调用方吃掉这个键）。</summary>
    private bool TryTableShortcut(KeyEventArgs e)
    {
        if (CaptureShortcutTable.Match(CaptureShortcutScope.Drawing, e, out string drawing) &&
            RunDrawingShortcut(drawing))
        {
            return true;
        }

        if (CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, e, out string screenshot))
        {
            switch (screenshot)
            {
                case "undo":
                    Undo();
                    return true;
                case "redo":
                    Redo();
                    return true;
            }
        }

        return false;
    }

    /// <summary>绘图类动作：换工具，或清空画布。</summary>
    private bool RunDrawingShortcut(string id)
    {
        if (id == "reset_canvas")
        {
            ClearCanvas();
            return true;
        }

        if (CaptureShortcutTable.ToolOf(id) is not CaptureTool tool) return false;
        SetTool(tool);
        return true;
    }

    /// <summary>
    /// 清空画布（<c>drawing_shortcuts/reset_canvas</c>）：标注全清，可撤销。
    /// 底图与裁剪结果不动——上游 <c>reset_canvas</c> 清的也只是画布上的标注。
    /// </summary>
    private void ClearCanvas()
    {
        CommitText();

        var doc = _doc.Doc;
        if (doc == null || doc.Marks.Count == 0) return;

        _doc.PushUndo();
        doc.ClearMarks();
        SelectMark(-1);
        RefreshMarks();
    }
}
