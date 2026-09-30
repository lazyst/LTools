using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CapsLockPro.Core;
using CapsLockPro.Native;

namespace CapsLockPro.Features;

/// <summary>
/// 统一动作执行引擎（对应计划 §8）。按 <see cref="ActionType"/> 分发：
/// <list type="bullet">
/// <item>launchApp → <see cref="Process.Start"/>(target, args, workdir)</item>
/// <item>openFile / openFolder / openUrl → ShellExecute（默认程序打开）</item>
/// <item>runCommand → 复用 <see cref="TerminalLauncher"/> 终端路由；direct 走 ShellExecute</item>
/// <item>sendText → 50ms 焦点延时后按 <see cref="SendTextMode"/> 分发（键入 / 剪贴板粘贴，§3.2.2）</item>
/// <item>internal → <see cref="InternalActionRegistry"/> 分发（dispatch 到 UI 线程执行）</item>
/// <item>composite → 顺序执行 <see cref="StepDto"/>（延迟 / 失败策略）；步骤内嵌 <see cref="ActionDto"/> 快照，**不支持嵌套**</item>
/// </list>
/// </summary>
/// <remarks>
/// 线程模型：<see cref="Run"/> 一律 spawn 到后台线程（钩子回调不得阻塞 &gt;300ms）；
/// internal 动作触碰 WPF 窗口，在后台线程内用 <see cref="System.Windows.Threading.Dispatcher.Invoke"/>
/// 同步投递到 UI 线程执行——同步等待保证 composite 步骤严格顺序、Delay 语义成立，
/// 且步骤失败能上抛给 <see cref="OnFailStrategy"/> 处理。
/// 失败由调用方统一经 <see cref="CrashLog.Write"/> 记录（含动作名 / 步骤序号 / 异常）。
/// </remarks>
internal static class ActionExecutor
{
    /// <summary>执行动作（后台线程，不阻塞调用方）。光标坐标默认取当前位置。</summary>
    public static void Run(ActionDto action) => Run(action, 0, 0);

    /// <summary>
    /// 执行动作（spawn 后台线程，不阻塞调用方）。cursorX/cursorY 为面板唤起时记录的光标坐标
    /// （0=取当前光标位置，供 <c>windowPin.toggle</c> 等需坐标的内部命令使用）。
    /// </summary>
    public static void Run(ActionDto action, int cursorX, int cursorY)
    {
        Task.Run(() =>
        {
            try { Execute(action, cursorX, cursorY); }
            catch (Exception ex) { CrashLog.Write($"ActionExecutor[{action.Name}]", ex); }
        });
    }

    // —— 同步执行（已位于后台线程，或被 composite 步骤调用）——
    // 失败一律上抛（不在此吞掉），由 Run 顶层 / composite 步骤各自的 catch 记录并按 OnFail 决策。

    private static void Execute(ActionDto action, int cursorX, int cursorY)
    {
        switch (action.Type)
        {
            case ActionType.launchApp:
                LaunchApp(action);
                break;
            case ActionType.openFile:
            case ActionType.openFolder:
            case ActionType.openUrl:
                ShellExecute(action);
                break;
            case ActionType.runCommand:
                RunCommand(action);
                break;
            case ActionType.sendText:
                SendText(action);
                break;
            case ActionType.@internal:
                DispatchInternal(action, cursorX, cursorY);
                break;
            case ActionType.composite:
                RunComposite(action, cursorX, cursorY);
                break;
            default:
                throw new InvalidOperationException($"未知动作类型: {action.Type}");
        }
    }

    // —— launchApp ——
    private static void LaunchApp(ActionDto a)
    {
        if (string.IsNullOrWhiteSpace(a.Target))
            throw new InvalidOperationException($"launchApp 缺少 Target: {a.Name}");
        Process.Start(BuildPsi(a.Target!, a.Args, a.Workdir));
    }

    // —— openFile / openFolder / openUrl（ShellExecute 走默认程序）——
    private static void ShellExecute(ActionDto a)
    {
        string? target = a.Type switch
        {
            ActionType.openFile => a.Path,
            ActionType.openFolder => a.Path,
            ActionType.openUrl => a.Url,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"{a.Type} 缺少路径/URL: {a.Name}");
        Process.Start(new ProcessStartInfo(target!) { UseShellExecute = true });
    }

