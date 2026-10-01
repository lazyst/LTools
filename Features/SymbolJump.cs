using System.Runtime.InteropServices;
using Clipboard = System.Windows.Clipboard;
using IDataObject = System.Windows.IDataObject;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 配对符号跳转（对应原版 lib/SymbolJump.ahk）。
/// CapsLock+P：读取光标右侧字符，若为配对符号则向匹配方向逐行扫描直到找到对应符号，
/// 移动光标到该处；支持嵌套计数、三帧边界检测、10s 超时、Esc/重按 CapsLock 中断。
/// </summary>
/// <remarks>
/// <b>必须在工作线程执行</b>：搜索循环含 ClipWait/Sleep，远超 300ms 钩子超时阈值，
/// 在钩子回调同步执行会被系统摘钩子。Start() 只设标志并 spawn 线程后立即返回。
/// 剪贴板访问需 STA，故搜索在工作线程且 <see cref="Thread.SetApartmentState"/> 为 STA。
/// </remarks>
/// <remarks>
/// <b>剪贴板时序两要点</b>（C# 端口相对 AHK 的补丁，见 NativeClipboard）：
/// <list type="bullet">
/// <item>选区建立与复制之间必须 <see cref="Thread.Sleep"/>(50)：C# SendInput 突发式无节拍，
/// 背靠背发出会让目标应用来不及处理选区就收到复制→复制空内容→超时（AHK 的 Send 自带 yield 无需显式 Sleep）。</item>
/// <item>剪贴板读写走 <see cref="NativeClipboard"/>（原始 Win32）：WinForms Clipboard 的 OLE 读取
/// 在裸 STA 线程上跨进程复制会阻塞数秒，致 ClipWait 500ms 超时形同虚设。</item>
/// </list>
/// </remarks>
internal static class SymbolJump
{
    // —— 诊断日志（存在 %TEMP%\capslock-symboljump-enable.txt 即开启，写入 %TEMP%\capslock-symboljump-diag.log）——
    private static readonly string DiagEnablePath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "capslock-symboljump-enable.txt");
    private static readonly string DiagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "capslock-symboljump-diag.log");
    private static bool DiagEnabledNow() { try { return System.IO.File.Exists(DiagEnablePath); } catch { return false; } }
    private static void Diag(string s)
    {
        if (!DiagEnabledNow()) return;
        try { System.IO.File.AppendAllText(DiagPath, $"[{DateTime.Now:HH:mm:ss.fff}] {s}\n"); }
        catch { }
    }
    // 取目标插入符的字符索引（GetGUIThreadInfo→hwndCaret→EM_GETSEL 的 active end）
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageEMGetSel(IntPtr hWnd, uint Msg, out int wParam, out int lParam);
    private const uint EmGetSel = 0x00B0;
    private static string TargetSelInfo()
    {
        try
        {
            var info = new Win32.Guithreadinfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.Guithreadinfo>() };
            if (!Win32.GetGUIThreadInfo(0, ref info) || info.hwndCaret == IntPtr.Zero)
                return "(no caret)";
            SendMessageEMGetSel(info.hwndCaret, EmGetSel, out int s, out int e);
            return $"sel=({s},{e}) active={e} hwndCaret={info.hwndCaret.ToInt64():X}";
        }
        catch (Exception ex) { return "(err " + ex.Message + ")"; }
    }
    // —— 成对符号映射（对应 AHK SymbolPairs / ReverseSymbolPairs）——
    private static readonly Dictionary<char, char> SymbolPairs = new()
    {
        { '(', ')' }, { '[', ']' }, { '{', '}' }, { '<', '>' },
        { '「', '」' }, { '『', '』' }, { '【', '】' }, { '《', '》' }, { '〈', '〉' },
        { '（', '）' }, { '［', '］' }, { '｛', '｝' }, { '〔', '〕' }, { '〖', '〗' },
        { '〘', '〙' }, { '〚', '〛' }, { '“', '”' }, { '‘', '’' }, { '‹', '›' }, { '«', '»' },
    };
    private static readonly Dictionary<char, char> ReverseSymbolPairs = BuildReverse();

    private static Dictionary<char, char> BuildReverse()
    {
        var d = new Dictionary<char, char>(SymbolPairs.Count);
        foreach (var kv in SymbolPairs) d[kv.Value] = kv.Key;
        return d;
    }

    // —— 启动（由 TextEditor CapsLock+P 分发调用）——

    /// <summary>触发一次符号跳转。若已在搜索中则忽略（对应 #HotIf !isSeekingSymbol）。</summary>
    public static void Start()
    {
        if (AppState.IsSeekingSymbol) return;
        AppState.IsSeekingSymbol = true;
        AppState.OtherKeyPressed = true;

        // 中断轮询线程（对应 AHK CheckForInterrupt 20ms 定时器）
        var poll = new Thread(PollInterrupt) { IsBackground = true };
        poll.Start();

        // 搜索工作线程（STA：剪贴板访问）
        var worker = new Thread(() =>
        {
            try { Seek(); }
            catch { /* 任何异常都确保标志复位（finally 兜底） */ }
            finally { AppState.IsSeekingSymbol = false; }
        }) { IsBackground = true };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    // —— 中断轮询（对应 CheckForInterrupt：Esc 或释放后重按 CapsLock → 中断）——
    private static void PollInterrupt()
    {
        bool initialReleased = false;
        while (AppState.IsSeekingSymbol)
        {
            try
            {
                if ((Win32.GetAsyncKeyState(Win32.VkEscape) & 0x8000) != 0)
                {
                    AppState.IsSeekingSymbol = false;
                    break;
                }
                bool caps = (Win32.GetAsyncKeyState(Win32.VkCapital) & 0x8000) != 0;
                if (!initialReleased && !caps)
                    initialReleased = true;
                else if (initialReleased && caps)
                {
                    AppState.IsSeekingSymbol = false;
                    break;
                }
            }
            catch { /* 轮询失败忽略，下一拍重试 */ }
            Thread.Sleep(20);
        }
    }

    // —— 主搜索流程（对应 AHK p:: 热键体）——
    private static void Seek()
    {
        int searchStartTime = Environment.TickCount;
        var savedClipboard = BackupClipboard();
        Diag($"==== Seek 开始 ==== {TargetSelInfo()}");

        NativeClipboard.Clear();
        // 读取光标右侧字符：+{Right}^c
        InputHelper.Combo((ushort)Win32.VkShift, (ushort)Win32.VkRight);
        Thread.Sleep(50); // 选区建立与复制之间的时序间隔（见类注释）
        InputHelper.Combo((ushort)Win32.VkControl, (ushort)'C');
        Diag($"[Seek] +Right^c 后 {TargetSelInfo()}");
        if (!ClipWait(500))
        {
            Diag("[Seek] 获取字符超时");
            Cleanup(savedClipboard, "获取字符超时");
            return;
        }
        NativeClipboard.TryGetText(out string current);
        Diag($"[Seek] current=\"{current}\" (len={current?.Length ?? -1})");
        NativeClipboard.Clear();

        // 检查是否为配对符号
        if (string.IsNullOrEmpty(current) || current.Length != 1 ||
            (!SymbolPairs.ContainsKey(current[0]) && !ReverseSymbolPairs.ContainsKey(current[0])))
        {
            Cleanup(savedClipboard, "光标右侧非配对符号");
            InputHelper.Tap((ushort)Win32.VkLeft); // {Left}
            return;
        }

        char currentChar = current[0];
        if (SymbolPairs.TryGetValue(currentChar, out char forwardTarget))
        {
            // 向前搜索
            InputHelper.Tap((ushort)Win32.VkRight); // {Right}
            Thread.Sleep(30);
            Diag($"[Seek] forward current='{currentChar}' target='{forwardTarget}', Tap(Right) 后 {TargetSelInfo()}");
            SearchInDirection("forward", forwardTarget, currentChar, searchStartTime, savedClipboard);
        }
        else
        {
            // 向后搜索
            char backwardTarget = ReverseSymbolPairs[currentChar];
            Diag($"[Seek] backward current='{currentChar}' target='{backwardTarget}'");
            SearchInDirection("backward", backwardTarget, currentChar, searchStartTime, savedClipboard);
        }
    }

    // —— 逐行搜索（对应 _SearchInDirection）——
    private static void SearchInDirection(string dir, char targetChar, char currentChar,
        int searchStartTime, IDataObject? savedClipboard)
    {
        int counter = 1;
        string?[] tempLine = new string?[3];   // [最新, 次, 旧]
        Win32.Point?[] caretPos = new Win32.Point?[3];

        while (true)
        {
            // 超时（10s）
            if (Environment.TickCount - searchStartTime > 10000)
            {
                Cleanup(savedClipboard, "搜索超时 - 未找到匹配符号");
                return;
            }

            // 中断检查
            if (!CheckInterrupt(savedClipboard))
                return;

            // 当前光标位置（边界检测用）
            Win32.Point? currentCaretPos = CaretHelper.GetCaretPosition();

            // 读取当前行内容（方向相关）
            string? lineContent = ReadLineContent(dir);
            Diag($"[Search dir={dir}] ReadLineContent 后 line=\"{lineContent}\" (len={lineContent?.Length ?? -1}) {TargetSelInfo()}");
            if (!CheckInterrupt(savedClipboard))
                return;
            if (lineContent == null)
            {
                Cleanup(savedClipboard, "获取行内容超时");
                return;
            }

            // 移动补偿（方向相关）：撤销 ReadLineContent 选择造成的光标位移
            if (dir == "forward")
                InputHelper.Tap((ushort)Win32.VkLeft);
            else
                InputHelper.Tap((ushort)Win32.VkRight);
            Thread.Sleep(30);
            Diag($"[Search dir={dir}] 补偿 Tap 后 {TargetSelInfo()}");

            // 更新三帧历史
            tempLine[2] = tempLine[1];
            tempLine[1] = tempLine[0];
            tempLine[0] = lineContent;

            caretPos[2] = caretPos[1];
            caretPos[1] = caretPos[0];
            caretPos[0] = currentCaretPos;

            // 边界检测 — 连续三帧内容+位置未变化说明到文档边界
            if (CheckBoundary(tempLine, caretPos))
            {
                Cleanup(savedClipboard, "到达文档边界 - 未找到匹配符号");
                return;
            }

            // 在当前行扫描
            var (found, position) = ScanLine(dir, lineContent, targetChar, currentChar, ref counter);
            Diag($"[Search dir={dir}] ScanLine found={found} position={position} counter={counter}");
            if (!AppState.IsSeekingSymbol)
                return; // 扫描中被中断

            if (found)
            {
                RestoreClipboard(savedClipboard);
                Diag($"[Search dir={dir}] RestoreClipboard 后 {TargetSelInfo()}");
                // 移动到目标位置（方向相关）
                if (dir == "forward")
                    RepeatTap((ushort)Win32.VkRight, position);
                else
                    RepeatTap((ushort)Win32.VkLeft, position);
                if (DiagEnabledNow()) Thread.Sleep(150); // 仅诊断时等待键处理完，便于取稳定位置
                Diag($"[Search dir={dir}] RepeatTap({position}) 后 {TargetSelInfo()}");
                AppState.IsSeekingSymbol = false;
                return;
            }

            // 移动到下一行（方向相关）
            if (dir == "forward")
            {
                InputHelper.Tap((ushort)Win32.VkEnd);   // {End}
                InputHelper.Tap((ushort)Win32.VkRight); // {Right}
            }
            else
            {
                InputHelper.Tap((ushort)Win32.VkUp);    // {Up}
                InputHelper.Tap((ushort)Win32.VkEnd);   // {End}
            }
            Thread.Sleep(30);
        }
    }

    /// <summary>读取当前行内容（对应 _ReadLineContent）。forward: 光标→行尾+1；backward: 行首-1→光标。</summary>
    private static string? ReadLineContent(string dir)
    {
        NativeClipboard.Clear();
        if (dir == "forward")
            InputHelper.ModifiedSequence((ushort)Win32.VkShift, (ushort)Win32.VkEnd, (ushort)Win32.VkRight);   // +{End}+{Right} 单次注入、Shift 全程保持
        else
            InputHelper.ModifiedSequence((ushort)Win32.VkShift, (ushort)Win32.VkHome, (ushort)Win32.VkLeft);    // +{Home}+{Left}
        Thread.Sleep(50); // 选区建立与复制之间的时序间隔（见类注释）
        InputHelper.Combo((ushort)Win32.VkControl, (ushort)'C');             // ^c

        if (!ClipWait(500))
            return null;
        NativeClipboard.TryGetText(out var line);
        return line;
    }

    /// <summary>在行内容中扫描配对符号（对应 _ScanLine）。返回 (found, position)。</summary>
    private static (bool found, int position) ScanLine(string dir, string lineContent,
        char targetChar, char currentChar, ref int counter)
    {
        if (dir == "forward")
        {
            // 从左到右
            for (int i = 0; i < lineContent.Length; i++)
            {
                if (!AppState.IsSeekingSymbol)
                    return (false, 0);
                char c = lineContent[i];
                if (c == targetChar)
                {
                    counter--;
                    if (counter == 0)
                        return (true, i); // A_Index-1
                }
                else if (c == currentChar)
                {
                    counter++;
                }
            }
        }
        else
        {
            // 从右到左
            int len = lineContent.Length;
            for (int idx = 1; idx <= len; idx++)
            {
                if (!AppState.IsSeekingSymbol)
                    return (false, 0);
                int position = len - idx; // AHK: lineLength - A_Index + 1（1-based → 0-based）
                char tempChar = lineContent[position];
                if (tempChar == targetChar)
                {
                    counter--;
                    if (counter == 0)
                        return (true, idx - 1); // A_Index-1
                }
                else if (tempChar == currentChar)
                {
                    counter++;
                }
            }
        }
        return (false, 0);
    }

    /// <summary>边界检测（对应 _CheckBoundary）：连续三帧内容+位置未变化 → 到达文档边界。</summary>
    private static bool CheckBoundary(string?[] tempLine, Win32.Point?[] caretPos)
    {
        // AHK: if (tempLine[1] = "" || tempLine[3] = "") return false —— 历史不足或空行不判边界
        if (string.IsNullOrEmpty(tempLine[0]) || string.IsNullOrEmpty(tempLine[2]))
            return false;
        if (tempLine[0] != tempLine[2])
            return false;
        if (caretPos[0] == null || caretPos[2] == null)
            return false;
        return caretPos[0]!.Value.X == caretPos[2]!.Value.X
            && caretPos[0]!.Value.Y == caretPos[2]!.Value.Y;
    }

    /// <summary>带中断检查的剪贴板等待（对应 _ClipWait）。原始 Win32 读取，不阻塞（见 NativeClipboard）。</summary>
    private static bool ClipWait(int timeoutMs)
    {
        int slept = 0;
        while (slept < timeoutMs)
        {
            if (!AppState.IsSeekingSymbol)
                return false; // 已中断
            try
            {
                if (NativeClipboard.TryGetText(out var t) && !string.IsNullOrEmpty(t))
                    return true;
            }
            catch { /* 剪贴板被占用，继续等 */ }
            Thread.Sleep(20);
            slept += 20;
        }
        return false;
    }

    /// <summary>检查是否中断（对应 _CheckInterrupt）。true=继续，false=已中断（已清理）。</summary>
    private static bool CheckInterrupt(IDataObject? savedClipboard)
    {
        if (AppState.IsSeekingSymbol)
            return true; // 继续
        // 已中断 → 清理并提示
        RestoreClipboard(savedClipboard);
        ShowTooltip("符号跳转已取消");
        AppState.IsSeekingSymbol = false;
        return false;
    }

    /// <summary>清理状态并退出（对应 _CleanupAndExit）。</summary>
    private static void Cleanup(IDataObject? savedClipboard, string tipText)
    {
        AppState.IsSeekingSymbol = false;
        RestoreClipboard(savedClipboard);
        if (!string.IsNullOrEmpty(tipText))
            ShowTooltip(tipText);
    }

    // —— 剪贴板备份/恢复（完整格式，走 WinForms OLE；每操作前后各一次，非热循环，剪贴板空闲时不阻塞）——
    private static IDataObject? BackupClipboard()
    {
        try { return Clipboard.GetDataObject(); }
        catch { return null; }
    }

    private static void RestoreClipboard(IDataObject? orig)
    {
        try { if (orig != null) Clipboard.SetDataObject(orig, copy: false); }
        catch { /* 恢复失败静默 */ }
    }

    // —— 小工具 ——
    private static void RepeatTap(ushort vk, int count)
    {
        // 逐次注入并在每次之间节拍等待：管理员→非管理员跨进程 + LL 钩子环境下，
        // 突发式批量注入多次同键会导致输入流溢出/丢弃（实测 13 次只生效 6 次）。
        // 逐次 + 间隔让目标应用逐个处理，避免丢键。对应 AHK Send("{vk N}") 的隐含节拍。
        for (int i = 0; i < count; i++)
        {
            InputHelper.Tap(vk);
            Thread.Sleep(20);
        }
    }

    private static void ShowTooltip(string msg)
    {
        TrayService.Notify(msg);
    }
}
