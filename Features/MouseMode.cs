using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 鼠标模式（对应原版 lib/MouseControl.ahk）。
/// CapsLock+Space 进入；CapsLock+Space 或 Esc 退出。
/// - e/d/s/f 按住持续移动（10ms 循环，对角线归一化，速度 mouseSpeed 1~20）
/// - q/a 调速 ±1（INI 持久化 [MouseMode] Speed）
/// - w/r 左/右键点击
/// - j/k/h/l 按住持续滚轮（50ms 循环，j 下 k 上 h 左 l 右）
/// </summary>
internal static class MouseMode
{
    private static readonly DispatcherTimer _moveTimer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private static readonly DispatcherTimer _wheelTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    // E/D/S/F 持续按下状态。WH_KEYBOARD_LL 吞掉 keydown 后 GetAsyncKeyState 不再反映这些键，
    // 必须由 OnKey 的 down/up 自行维护，MoveLoop 据此移动（对应 AHK GetKeyState("e","P") 的物理状态）。
    private static bool _up, _down, _left, _right;
    // W/R 按住状态（press-hold：tap=点击，hold+move=拖动）
    private static bool _leftDown, _rightDown;
    private static int _wheelDir;      // 0=WheelUp,1=WheelDown,2=WheelLeft,3=WheelRight

    static MouseMode()
    {
        _moveTimer.Tick += (_, _) => MoveLoop();
        _wheelTimer.Tick += (_, _) => WheelLoop();
    }

    /// <summary>进入鼠标模式。</summary>
    public static void Enter()
    {
        _up = _down = _left = _right = false;
        _leftDown = _rightDown = false;
        _moveTimer.Stop(); _wheelTimer.Stop();
        AppState.MouseModeActive = true;
        AppState.OtherKeyPressed = true;
        ShowTooltip($"鼠标模式已启用 (速度: {AppState.MouseModeSpeed})");
    }

    /// <summary>退出鼠标模式。</summary>
    public static void Exit()
    {
        AppState.MouseModeActive = false;
        AppState.OtherKeyPressed = true;
        _up = _down = _left = _right = false;
        // 退出前释放可能按住的鼠标键（退出后 W/R keyup 不再路由，防按键卡死）
        if (_leftDown) { Win32.mouse_event(Win32.MouseeventfLeftup, 0, 0, 0, IntPtr.Zero); _leftDown = false; }
        if (_rightDown) { Win32.mouse_event(Win32.MouseeventfRightup, 0, 0, 0, IntPtr.Zero); _rightDown = false; }
        _moveTimer.Stop(); _wheelTimer.Stop();
        ShowTooltip("鼠标模式已关闭");
    }

    /// <summary>该键是否为鼠标模式键。</summary>
    public static bool IsMouseKey(ushort vk) => vk switch
    {
        (ushort)'E' or (ushort)'D' or (ushort)'S' or (ushort)'F'  // 移动
        or (ushort)'Q' or (ushort)'A'                              // 速度
        or (ushort)'W' or (ushort)'R'                              // 点击
        or (ushort)'J' or (ushort)'K' or (ushort)'H' or (ushort)'L' // 滚轮
        or (ushort)' ' or (ushort)Win32.VkEscape or (ushort)Win32.VkCapital => true, // Space/Esc 退出
        _ => false,
    };

    /// <summary>处理鼠标模式键。down=true 按下，false 释放。</summary>
    public static void OnKey(ushort vk, bool down)
    {
        switch (vk)
        {
            case (ushort)'E': SetMove('E', down); break;
            case (ushort)'D': SetMove('D', down); break;
            case (ushort)'S': SetMove('S', down); break;
            case (ushort)'F': SetMove('F', down); break;
            case (ushort)'Q': if (down) AdjustSpeed(+1); break;
            case (ushort)'A': if (down) AdjustSpeed(-1); break;
            case (ushort)'W': if (down) PressLeft(); else ReleaseLeft(); break;
            case (ushort)'R': if (down) PressRight(); else ReleaseRight(); break;
            case (ushort)'J': StartWheel(down, 1); break;
            case (ushort)'K': StartWheel(down, 0); break;
            case (ushort)'H': StartWheel(down, 2); break;
            case (ushort)'L': StartWheel(down, 3); break;
            case (ushort)' ' or (ushort)Win32.VkEscape: if (down) Exit(); break;
        }
    }

