using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LTools.Core.Share;

/// <summary>
/// 共享助手进程（<c>ShareHost.exe</c>）的唯一出入口。
/// </summary>
/// <remarks>
/// <para>
/// 主程序是 <c>requireAdministrator</c>，而 Explorer 只会把文件拖给**普通权限**的窗口
/// （UIPI 拦「中完整性 → 高完整性」的拖放），所以共享窗口放在一个 asInvoker 的独立进程里
/// ——和 Quicker「界面普通权限、提权按需」是同一个思路，只是范围只有这一个窗口。
/// </para>
/// <para>
/// 两个进程之间只有两样东西：一个命名互斥体（在不在跑）与 <c>share/host.intent</c>
/// （要它做什么：<c>run</c> / <c>show</c> / <c>stop</c>）。**用文件而不是命令行**：
/// 提权进程没法直接生普通权限子进程（子进程会继承高完整性令牌），只能经 <c>explorer.exe</c>
/// 拉起，而 Explorer 不转发参数。
/// </para>
/// <para>
/// 意图文件是「最后一次要求」而不是队列：助手按内容执行一次就删，重复写同一个值没有副作用。
/// </para>
/// </remarks>
internal static class ShareHelper
{
    public const string ExeName = "ShareHost.exe";

    /// <summary>会话内唯一的互斥体名；助手进程持有它直到退出。</summary>
    public const string MutexName = @"Local\LToolsShareHost";

    public const string RunIntent = "run";
    public const string ShowIntent = "show";
    public const string StopIntent = "stop";

    public static string ExePath => Path.Combine(AppPaths.AppDirectory, ExeName);

    public static string IntentPath => Path.Combine(AppPaths.ShareDir, "host.intent");

    /// <summary>助手可执行文件在不在（打包漏了就直说，别静默什么都不发生）。</summary>
    public static bool IsAvailable => File.Exists(ExePath);

    public static bool IsRunning => MutexExists();

    /// <summary>拉起（窗口保持隐藏）。已经在跑就什么都不做——服务由它自己按配置决定开不开。</summary>
    public static void Start() => Send(RunIntent);

    /// <summary>拉起并显示窗口；已经在跑就唤起。</summary>
    public static void Open() => Send(ShowIntent);

    /// <summary>让它停下（服务与进程一起）。没在跑就只清掉残留意图。</summary>
    public static void Stop()
    {
        if (IsRunning) Write(StopIntent);
        else ClearIntent();
    }

    /// <summary>读一次意图（助手用）；没有 / 读不到就是 null。</summary>
    public static string? ReadIntent()
    {
        try
        {
            return File.Exists(IntentPath) ? File.ReadAllText(IntentPath).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行完把意图删掉；期间主程序改成了别的值就留着（下一轮再看）。</summary>
    public static bool Consume(string intent)
    {
        try
        {
            if (!File.Exists(IntentPath)) return false;
            if (!string.Equals(File.ReadAllText(IntentPath).Trim(), intent, StringComparison.Ordinal)) return false;
            File.Delete(IntentPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ClearIntent()
    {
        try { if (File.Exists(IntentPath)) File.Delete(IntentPath); } catch { /* 删不掉下次覆盖 */ }
    }

    private static void Send(string intent)
    {
        if (!IsAvailable)
        {
            ToastService.Show($"找不到 {ExeName}：它必须和 LTools.exe 放在同一个目录",
                null, ToastService.LongMs, ToastKind.error);
            return;
        }

        Write(intent);

        // 已经在跑：它自己会在下一轮轮询里看到这个意图。
        if (IsRunning) return;

        try
        {
            // 交给 Explorer（中完整性）替我们拉起来；直接 CreateProcess 会继承我们的高完整性令牌，
            // 那样窗口照样收不到拖放，整个助手就白拆了。
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Share", $"经 Explorer 启动共享助手失败，改为直接启动（拖放会失效）：{ex.Message}");
            try
            {
                Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
            }
            catch (Exception inner)
            {
                ToastService.Show("打不开共享窗口：" + inner.Message, null, ToastService.LongMs, ToastKind.error);
            }
        }
    }

    private static void Write(string intent)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ShareDir);
            string temp = IntentPath + ".tmp";
            File.WriteAllText(temp, intent);
            File.Move(temp, IntentPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Share", $"写共享助手指令失败：{ex.Message}");
        }
    }

    private static bool MutexExists()
    {
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch
        {
            // 没建 / 权限不够 / 上一个进程刚退，一律按「没在跑」处理（下次会重新拉起）。
            return false;
        }
    }
}
