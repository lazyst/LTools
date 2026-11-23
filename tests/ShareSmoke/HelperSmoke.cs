using System.Diagnostics;
using System.IO;

namespace ShareSmoke;

/// <summary>
/// 共享助手（<c>ShareHost.exe</c>）的冒烟段：它是**普通权限**进程，从资源管理器拖文件进来要能落进共享区。
/// </summary>
/// <remarks>
/// 助手自己按 exe 位置解析数据根（仓库根），而冒烟进程的数据根在别处，所以这里不调
/// <c>ShareHelper</c>，直接往 <c>&lt;仓库&gt;/share/host.intent</c> 写意图——和主程序做的是同一件事。
/// 拖放会往真实清单里加一条，跑完把 <c>items.json</c> 原样还原。
/// </remarks>
internal static class HelperSmoke
{
    public static void Run(Action<bool, string> check, string repo)
    {
        string exe = Path.Combine(repo, "bin", "Debug", "net10.0-windows10.0.19041.0", "ShareHost.exe");
        string intentPath = Path.Combine(repo, "share", "host.intent");
        string itemsPath = Path.Combine(repo, "share", "items.json");
        byte[]? backup = File.Exists(itemsPath) ? File.ReadAllBytes(itemsPath) : null;
        string temp = Path.Combine(Path.GetTempPath(), "ltools-helper-smoke");

        Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
        Directory.CreateDirectory(temp);

        check(File.Exists(exe), "ShareHost.exe 和 LTools.exe 在同一个目录");
        if (!File.Exists(exe)) return;

        try
        {
            foreach (var stale in Process.GetProcessesByName("ShareHost")) stale.Kill();
            WriteIntent(intentPath, "show");

            if (Process.GetProcessesByName("ShareHost").Length == 0)
            {
                // 主程序（提权）就是这么拉它的：经 Explorer，子进程才是普通权限。
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
            }

            IntPtr hwnd = DragSmoke.WaitProcessWindow("ShareHost", 12000);
            check(hwnd != IntPtr.Zero, "拉起助手，窗口出来了");
            if (hwnd == IntPtr.Zero) return;

            check(DragSmoke.ElevationOf(hwnd) == 0, "助手跑在普通权限下（拖放能成立的前提）");

            DragSmoke.ParkRight(hwnd);
            DragSmoke.DragFileInto(check, temp, hwnd, "助手拖我.txt");

            check(ItemsContain(itemsPath, "助手拖我.txt"), "真资源管理器拖进助手窗口：文件进了共享区");
        }
        finally
        {
            WriteIntent(intentPath, "stop");
            for (int i = 0; i < 24 && Process.GetProcessesByName("ShareHost").Length > 0; i++) Thread.Sleep(150);
            foreach (var leftover in Process.GetProcessesByName("ShareHost"))
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
