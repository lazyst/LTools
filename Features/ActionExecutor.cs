using System;
using System.Diagnostics;
using System.Globalization;
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
        var process = ProcessLauncher.StartApp(a.Target!, a.Args, a.Workdir ?? "");
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
        bool capture = !string.IsNullOrWhiteSpace(a.OutputVar);
        var outcome = CommandRunner.Run(new CommandRequest
        {
            Command = a.Cmd ?? "",
            Terminal = a.Terminal ?? "direct",
            KeepWindow = a.KeepWindow ?? false,
            Workdir = a.Workdir ?? "",
            Label = a.Name,
            Capture = capture,
        }, ctx);

        if (!outcome.Captured) return null;

        return new VarValue
        {
            Text = outcome.Stdout,
            Error = outcome.Stderr,
            Number = outcome.ExitCode,
            Flag = outcome.ExitCode == 0,
        };
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

        TextSender.Dispatch(text, a.Mode ?? SendTextMode.auto, a.Name);

        if (a.AppendEnter == true)
            InputHelper.Tap((ushort)Win32.VkReturn);
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
                    if (t.Length > 0) TextSender.Dispatch(t, SendTextMode.auto, a.Name);
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
