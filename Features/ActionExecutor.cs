using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 统一动作执行引擎（对应计划 §8）。按 <see cref="ActionType"/> 分发：
/// <list type="bullet">
/// <item>launchApp → <see cref="Process.Start"/>(target, args, workdir)</item>
/// <item>openFile / openFolder / openUrl → ShellExecute（默认程序打开）</item>
/// <item>runCommand → 复用 <see cref="TerminalLauncher"/> 终端路由；direct 走 ShellExecute</item>
/// <item>sendText → 50ms 焦点延时后按 <see cref="SendTextMode"/> 分发（auto=Unicode 注入 / type=键入 / paste=剪贴板粘贴，§3.2.2）</item>
/// <item>setClipboard → 把文本（可含运行期模板）写进系统剪贴板，不发送、不按键</item>
/// <item>toast → 右下角提示卡片，投递到 UI 线程即返回（不阻塞后续步骤、不抢焦点）</item>
/// <item>quickNote → 把正文存进速记（<see cref="NoteActions"/>，与 CapsLock+N 同一个目录）</item>
/// <item>window → <see cref="WindowAction"/>：切到前台 / 最大化 / 最小化 / 还原 / 调整大小（按标题匹配窗口，读回真实状态）</item>
/// <item>sendKeys → 50ms 焦点延时后顺序遍历条目（chord 逐 stroke Combo、stroke 间 15ms；text 复用 sendText auto；sleep 等待，§3.2.1）</item>
/// <item>internal → <see cref="InternalActionRegistry"/> 分发（dispatch 到 UI 线程执行）</item>
/// <item>mouse → 绝对移动 + 点击 / 双击 / 滚轮（坐标支持运行期模板）</item>
/// <item>composite → 交给 <see cref="FlowRunner"/> 解释执行嵌套流程；节点内嵌 <see cref="ActionDto"/> 快照，**不支持嵌套流程**</item>
/// </list>
/// <para>
/// 有结果的动作（见 <see cref="ActionResults"/>）会把结果返回给调用方；流程里由
/// <see cref="FlowRunner"/> 写入 <see cref="ActionDto.OutputVar"/> 指定的变量，独立运行动作时忽略。
/// </para>
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
        // 从 CapsLock 菜单启动时 CapsLock 仍可能处于按下态；流程运行期间钩子会屏蔽 CapsLock，
        // 若不在启动前清掉，状态机可能一直认为它按着（见 FlowRunner.PrepareToRun）。
        if (action.Type == ActionType.composite) FlowRunner.PrepareToRun();

        bool trace = action.Type != ActionType.composite;   // composite 由 FlowRunner 记录流程日志

        Task.Run(() =>
        {
            long started = Environment.TickCount64;
            if (trace) AppLog.Info("Action", $"执行「{action.Name}」({action.Type})");
            try
            {
                Execute(action, cursorX, cursorY);
                if (trace) AppLog.Info("Action", $"完成「{action.Name}」· {Environment.TickCount64 - started} ms");
            }
            catch (Exception ex)
            {
                if (trace) AppLog.Error("Action", $"失败「{action.Name}」({action.Type})", ex);
                CrashLog.Write($"ActionExecutor[{action.Name}]", ex);
            }
        });
    }

    // —— 同步执行（已位于后台线程，或被流程节点调用）——
    // 失败一律上抛（不在此吞掉），由 Run 顶层 / FlowRunner 的每个节点 catch 记录并按 OnFail 决策。

    /// <summary>
    /// 同步执行一个动作（已位于后台线程）。返回动作结果（不支持输出 / 无结果时返回 null），
    /// 由 <see cref="FlowRunner"/> 决定是否写入变量；<paramref name="ctx"/> 供可取消的长命令使用。
    /// </summary>
    internal static VarValue? Execute(ActionDto action, int cursorX, int cursorY, RunContext? ctx = null)
    {
        VarValue? result = null;
        switch (action.Type)
        {
            case ActionType.launchApp:
                result = LaunchApp(action);
                break;
            case ActionType.openFile:
            case ActionType.openFolder:
            case ActionType.openUrl:
                result = ShellExecute(action);
                break;
            case ActionType.runCommand:
                result = RunCommand(action, ctx);
                break;
            case ActionType.sendText:
                SendText(action);
                break;
            case ActionType.setClipboard:
                result = SetClipboard(action);
                break;
            case ActionType.toast:
                ShowToast(action);
                break;
            case ActionType.quickNote:
                result = NoteActions.Write(action);
                break;
            case ActionType.window:
                result = WindowAction.Run(action);
                break;
            case ActionType.clickText:
                result = TextClickAction.Run(action, ctx);
                break;
            case ActionType.imageClick:
                result = ImageClickAction.Run(action, ctx);
                break;
            case ActionType.sendKeys:
                SendKeys(action);
                break;
            case ActionType.mouse:
                result = Mouse(action);
                break;
            case ActionType.macro:
                PlayMacro(action);
                break;
            case ActionType.@internal:
                DispatchInternal(action, cursorX, cursorY);
                break;
            case ActionType.composite:
                FlowRunner.RunAction(action, cursorX, cursorY);
                break;
            default:
                throw new InvalidOperationException($"未知动作类型: {action.Type}");
        }
        return result;
    }

    // —— launchApp ——
    private static VarValue? LaunchApp(ActionDto a)
    {
        if (string.IsNullOrWhiteSpace(a.Target))
            throw new InvalidOperationException($"launchApp 缺少 Target: {a.Name}");
        string wd = AppPaths.ResolveWorkdir(a.Workdir);
        // CreateProcess 解析相对 exe 用的是父进程 CWD，不是 WorkingDirectory；先按数据根 / 工作目录解析成绝对路径，
        // 否则 `resources\foo.exe` 在 LTools 自身 CWD 不是数据根时（如开机自启 = System32）会找不到文件。
        string target = AppPaths.ResolveTarget(a.Target!, wd);
        var process = Process.Start(BuildPsi(target, a.Args, wd));
        if (process == null) return VarValue.FromBool(true);

        try
        {
            int pid = process.Id;
            return new VarValue
            {
                Text = pid.ToString(CultureInfo.InvariantCulture),
                Number = pid,
                Flag = true,
            };
        }
        catch
        {
            // 进程可能瞬间退出、句柄已失效：启动本身成功，给个布尔结果即可
            return VarValue.FromBool(true);
        }
        finally { process.Dispose(); }
    }

    // —— openFile / openFolder / openUrl（ShellExecute 走默认程序）——
    private static VarValue? ShellExecute(ActionDto a)
    {
        string wd = AppPaths.ResolveWorkdir(a.Workdir);
        string? target = a.Type switch
        {
            // openFile/openFolder：相对路径解析到数据根 / 工作目录（开机自启时 CWD=System32，否则相对文件会找不到）
            ActionType.openFile => AppPaths.ResolvePath(a.Path, wd, allowDirectory: false),
            ActionType.openFolder => AppPaths.ResolvePath(a.Path, wd, allowDirectory: true),
            ActionType.openUrl => a.Url,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"{a.Type} 缺少路径/URL: {a.Name}");
        var psi = new ProcessStartInfo(target!) { UseShellExecute = true };
        if (a.Type != ActionType.openUrl && !string.IsNullOrEmpty(wd) && Directory.Exists(wd))
            psi.WorkingDirectory = wd;
        Process.Start(psi);
        return new VarValue { Text = target!, Flag = true };
    }

    // —— runCommand（复用 TerminalLauncher 终端路由；填了输出变量时后台捕获输出）——
    private static VarValue? RunCommand(ActionDto a, RunContext? ctx)
    {
        // 输出变量 = 需要结果：改为后台执行并捕获 stdout / stderr / 退出码（不再打开终端）。
        // 捕获模式没有窗口，「保持窗口」在这里不适用；记一条日志，便于解释「勾了却没保留」。
        if (!string.IsNullOrWhiteSpace(a.OutputVar))
        {
            if (a.KeepWindow == true)
                AppLog.Info("Action", $"「{a.Name}」填了输出变量，改为后台捕获执行（不显示窗口），已忽略「保持窗口」");
            return RunCommandCaptured(a, ctx);
        }

        string cmd = a.Cmd ?? "";
        string terminal = a.Terminal ?? "direct";
        bool keepWindow = a.KeepWindow ?? false;
        string workdir = a.Workdir ?? "";

        // direct / 空 terminal：走 ShellExecute（TerminalLauncher 的 direct 分支为 no-op）
        if (terminal == "direct" || terminal.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(cmd))
                throw new InvalidOperationException($"runCommand 缺少 Cmd: {a.Name}");
            if (keepWindow)
                AppLog.Info("Action", $"「{a.Name}」终端为「直接运行」，没有窗口可保持，已忽略「保持窗口」");
            RunDirect(cmd, workdir);
            return null;
        }

        var r = TerminalLauncher.TryBuildLaunch(terminal, keepWindow, cmd, workdir);
        if (!r.Ok)
        {
            if (r.Error == null) return null; // 空命令仅开 shell 无 exe 时的 no-op
            TrayService.Notify(r.Error); // 替代原 MenuSystem 的模态提示，不阻塞动作路径
            throw new InvalidOperationException(r.Error);
        }
        Process.Start(BuildPsi(r.Exe!, r.Args, r.Workdir));
        return null;
    }

    /// <summary>单条输出最多保留的字符数（防止命令刷屏把内存吃满）。</summary>
    private const int MaxCapturedChars = 200_000;

    /// <summary>没有流程上下文时，捕获型命令的最长等待时间。</summary>
    private const int MaxCaptureMs = 10 * 60 * 1000;

    private static readonly Encoding? OemEncoding = CreateOemEncoding();

    private static Encoding? CreateOemEncoding()
    {
        try
        {
            // .NET 默认不带 OEM 代码页；注册一次即可按系统 OEM 编码解码 cmd 的输出（中文不乱码）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return null;   // 拿不到就退回运行时默认编码
        }
    }

    /// <summary>Git Bash / WSL 的管道输出是 UTF-8（无 BOM）。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// 按「运行命令」里选的终端构造捕获用的启动信息：不使用 keepWindow / Windows Terminal 包装，
    /// 直接跑 shell 的「执行一条命令」形式，才能从管道拿到输出。
    /// Git Bash / WSL 的管道输出是 UTF-8；cmd 与 PowerShell 默认跟随系统 OEM 代码页（中文 Windows 为 GBK）。
    /// </summary>
    private static ProcessStartInfo BuildCapturePsi(ActionDto a, string cmd, string wd)
    {
        string terminal = a.Terminal ?? "direct";

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (terminal is "pwsh7" or "pwsh5" or "gitbash" or "wslbash")
        {
            string? shell = TerminalLauncher.Resolve(terminal);
            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                psi.FileName = shell;
                switch (terminal)
                {
                    case "pwsh7":
                    case "pwsh5":
                        psi.ArgumentList.Add("-NoProfile");
                        psi.ArgumentList.Add("-NonInteractive");
                        psi.ArgumentList.Add(terminal == "pwsh7" ? "-c" : "-Command");
                        psi.ArgumentList.Add(cmd);
                        break;
                    case "gitbash":
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(cmd);
                        break;
                    case "wslbash":
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-lc");
                        psi.ArgumentList.Add(cmd);
                        break;
                }

                // Git Bash / WSL 固定 UTF-8；cmd 与 PowerShell 的管道输出跟随系统 OEM 代码页。
                bool utf8 = terminal is "gitbash" or "wslbash";
                psi.StandardOutputEncoding = utf8 ? Utf8NoBom : OemEncoding;
                psi.StandardErrorEncoding = utf8 ? Utf8NoBom : OemEncoding;

                if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd))
                    psi.WorkingDirectory = wd;
                return psi;
            }

            AppLog.Warn("Action",
                $"未找到 {TerminalLauncher.DisplayLabel(terminal)}，捕获执行改用 cmd.exe：{a.Name}");
        }

        // direct / cmd / wt（wt 只是终端宿主，捕获时不需要它）
        psi.FileName = "cmd.exe";
        psi.Arguments = "/d /s /c \"" + cmd + "\"";
        psi.StandardOutputEncoding = OemEncoding;
        psi.StandardErrorEncoding = OemEncoding;
        if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd))
            psi.WorkingDirectory = wd;
        return psi;
    }

    /// <summary>
    /// 后台执行命令并捕获输出（供「结果存到变量」使用）。
    /// 隐藏窗口执行（cmd / 所选 shell），等待进程退出后返回
    /// <c>Text</c>=标准输出、<c>Error</c>=错误输出、<c>Number</c>=退出码、<c>Flag</c>=是否成功。
    /// 流程里可按停止热键取消（连同子进程一起结束）。
    /// </summary>
    private static VarValue RunCommandCaptured(ActionDto a, RunContext? ctx)
    {
        string cmd = (a.Cmd ?? "").Trim();
        if (cmd.Length == 0)
            throw new InvalidOperationException($"runCommand 缺少 Cmd: {a.Name}");

        string wd = AppPaths.ResolveWorkdir(a.Workdir);
        var psi = BuildCapturePsi(a, cmd, wd);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动命令: {a.Name}");

        // 先异步把两条流读空，避免子进程写满管道缓冲区后卡住
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        long started = Environment.TickCount64;
        while (!process.HasExited)
        {
            if (ctx != null)
            {
                if (!ctx.Wait(100))
                {
                    TryKill(process);
                    throw new FlowAbortException("已停止", userCancelled: true);
                }
            }
            else
            {
                Thread.Sleep(100);
                if (Environment.TickCount64 - started > MaxCaptureMs)
                {
                    TryKill(process);
                    throw new InvalidOperationException($"命令运行超过 {MaxCaptureMs / 60000} 分钟，已中止: {a.Name}");
                }
            }
        }

        string stdout = Cap(stdoutTask.GetAwaiter().GetResult());
        string stderr = Cap(stderrTask.GetAwaiter().GetResult());
        int exitCode = process.ExitCode;

        return new VarValue
        {
            Text = stdout.TrimEnd('\r', '\n'),
            Error = stderr.TrimEnd('\r', '\n'),
            Number = exitCode,
            Flag = exitCode == 0,
        };

        static string Cap(string text) => text.Length <= MaxCapturedChars
            ? text
            : text[..MaxCapturedChars] + "\n…（输出过长，已截断）";
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已退出 / 无权限 */ }
    }

    /// <summary>direct 终端：拆 exe+args 启动，失败回退 ShellExecute（URL/文档/含空格非可执行首段）。</summary>
    private static void RunDirect(string cmd, string workdir)
    {
        string wd = AppPaths.ResolveWorkdir(workdir);
        try
        {
            TrySplitCommandLine(cmd, out string exe, out string args);
            Process.Start(BuildPsi(AppPaths.ResolveTarget(exe, wd), args, wd));
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

    // —— setClipboard（写入剪贴板）——

    /// <summary>
    /// 把文本写进剪贴板（不发送、不按键）。文本为空表示清空剪贴板。
    /// 剪贴板被占用时的重试在 <see cref="NativeClipboard.SetText"/> 内部完成。
    /// </summary>
    private static VarValue? SetClipboard(ActionDto a)
    {
        string text = a.Text ?? "";
        if (!NativeClipboard.SetText(text))
            throw new InvalidOperationException($"写入剪贴板失败（剪贴板被其他程序占用？）: {a.Name}");
        return new VarValue { Text = text, Flag = true };
    }

    // —— toast（提示弹窗）——
    //
    // 只是显示：投递到 UI 线程就返回，下一步立刻继续。提示卡片不抢焦点（WS_EX_NOACTIVATE），
    // 自动化正在往别的窗口输入时也不会被它打断。

    private static void ShowToast(ActionDto a)
    {
        string text = a.Text ?? "";
        if (text.Length == 0)
        {
            // 空正文（比如 ${变量} 取出来是空串）没什么可看的：记一条日志，不弹空气泡。
            AppLog.Warn("Action", $"「{a.Name}」的提示内容为空，已跳过");
            return;
        }

        int duration = Math.Clamp(a.DurationMs ?? ToastService.DefaultDurationMs, 0, 600_000);
        ToastService.Show(text, string.IsNullOrWhiteSpace(a.Name) ? null : a.Name, duration);
    }

    // —— sendText（发送文本，§3.2.2）——
    private static void SendText(ActionDto a)
    {
        string text = a.Text ?? "";
        if (text.Length == 0)
            throw new InvalidOperationException($"sendText 缺少 Text: {a.Name}");

        // 焦点延时（§12）：面板/菜单关闭 → 目标窗口焦点切回有时间差，立即发键可能丢焦点。
        Thread.Sleep(50);

        DispatchText(a, text, a.Mode ?? SendTextMode.auto);

        if (a.AppendEnter == true)
            InputHelper.Tap((ushort)Win32.VkReturn);
    }

    /// <summary>
    /// 按 <paramref name="mode"/> 发送文本（不含焦点延时、不含 AppendEnter）。
    /// 供 sendText 动作（前置延时后调用）与 sendKeys 的 text 条目（无延时）共用。
    /// </summary>
    /// <remarks>
    /// 三模式语义（§12「输入法干扰修复」修订）：
    /// <list type="bullet">
    /// <item><b>auto（默认）</b>：<c>KEYEVENTF_UNICODE</c> 批量注入——绕过键盘布局与输入法，
    /// 中英文/emoji 均可发，<b>不碰剪贴板</b>（修复「纯 ASCII 键入被中文输入法吞」+「剪贴板残留历史」）。</item>
    /// <item><b>type</b>：<c>VkKeyScanW</c> 键入——真实模拟按键，受输入法影响（用户明确要求时才用）。</item>
    /// <item><b>paste</b>：剪贴板粘贴 + 150ms 后恢复原剪贴板（仅文本格式可恢复）。</item>
    /// </list>
    /// </remarks>
    private static void DispatchText(ActionDto a, string text, SendTextMode mode)
    {
        if (mode == SendTextMode.paste)
        {
            // 先备份原剪贴板文本（非文本内容如图片无法经文本接口恢复——粘贴模式必然覆盖）。
            bool hadOld = NativeClipboard.TryGetText(out var oldText);
            if (!NativeClipboard.SetText(text))
                throw new InvalidOperationException($"sendText 写剪贴板失败: {a.Name}");
            InputHelper.Combo((ushort)Win32.VkControl, (ushort)'V');
            // 等目标窗口读走剪贴板再恢复，否则粘到原内容；150ms 对前台应用足够。
            Thread.Sleep(150);
            // 仅当剪贴板仍是我们写入的文本（用户未在此期间复制新内容）才恢复，避免覆盖新复制。
            if (hadOld && NativeClipboard.TryGetText(out var now) && now == text)
                NativeClipboard.SetText(oldText);
        }
        else if (mode == SendTextMode.type)
        {
            TypeText(a, text);
        }
        else // auto（默认）
        {
            InputHelper.SendUnicodeText(text);
        }
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

    // —— sendKeys（模拟按键，§3.2.1）——
    private static void SendKeys(ActionDto a)
    {
        if (a.Items == null || a.Items.Count == 0)
            throw new InvalidOperationException($"sendKeys 缺少 Items: {a.Name}");

        // 焦点延时（§12，与 sendText 一致）
        Thread.Sleep(50);

        for (int i = 0; i < a.Items.Count; i++)
        {
            var item = a.Items[i];
            switch (item.Kind)
            {
                case KeyItemKind.chord:
                    if (item.Strokes == null || item.Strokes.Count == 0) break;
                    for (int s = 0; s < item.Strokes.Count; s++)
                    {
                        // stroke 间固定 ~15ms（菜单流需应用响应时间；更大间隔用 sleep 条目）
                        if (s > 0) Thread.Sleep(15);
                        if (!KeyStroke.TryParse(item.Strokes[s], out var vks, out var err))
                            throw new InvalidOperationException(
                                $"sendKeys 步骤{i + 1} 无法解析按键 \"{item.Strokes[s]}\": {err}");
                        InputHelper.Combo(vks.ToArray());
                    }
                    break;
                case KeyItemKind.text:
                    // 内联短文本条目：复用 sendText 的 auto 逻辑（不延时、不追加回车）
                    var t = item.Text ?? "";
                    if (t.Length > 0) DispatchText(a, t, SendTextMode.auto);
                    break;
                case KeyItemKind.sleep:
                    if (item.Ms > 0) Thread.Sleep(item.Ms);
                    break;
            }
        }
    }

    // —— macro（回放录制的键鼠宏）——
    private static void PlayMacro(ActionDto a)
    {
        if (string.IsNullOrWhiteSpace(a.MacroId))
            throw new InvalidOperationException($"macro 缺少 MacroId: {a.Name}");

        var macro = MacroStore.Load(a.MacroId!)
            ?? throw new InvalidOperationException($"宏不存在: {a.MacroId}");

        MacroPlayer.PlaySync(macro);
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

    // —— mouse（鼠标操作）——

    private static VarValue? Mouse(ActionDto a)
    {
        string sx = a.X?.Trim() ?? "";
        string sy = a.Y?.Trim() ?? "";

        // 坐标可以留空 = 用当前光标位置（滚轮动作最常用）。填了就必须是整数（模板在流程里已展开）。
        if (sx.Length > 0 || sy.Length > 0)
        {
            if (!int.TryParse(sx, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
                !int.TryParse(sy, NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                throw new InvalidOperationException($"mouse 坐标无效: X={a.X}, Y={a.Y} ({a.Name})");

            MouseOps.Move(x, y);
            Thread.Sleep(10);   // 让光标位置先落定，再发按键
        }

        MouseOps.Perform(a.MouseAction ?? MouseActionKind.click, a.WheelDelta ?? 120);

        return Win32.GetCursorPos(out var point) ? VarValue.FromPoint(point.X, point.Y) : null;
    }
}
