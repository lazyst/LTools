using System;
using System.Threading;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 鼠标注入的唯一实现：「鼠标操作」与「文字点击」共用，避免两处各写一套 mouse_event。
/// </summary>
/// <remarks>调用方在后台线程（动作执行 / 流程步骤），这里不碰 WPF。</remarks>
internal static class MouseOps
{
    /// <summary>注入鼠标事件的标记（与宏回放一致，便于排查自己发出的事件）。</summary>
    public static readonly IntPtr Tag = (IntPtr)0x1234ABCD;

    /// <summary>把光标移到虚拟桌面物理坐标（与截图 / 找图坐标系一致）。</summary>
    public static void Move(int x, int y) => InputHelper.MoveToScreen(x, y, Tag);

    /// <summary>在当前光标处执行一个鼠标动作；滚轮按 <paramref name="wheelDelta"/> 刻度（120 = 一格）。</summary>
    public static void Perform(MouseActionKind kind, int wheelDelta = 120)
    {
        int wheel = Math.Clamp(wheelDelta, 1, 12_000);
        switch (kind)
        {
            case MouseActionKind.doubleClick:
                Click(1);
                Thread.Sleep(60);
                Click(1);
                break;
            case MouseActionKind.rightClick:
                Click(2);
                break;
            case MouseActionKind.middleClick:
                Click(3);
                break;
            case MouseActionKind.move:
                break;
            case MouseActionKind.wheelUp:
                Win32.mouse_event(Win32.MouseeventfWheel, 0, 0, (uint)wheel, Tag);
                break;
            case MouseActionKind.wheelDown:
                Win32.mouse_event(Win32.MouseeventfWheel, 0, 0, unchecked((uint)-wheel), Tag);
                break;
            default:
                Click(1);
                break;
        }
    }

    /// <summary>按下并释放一个鼠标键（1=左 2=右 3=中）。</summary>
    private static void Click(int button)
    {
        uint down = button switch
        {
            2 => Win32.MouseeventfRightdown,
            3 => Win32.MouseeventfMiddledown,
            _ => Win32.MouseeventfLeftdown,
        };
        uint up = button switch
        {
            2 => Win32.MouseeventfRightup,
            3 => Win32.MouseeventfMiddleup,
            _ => Win32.MouseeventfLeftup,
        };
        Win32.mouse_event(down, 0, 0, 0, Tag);
        Win32.mouse_event(up, 0, 0, 0, Tag);
    }
}
