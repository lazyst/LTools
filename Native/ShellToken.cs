using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>
/// 以 **Explorer（普通权限）的令牌** 启动一个进程。
/// </summary>
/// <remarks>
/// <para>
/// 提权进程直接 `CreateProcess` 出来的子进程会继承高完整性令牌；而 UIPI 又让高完整性窗口收不到
/// Explorer 的拖放，所以「共享助手」必须是普通权限的（见 `docs/LAN_SHARE.md` §6）。
/// </para>
/// <para>
/// 试过但**不能用**的两种办法：`explorer.exe &lt;exe&gt;`（会把资源管理器窗口叫到前台，用户报过）、
/// `Shell.Application.ShellExecute`（在提权进程里是**进程内**对象，子进程照样提权）。
/// 这里复制 shell 的令牌再用 <c>CreateProcessWithTokenW</c> 起进程：不需要 `SeAssignPrimaryToken`，
/// 只要 `SeImpersonate`（管理员令牌自带），也不会碰资源管理器的窗口。
/// </para>
/// </remarks>
internal static class ShellToken
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenAllAccess = 0x000F01FF;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenElevation = 20;

    /// <summary>在 Explorer 的令牌下启动 <paramref name="path"/>（失败返回 false，调用方自己兜底）。</summary>
    public static bool Start(string path)
    {
        if (!File.Exists(path)) return false;

        int session = Process.GetCurrentProcess().SessionId;

        foreach (Process shell in Process.GetProcessesByName("explorer"))
        {
            if (shell.SessionId != session) continue;

            IntPtr process = IntPtr.Zero;
            IntPtr token = IntPtr.Zero;
            IntPtr primary = IntPtr.Zero;
            try
            {
                process = OpenProcess(ProcessQueryLimitedInformation, false, shell.Id);
                if (process == IntPtr.Zero) continue;

                if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out token)) continue;

                // 用户可能「以管理员身份」跑过 Explorer（罕见）：那个令牌是提权的，拿它启动的助手
                // 照样收不到拖放。跳过它，找下一个普通权限的 Explorer。
                if (IsElevated(token)) continue;

                if (!DuplicateTokenEx(token, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary)) continue;

                var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
                string commandLine = "\"" + path + "\"";
                string workdir = Path.GetDirectoryName(path) ?? "";

                if (!CreateProcessWithTokenW(primary, 0, null, commandLine, CreateUnicodeEnvironment,
                        IntPtr.Zero, workdir, ref startup, out PROCESS_INFORMATION info))
                {
                    continue;
                }

                CloseHandle(info.hThread);
                CloseHandle(info.hProcess);
                return true;
            }
            catch
            {
                // 换下一个 Explorer 试
            }
            finally
            {
                if (primary != IntPtr.Zero) CloseHandle(primary);
                if (token != IntPtr.Zero) CloseHandle(token);
                if (process != IntPtr.Zero) CloseHandle(process);
                shell.Dispose();
            }
        }

        return false;
    }

    /// <summary>令牌是不是提权的（TokenElevation 非 0）。查询失败按「提权」处理，免得拿到坏令牌。</summary>
    private static bool IsElevated(IntPtr token)
    {
        try
        {
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _)) return true;
            return elevated != 0;
        }
        catch
        {
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint access, IntPtr attributes,
        int impersonationLevel, int tokenType, out IntPtr primaryToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName,
        string commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
