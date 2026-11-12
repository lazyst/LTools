using System.Collections.Generic;
using System.Linq;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 运行期「还按着的键 / 鼠标键」状态：动作单元用「按下 / 抬起」跨步骤保持按下时，
/// 由 <c>AutomationRunner.RunCore</c> 的 finally 调 <see cref="ReleaseAll"/> 兜底释放。
/// </summary>
/// <remarks>
/// <para>
/// 正式运行、编辑器「▶ 测试」与 MCP 草稿试运行都走同一个 <c>RunCore</c>，所以一处清理全覆盖
/// （成功 / 失败 / 停止热键 / 关窗）。没有它，动作中途失败就会把鼠标左键一直按着，
/// 桌面持续框选、拖着东西放不下——比键盘卡住 Ctrl 更难自愈。
/// </para>
/// <para>
/// 成对的操作（单击 / 拖动）自己会抬起，登记后立刻移除，不会留下状态；
/// 这里只兜住「只写了按下、没写抬起」或中途被打断的情况。宏回放有自己的 finally，不接这里。
/// </para>
/// </remarks>
internal static class InputHoldState
{
    private static readonly object Gate = new();
    private static readonly HashSet<int> Buttons = new();
    private static readonly HashSet<ushort> Keys = new();

    public static void MouseDown(int button) { lock (Gate) Buttons.Add(button); }

    public static void MouseUp(int button) { lock (Gate) Buttons.Remove(button); }

    public static void KeyDown(ushort vk) { lock (Gate) Keys.Add(vk); }

    public static void KeyUp(ushort vk) { lock (Gate) Keys.Remove(vk); }

    /// <summary>运行结束兜底：把仍按着的鼠标键与键盘键全部释放，并清空状态。</summary>
    public static void ReleaseAll()
    {
        int[] buttons;
        ushort[] keys;
        lock (Gate)
        {
            buttons = Buttons.ToArray();
            keys = Keys.ToArray();
            Buttons.Clear();
            Keys.Clear();
        }

        // 先松鼠标再松修饰键，避免带 Ctrl/Shift 的点击在清理过程中继续生效。
        foreach (int b in buttons) MouseOps.Up(b);
        foreach (ushort vk in keys) InputHelper.Up(vk);
    }
}