    // —— runCommand（复用 TerminalLauncher 终端路由）——
    private static void RunCommand(ActionDto a)
    {
        string cmd = a.Cmd ?? "";
        string terminal = a.Terminal ?? "direct";
        bool keepWindow = a.KeepWindow ?? false;
        string workdir = a.Workdir ?? "";

        // direct / 空 terminal：走 ShellExecute（TerminalLauncher 的 direct 分支为 no-op）
        if (terminal == "direct" || terminal.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(cmd))
                throw new InvalidOperationException($"runCommand 缺少 Cmd: {a.Name}");
            RunDirect(cmd, workdir);
            return;
        }

        var r = TerminalLauncher.TryBuildLaunch(terminal, keepWindow, cmd, workdir);
        if (!r.Ok)
        {
            if (r.Error == null) return; // 空命令仅开 shell 无 exe 时的 no-op
            TrayService.Notify(r.Error); // 替代原 MenuSystem 的模态提示，不阻塞动作路径
            throw new InvalidOperationException(r.Error);
        }
        Process.Start(BuildPsi(r.Exe!, r.Args, r.Workdir));
    }

    /// <summary>direct 终端：拆 exe+args 启动，失败回退 ShellExecute（URL/文档/含空格非可执行首段）。</summary>
    private static void RunDirect(string cmd, string workdir)
    {
        string wd = string.IsNullOrWhiteSpace(workdir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : workdir.Trim();
        try
        {
            TrySplitCommandLine(cmd, out string exe, out string args);
            Process.Start(BuildPsi(exe, args, wd));
        }
        catch
        {
            // 回退：ShellExecute 处理 URL/文档等非可执行目标（回退再失败则上抛）
            Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = wd });
        }
    }

    /// <summary>构造 ProcessStartInfo（工作目录仅在存在时设置）。</summary>
    private static ProcessStartInfo BuildPsi(string exe, string? args, string? workdir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        if (!string.IsNullOrEmpty(workdir) && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;
        return psi;
    }

    /// <summary>命令行拆分：首段（带引号或到空格）为 exe，其余为参数。</summary>
    private static bool TrySplitCommandLine(string cmd, out string exe, out string args)
    {
        cmd = cmd.Trim();
        exe = ""; args = "";
        if (cmd.Length == 0) return false;
        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) { exe = cmd[1..]; return true; }
            exe = cmd[1..end];
            args = cmd[(end + 1)..].Trim();
            return true;
        }
        int sp = cmd.IndexOf(' ');
        if (sp < 0) { exe = cmd; return true; }
        exe = cmd[..sp];
        args = cmd[(sp + 1)..].Trim();
        return true;
    }

    // —— sendText（发送文本，§3.2.2）——
    private static void SendText(ActionDto a)
    {
        string text = a.Text ?? "";
        if (text.Length == 0)
            throw new InvalidOperationException($"sendText 缺少 Text: {a.Name}");

        // 焦点延时（§12）：面板/菜单关闭 → 目标窗口焦点切回有时间差，立即发键可能丢焦点。
        Thread.Sleep(50);

        // auto → 按内容判定；type / paste → 强制
        var mode = a.Mode ?? SendTextMode.auto;
        if (mode == SendTextMode.auto)
            mode = NeedPaste(text) ? SendTextMode.paste : SendTextMode.type;

        if (mode == SendTextMode.paste)
        {
            // 不恢复原剪贴板（§12，与 Quicker 一致）
            if (!NativeClipboard.SetText(text))
                throw new InvalidOperationException($"sendText 写剪贴板失败: {a.Name}");
            InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
        }
        else
        {
            TypeText(a, text);
        }

        if (a.AppendEnter == true)
            InputHelper.Tap((ushort)Win32.VkReturn);
    }

    /// <summary>auto 判定：超 100 字符 / 含非 ASCII / 含换行 → 剪贴板粘贴（§3.2.2）。</summary>
    private static bool NeedPaste(string text)
    {
        if (text.Length > 100) return true;
        foreach (var ch in text)
            if (ch > 0x7F || ch == '\n' || ch == '\r') return true;
        return false;
    }

    /// <summary>
    /// 键入式发送：预检全部字符可映射后逐字符发送；遇 <c>\n</c>/<c>\r\n</c> 转 Enter、<c>\t</c> 转 Tab。
    /// 强制 type 模式遇不可映射字符（中文等）**报错而非静默改走剪贴板**——不违背「强制键入」语义（§12）。
    /// </summary>
    private static void TypeText(ActionDto a, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\n' || ch == '\r' || ch == '\t') continue;
            if (!InputHelper.CanType(ch))
                throw new InvalidOperationException(
                    $"sendText 键入方式无法映射字符 U+{(int)ch:X4}（非当前键盘布局字符），请改用「自动」或「粘贴」模式: {a.Name}");
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;  // \r\n 算一次回车
                InputHelper.Tap((ushort)Win32.VkReturn);
            }
            else if (ch == '\n') InputHelper.Tap((ushort)Win32.VkReturn);
            else if (ch == '\t') InputHelper.Tap((ushort)Win32.VkTab);
            else InputHelper.SendChar(ch);
        }
    }

    // —— internal（内部命令分发到 UI 线程，同步等待）——
    private static void DispatchInternal(ActionDto a, int cursorX, int cursorY)
    {
        if (string.IsNullOrWhiteSpace(a.Command))
            throw new InvalidOperationException($"internal 缺少 Command: {a.Name}");
        if (!InternalActionRegistry.TryGet(a.Command!, out var handler) || handler == null)
            throw new InvalidOperationException($"未注册的内部命令: {a.Command}");

        // 光标坐标为 0 时取当前位置（面板唤起时记录的坐标优先）
        int x = cursorX, y = cursorY;
        if (x == 0 && y == 0 && Win32.GetCursorPos(out var pt))
        {
            x = pt.X; y = pt.Y;
        }

        // internal 动作触碰 WPF 窗口（QuickNote/Config/HelpPanel 等）：同步 dispatch 到 UI 线程。
        // 同步（Invoke 而非 BeginInvoke）保证 composite 步骤严格顺序、Delay 语义成立，
        // 且步骤内异常能上抛给调用方按 OnFail 处理。
        var disp = Application.Current?.Dispatcher;
        if (disp == null) { handler(x, y); return; }
        disp.Invoke(new Action(() => handler(x, y)));
    }

    // —— composite（顺序执行步骤；步骤内嵌快照，不支持嵌套）——
    private static void RunComposite(ActionDto a, int cursorX, int cursorY)
    {
        if (a.Steps == null || a.Steps.Count == 0) return;

        for (int i = 0; i < a.Steps.Count; i++)
        {
            var step = a.Steps[i];
            if (step.DelayMs > 0)
                Thread.Sleep(step.DelayMs);

            var stepAction = step.Action;
            // 嵌套守卫：编辑器已禁止组合步骤内嵌组合，此处对畸形配置兜底（跳过 + 记日志，不崩）。
            if (stepAction == null || stepAction.Type == ActionType.composite)
            {
                CrashLog.Write($"ActionExecutor[{a.Name}].Step{i + 1}",
                    new InvalidOperationException("组合动作不支持嵌套组合步骤"));
                if (OnFailOf(step, a) == OnFailStrategy.abort) break;
                continue;
            }

            try
            {
                Execute(stepAction, cursorX, cursorY);
            }
            catch (Exception ex)
            {
                CrashLog.Write($"ActionExecutor[{a.Name}].Step{i + 1}({stepAction.Name})", ex);
                if (OnFailOf(step, a) == OnFailStrategy.abort) break;
            }
        }
    }

    /// <summary>解析步骤失败策略：步骤显式值 &gt; 组合级默认 &gt; continue（§4）。</summary>
    private static OnFailStrategy OnFailOf(StepDto step, ActionDto composite) =>
        step.OnFail ?? composite.CompositeOnFail ?? OnFailStrategy.@continue;
}
