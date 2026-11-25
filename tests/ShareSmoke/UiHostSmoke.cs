using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareSmoke;

/// <summary>
/// 界面宿主（<c>UiHost.exe</c>）的冒烟段：它是**普通权限**进程，从资源管理器拖文件进来要能落进共享区。
/// </summary>
/// <remarks>
/// 助手自己按 exe 位置解析数据根（仓库根），而冒烟进程的数据根在别处，所以这里不调
/// <c>UiHostProcess</c>，直接往 <c>&lt;仓库&gt;/ui-host/intent.txt</c> 写意图——和主程序做的是同一件事。
/// 拖放会往真实清单里加一条，跑完把 <c>items.json</c> 原样还原。
/// <para>
/// 拉起方式优先走**产品自己的那条路**（<c>LTools.Native.ShellToken</c>：复制 Explorer 的令牌），
/// 并断言它不会把资源管理器叫到前台——用户报过「第一次打开共享，资源管理器跳出来了」。
/// 冒烟不是提权运行时 <c>ShellToken</c> 起不来，退回 <c>explorer.exe &lt;exe&gt;</c>，那条断言就跳过。
/// </para>
/// </remarks>
internal static class UiHostSmoke
{
    public static void Run(Action<bool, string> check, string repo)
    {
        string exe = Path.Combine(repo, "bin", "Debug", "net10.0-windows10.0.19041.0", "UiHost.exe");
        string intentPath = Path.Combine(repo, "ui-host", "intent.txt");
        string itemsPath = Path.Combine(repo, "share", "items.json");
        byte[]? backup = File.Exists(itemsPath) ? File.ReadAllBytes(itemsPath) : null;
        string temp = Path.Combine(Path.GetTempPath(), "ltools-helper-smoke");

        Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
        Directory.CreateDirectory(temp);

        check(File.Exists(exe), "UiHost.exe 和 LTools.exe 在同一个目录");
        if (!File.Exists(exe)) return;

        try
        {
            foreach (var stale in Process.GetProcessesByName("UiHost")) stale.Kill();
            WriteIntent(intentPath, "show");

            int cabinetsBefore = CabinetWindows();
            IntPtr foregroundBefore = GetForegroundWindow();

            bool viaToken = Process.GetProcessesByName("UiHost").Length == 0 && TryLaunchViaShellToken(exe);

            if (Process.GetProcessesByName("UiHost").Length == 0)
            {
                // 老办法（会把资源管理器叫到前台）：冒烟不是提权 / 复制令牌失败时的兜底。
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
            }

            IntPtr hwnd = DragSmoke.WaitProcessWindow("UiHost", 12000);
            check(hwnd != IntPtr.Zero, "拉起宿主，窗口出来了");
            if (hwnd == IntPtr.Zero) return;

            check(DragSmoke.ElevationOf(hwnd) == 0, "助手跑在普通权限下（拖放能成立的前提）");

            // 复制令牌那条路不该碰资源管理器的窗口（有一条走 explorer 时这条不成立，跳过）。
            if (viaToken)
            {
                IntPtr foregroundAfter = GetForegroundWindow();
                bool explorerRaised = IsCabinet(foregroundAfter) && foregroundAfter != foregroundBefore;
                check(CabinetWindows() == cabinetsBefore && !explorerRaised,
                    "拉起宿主没有唤醒资源管理器窗口（前台还在原来的窗口上）");
            }

            DragSmoke.ParkRight(hwnd);
            DragSmoke.DragFileInto(check, temp, hwnd, "宿主拖我.txt");

            check(ItemsContain(itemsPath, "宿主拖我.txt"), "真资源管理器拖进宿主窗口：文件进了共享区");
        }
        finally
        {
            WriteIntent(intentPath, "stop");
            for (int i = 0; i < 24 && Process.GetProcessesByName("UiHost").Length > 0; i++) Thread.Sleep(150);
            foreach (var leftover in Process.GetProcessesByName("UiHost"))
            {
                try { leftover.Kill(); } catch { /* 已经退了 */ }
            }

            try
            {
                if (backup != null) File.WriteAllBytes(itemsPath, backup);
                else if (File.Exists(itemsPath)) File.Delete(itemsPath);
            }
            catch { /* 还原失败不该让整段变红 */ }
        }
    }

    /// <summary>走产品那条路拉起宿主（复制 Explorer 的令牌）；不是提权进程时返回 false。</summary>
    private static bool TryLaunchViaShellToken(string exe)
    {
        try
        {
            Type? type = typeof(LTools.Features.ActionDto).Assembly.GetType("LTools.Native.ShellToken");
            MethodInfo? start = type?.GetMethod("Start", BindingFlags.Public | BindingFlags.Static);
            if (start == null) return false;
            return start.Invoke(null, new object[] { exe }) is true;
        }
        catch
        {
            return false;
        }
    }

    private static int CabinetWindows()
    {
        int count = 0;
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && IsCabinet(h)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }

    private static bool IsCabinet(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        GetClassNameW(hwnd, name, name.Capacity);
        return name.ToString() == "CabinetWClass";
    }

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int size);

    private static void WriteIntent(string path, string intent)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, intent);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>清单里有没有这个名字（按 JSON 解析，别拿原文字符串比——中文会被转义成 \uXXXX）。</summary>
    private static bool ItemsContain(string path, string name)
    {
        try
        {
            if (!File.Exists(path)) return false;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                foreach (string key in new[] { "name", "Name" })
                {
                    if (item.TryGetProperty(key, out var value) && value.GetString() == name) return true;
                }
            }
        }
        catch
        {
            // 读不到就当没有（断言会红，原因写在消息里）
        }

        return false;
    }
}
