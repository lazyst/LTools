using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace LTools.Core;

/// <summary>自启任务相对「当前 exe」的状态。</summary>
internal enum AutoStartState
{
    /// <summary>没有自启任务。</summary>
    Disabled,

    /// <summary>任务指向当前 exe，自启有效。</summary>
    Enabled,

    /// <summary>任务指向另一条路径（便携目录被挪动、或另一份拷贝抢过任务名），自启可能已失效。</summary>
    Stale,
}

/// <summary>自启任务的查询结果。</summary>
internal readonly record struct AutoStartInfo(
    AutoStartState State,
    string? RegisteredPath,
    bool RegisteredPathExists)
{
    /// <summary>任务是否存在（不论指向哪里）——复选框是否勾上用它判断。</summary>
    public bool TaskExists => State != AutoStartState.Disabled;
}

/// <summary>
/// 开机自启管理（任务计划程序 + highest privileges）。
///
/// 为什么用任务计划而非注册表 Run 键：本应用是 requireAdministrator 提权程序，
/// 注册表 Run 启动提权程序会弹 UAC 确认；任务计划可静默以最高权限启动，开机无 UAC 弹窗，体验更好。
///
/// 任务以 <c>/create /xml</c> 注册（登录触发 + HighestAvailable），任务名固定 <c>LTools</c>，
/// <c>Command</c> 指向当前 exe 全路径。必须用 XML 而非 <c>/sc ONLOGON /rl HIGHEST</c>：
/// 后者默认勾选「仅交流电源启动」「切换到电池即停止」，笔记本掉一次交流电就杀掉常驻进程
/// （见 <see cref="Enable"/> 与 CHANGELOG「笔记本离开后 LTools 无声退出」）。
///
/// 任务里存的是注册那一刻的绝对路径。便携版把目录挪走（下载 → 解压 → 移到 D:\Tools 很常见）后，
/// 任务仍在但指向已消失的 exe，登录时静默失败（<c>MultipleInstancesPolicy=IgnoreNew</c>，失败不提示），
/// 而设置页只查「任务是否存在」就会一直显示已启用。故这里除了注册 / 删除，还提供
/// <see cref="Query"/>（带路径比对）与 <see cref="HealIfMoved"/>（启动时自愈）。
/// </summary>
internal static class AutoStartService
{
    private const string TaskName = "LTools";

    /// <summary>当前 exe 全路径（任务计划 Command）。</summary>
    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static bool _legacyCleaned;

    // ============ 查询 ============

    /// <summary>
    /// 查询任务状态：有没有任务、指向谁、那条路径是否还在。
    /// 供设置页显示「已启用 / 路径已失效」，也供启动自愈判断。
    /// </summary>
    public static AutoStartInfo Query()
    {
        CleanupLegacyTask();

        string? registered = QueryTaskCommand();
        if (string.IsNullOrEmpty(registered))
            return new AutoStartInfo(AutoStartState.Disabled, null, false);

        bool same = SamePath(registered, ExePath);
        return new AutoStartInfo(
            same ? AutoStartState.Enabled : AutoStartState.Stale,
            registered,
            File.Exists(registered));
    }

    /// <summary>
    /// 启动时的路径对齐：任务指向的 exe 不是当前程序，就改写为当前 exe。
    /// 同一个任务名只能服务一份程序——留两套路径只会让「到底谁自启」说不清，
    /// 所以以正在运行的这份为准（用户刚把它启动起来，说明想用的是它）。
    /// </summary>
    public static void HealIfStale()
    {
        var info = Query();
        if (info.State != AutoStartState.Stale) return;

        var exe = ExePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

        if (Enable())
            AppLog.Info("Startup", $"自启任务已改为指向当前程序：{info.RegisteredPath} → {exe}");
        else
            AppLog.Warn("Startup", $"自启任务仍指向 {info.RegisteredPath}，改写失败（可能缺少管理员权限）");
    }

