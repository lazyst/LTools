using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace LTools.Core;

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
/// </summary>
internal static class AutoStartService
{
    private const string TaskName = "LTools";

    /// <summary>当前 exe 全路径（任务计划 Command）。</summary>
    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static bool _legacyCleaned;

    /// <summary>
    /// 是否已启用自启（查询任务计划是否存在）。
    /// 首次查询时顺带清理改名前的旧任务 <c>CapsLockPro</c>——其 /tr 指向已不存在的旧 exe。
    /// </summary>
    public static bool IsEnabled()
    {
        CleanupLegacyTask();
        var psi = new ProcessStartInfo("schtasks", $"/query /tn {TaskName} /fo LIST")
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
            p.WaitForExit(3000);
            return p.ExitCode == 0;   // 0 = 任务存在
        }
        catch { return false; }
    }

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
