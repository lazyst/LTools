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

    /// <summary>相对当前光标移动（可为负）；SendInput 相对量不经鼠标加速。</summary>
    public static void MoveRelative(int dx, int dy) => InputHelper.MoveRelative(dx, dy, Tag);

    /// <summary>
    /// 按下鼠标键（1=左 2=右 3=中 4=X1 5=X2），**不自动抬起**。
    /// 配对由调用方（拖动用 try/finally）或运行结束的 <see cref="InputHoldState.ReleaseAll"/> 兜底。
    /// </summary>
    public static void Down(int button)
    {
        int b = Normalize(button);
        uint flags = b switch
        {
            2 => Win32.MouseeventfRightdown,
            3 => Win32.MouseeventfMiddledown,
            4 => Win32.MouseeventfXdown,
            5 => Win32.MouseeventfXdown,
            _ => Win32.MouseeventfLeftdown,
        };
        uint data = b switch { 4 => (uint)Win32.Xbutton1, 5 => (uint)Win32.Xbutton2, _ => 0 };
        Win32.mouse_event(flags, 0, 0, data, Tag);
        InputHoldState.MouseDown(b);
    }

    /// <summary>抬起鼠标键（编号同 <see cref="Down"/>）；已抬起时重复调用无害。</summary>
    public static void Up(int button)
    {
        int b = Normalize(button);
        uint flags = b switch
        {
            2 => Win32.MouseeventfRightup,
            3 => Win32.MouseeventfMiddleup,
            4 => Win32.MouseeventfXup,
            5 => Win32.MouseeventfXup,
            _ => Win32.MouseeventfLeftup,
        };
        uint data = b switch { 4 => (uint)Win32.Xbutton1, 5 => (uint)Win32.Xbutton2, _ => 0 };
        Win32.mouse_event(flags, 0, 0, data, Tag);
        InputHoldState.MouseUp(b);
    }

    private static int Normalize(int button) => button is >= 1 and <= 5 ? button : 1;

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

    /// <summary>按下并释放一个鼠标键（点击类操作走这里，状态不会残留）。</summary>
    private static void Click(int button)
    {
        Down(button);
        Up(button);
    }
}