    /// <summary>
    /// 读任务计划里的可执行文件路径；任务不存在 / 查询失败时返回 null。
    ///
    /// 用任务计划 COM 而不是 <c>schtasks /query /xml</c>：后者把 XML 写进控制台代码页
    /// （中文 Windows 是 GBK），.NET 按 UTF-8 解码会把中文路径解成乱码，从而误判「路径不符」
    /// 并在每次启动时重写任务。COM 返回 BSTR，与代码页无关。
    /// </summary>
    private static string? QueryTaskCommand()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;

            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();

            dynamic task = service.GetFolder("\\").GetTask(TaskName);
            dynamic actions = task.Definition.Actions;
            if ((int)actions.Count < 1) return null;

            dynamic action = actions.Item(1);
            // 0 = TASK_ACTION_EXEC；其它类型（COM handler 等）不是我们把关的形态
            return (int)action.Type == 0 ? (string?)action.Path : null;
        }
        catch (COMException)
        {
            return null;   // 0x80070002 = 任务不存在；其它 COM 失败也按「查不到」处理
        }
        catch
        {
            return null;
        }
    }

    private static bool SamePath(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return path.Trim().TrimEnd('\\'); }
    }

    // ============ 注册 / 删除 ============

    /// <summary>
    /// 启用自启：用任务 XML 注册 ONLOGON + HighestAvailable 任务（覆盖已有）。
    ///
    /// 为什么改用 /xml 而非 <c>/sc ONLOGON /rl HIGHEST</c>：
    /// 后者由 schtasks 创建时会默认勾上「仅交流电源启动」与「切换到电池即停止」两项电池条件
    /// （<c>DisallowStartIfOnBatteries</c> / <c>StopIfGoingOnBatteries</c>）。笔记本只要瞬间掉一次
    /// 交流电，任务计划程序就会把常驻进程终止，且登录触发器不会自动重启 —— LTools 就这样无声消失。
    /// 改用 XML 注册可显式关闭这两项，并把执行时长设为无限制（默认 72h 同样会杀常驻进程）。
    /// </summary>
    public static bool Enable()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

        var xmlPath = Path.Combine(Path.GetTempPath(), "LTools-task.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exe), Encoding.Unicode);
            // XML 文件含空格需引号；schtasks 用 UTF-16 编码读取
            return RunSchtasks($"/create /tn {TaskName} /xml \"{xmlPath}\" /f");
        }
        catch { return false; }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* 删除临时 XML 失败无所谓 */ }
        }
    }

    /// <summary>构造任务定义 XML（登录触发、最高权限、不因电池/时长被终止）。</summary>
    private static string BuildTaskXml(string exe)
    {
        // 用当前用户 SID 作为 Principal.UserId，避免域账号名带反斜杠在 XML 里转义出错
        var userId = WindowsIdentity.GetCurrent().User?.Value
                     ?? WindowsIdentity.GetCurrent().Name;

        return $$"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>LTools 登录自启（免 UAC 提权）</Description>
            <URI>\{{TaskName}}</URI>
          </RegistrationInfo>
          <Principals>
            <Principal id="Author">
              <UserId>{{userId}}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
            </LogonTrigger>
          </Triggers>
          <Actions Context="Author">
            <Exec>
              <Command>{{SecurityElement.Escape(exe)}}</Command>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    /// <summary>禁用自启：删除任务。</summary>
    public static bool Disable() => RunSchtasks($"/delete /tn {TaskName} /f");

    /// <summary>一次性删除改名前遗留的自启任务（改名 LTools 前任务名为 CapsLockPro）。</summary>
    private static void CleanupLegacyTask()
    {
        if (_legacyCleaned) return;
        _legacyCleaned = true;
        RunSchtasks("/delete /tn CapsLockPro /f");   // 任务不存在/无权限时静默失败
    }

    private static bool RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
