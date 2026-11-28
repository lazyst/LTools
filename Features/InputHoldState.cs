using System.Collections.Generic;
using System.Linq;
using LTools.Features.Running;
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
/// **每个运行一份**：并发时 A 结束不能把 B 还按着的键放掉（见 <c>docs/RUN_INTEROP.md</c> §4）。
/// 不在运行里的调用（内置功能的单次注入）走 <see cref="Ambient"/> 的进程级兜底实例，
/// 由退出 / 功能结束自己释放，语义与以前一致。
/// </para>
/// </remarks>
internal sealed class InputHoldState
{
    private static readonly InputHoldState FallbackState = new();

    private readonly object _gate = new();
    private readonly HashSet<int> _buttons = new();
    private readonly HashSet<ushort> _keys = new();

    /// <summary>当前线程所属运行的登记表；不在运行里时是进程级兜底。</summary>
    public static InputHoldState Ambient => ActionRunScope.Hold ?? FallbackState;

    public static void MouseDown(int button) => Ambient.PressMouse(button);

    public static void MouseUp(int button) => Ambient.ReleaseMouse(button);

    public static void KeyDown(ushort vk) => Ambient.PressKey(vk);

    public static void KeyUp(ushort vk) => Ambient.ReleaseKey(vk);

    /// <summary>进程级兜底（退出 / 内置功能结束时用）。</summary>
    public static void ReleaseAmbient() => FallbackState.ReleaseAll();

    /// <summary>自己这一份登记了几个键 / 鼠标键（自测用；不触发释放）。</summary>
    public int HeldCount
    {
        get
        {
            lock (_gate) return _buttons.Count + _keys.Count;
        }
    }

    public void PressMouse(int button)
    {
        lock (_gate) _buttons.Add(button);
    }

    public void ReleaseMouse(int button)
    {
        lock (_gate) _buttons.Remove(button);
    }

    public void PressKey(ushort vk)
    {
        lock (_gate) _keys.Add(vk);
    }

    public void ReleaseKey(ushort vk)
    {
        lock (_gate) _keys.Remove(vk);
    }

    /// <summary>运行结束兜底：把**自己这一份**仍按着的鼠标键与键盘键全部释放，并清空状态。</summary>
    public void ReleaseAll()
    {
        int[] buttons;
        ushort[] keys;
        lock (_gate)
        {
            buttons = _buttons.ToArray();
            keys = _keys.ToArray();
            _buttons.Clear();
            _keys.Clear();
        }

        // 先松鼠标再松修饰键，避免带 Ctrl/Shift 的点击在清理过程中继续生效。
        foreach (int b in buttons) MouseOps.Up(b);
        foreach (ushort vk in keys) InputHelper.Up(vk);
    }
}