    // —— 移动 ——
    // 多键同时按需独立方向分量；吞键后 GetAsyncKeyState 失效，故由 OnKey 的 down/up 维护各键按下状态。
    private static void SetMove(char key, bool down)
    {
        switch (key)
        {
            case 'E': _up = down; break;
            case 'D': _down = down; break;
            case 'S': _left = down; break;
            case 'F': _right = down; break;
        }
        if (_up || _down || _left || _right) _moveTimer.Start(); else _moveTimer.Stop();
    }

    private static void MoveLoop()
    {
        try
        {
            int dx = 0, dy = 0;
            if (_up) dy -= 1;
            if (_down) dy += 1;
            if (_left) dx -= 1;
            if (_right) dx += 1;
            if (dx == 0 && dy == 0) { _moveTimer.Stop(); return; }
            // 对角线归一化
            if (dx != 0 && dy != 0)
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                dx = (int)Math.Round(dx / len); dy = (int)Math.Round(dy / len);
            }
            if (!Win32.GetCursorPos(out var pt)) return;
            Win32.SetCursorPos(pt.X + dx * AppState.MouseModeSpeed, pt.Y + dy * AppState.MouseModeSpeed);
        }
        catch (System.Exception ex)
        {
            CrashLog.Write("MouseMode.MoveLoop", ex);
            _moveTimer.Stop(); // 防止异常循环崩溃
        }
    }

    // —— 速度 ——
    private static void AdjustSpeed(int delta)
    {
        AppState.MouseModeSpeed = Math.Clamp(AppState.MouseModeSpeed + delta, 1, 20);
        SaveSpeed();
        ShowTooltip("鼠标速度: " + AppState.MouseModeSpeed);
    }

    private static void SaveSpeed()
    {
        // 落盘放后台线程：SaveSpeed 经 OnKey 在钩子回调路径上被调用（CapsLock+Q/A 调速），
        // 钩子回调不得阻塞（磁盘 IO 在负载下可能 >300ms，系统会卸载钩子）；
        // 内存态 AppState.MouseModeSpeed 已在 AdjustSpeed 同步改完。读-改-写经 ConfigIO 串行化。
        int speed = AppState.MouseModeSpeed;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { ConfigIO.Modify(cfg => cfg.MouseModeSpeed = speed); }
            catch (System.Exception ex) { CrashLog.Write("MouseMode.SaveSpeed", ex); }
        });
    }

    // —— 点击 / 拖动 ——
    // W/R 改为 press-hold 语义：keydown 注入按下（仅首次，自动重复不重复注入），
    // keyup 注入抬起。tap=点击，hold+move=拖动（原版 Click 无拖动能力，此为增强）。
    private static void PressLeft()
    {
        if (_leftDown) return;
        Win32.mouse_event(Win32.MouseeventfLeftdown, 0, 0, 0, IntPtr.Zero);
        _leftDown = true;
    }
    private static void ReleaseLeft()
    {
        if (!_leftDown) return;
        Win32.mouse_event(Win32.MouseeventfLeftup, 0, 0, 0, IntPtr.Zero);
        _leftDown = false;
    }
    private static void PressRight()
    {
        if (_rightDown) return;
        Win32.mouse_event(Win32.MouseeventfRightdown, 0, 0, 0, IntPtr.Zero);
        _rightDown = true;
    }
    private static void ReleaseRight()
    {
        if (!_rightDown) return;
        Win32.mouse_event(Win32.MouseeventfRightup, 0, 0, 0, IntPtr.Zero);
        _rightDown = false;
    }

    // —— 滚轮 ——
    private static void StartWheel(bool down, int dir)
    {
        if (!down) { _wheelTimer.Stop(); return; }
        _wheelDir = dir;
        _wheelTimer.Start();
    }

    private static void WheelLoop()
    {
        try
        {
            uint delta = Win32.WheelDelta;
            switch (_wheelDir)
            {
                case 0: Win32.mouse_event(Win32.MouseeventfWheel, 0, 0, delta, IntPtr.Zero); break;       // 上
                case 1: Win32.mouse_event(Win32.MouseeventfWheel, 0, 0, unchecked((uint)-delta), IntPtr.Zero); break; // 下
                case 2: Win32.mouse_event(Win32.MouseeventfHwheel, 0, 0, unchecked((uint)-delta), IntPtr.Zero); break; // 左
                case 3: Win32.mouse_event(Win32.MouseeventfHwheel, 0, 0, delta, IntPtr.Zero); break;      // 右
            }
        }
        catch (System.Exception ex)
        {
            CrashLog.Write("MouseMode.WheelLoop", ex);
            _wheelTimer.Stop();
        }
    }

    private static void ShowTooltip(string msg) =>
        TrayService.Notify(msg);
}
