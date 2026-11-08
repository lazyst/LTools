using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.Selection;

/// <summary>资源管理器（或桌面）当前的文件夹与选中项。</summary>
internal sealed record ShellSelection(string Folder, IReadOnlyList<string> Paths);

/// <summary>
/// 通过 <c>Shell.Application</c> 读资源管理器的当前文件夹与选中项，桌面走 UI Automation。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 COM 而不是模拟 Ctrl+C</b>：不注入按键、不动剪贴板、毫秒级返回，和「读取选中文本」
/// 走 UIA 的取向一致。代价是只认资源管理器 / 桌面（第三方文件管理器拿不到），拿不到时由调用方
/// 明确降级成 <c>found = false</c>，或按用户开关改用 <see cref="SelectionFallback"/>。
/// </para>
/// <para>
/// <b>桌面不在 <c>ShellWindows</c> 里</b>：实测 Win11 的 <c>shell.Windows()</c> 只列资源管理器窗口，
/// 桌面得从 <c>Shell.NameSpace(0)</c> 取目录、用 UIA 读选中项的名字，再映射回桌面目录里的文件。
/// </para>
/// <para>
/// <b>Win11 的标签页</b>：同一个窗口的多个标签在 <c>ShellWindows</c> 里是多个条目、<c>HWND</c> 相同，
/// 用窗口标题（= 活动标签的文件夹名）挑出活动标签那个。
/// </para>
/// </remarks>
internal static class ShellBridge
{
    /// <summary>读前台资源管理器 / 桌面的当前文件夹与选中项；前台不是资源管理器时返回 null。</summary>
    public static ShellSelection? Foreground()
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return null;

        IntPtr root = Win32.GetAncestor(foreground, Win32.GaRoot);
        if (root == IntPtr.Zero) root = foreground;

        string className = WindowFinder.GetClass(root);
        bool desktop = IsDesktopClass(className);
        if (!desktop && !string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase))
            return null;

        string title = WindowTitle(root);

        try
        {
            return StaRunner.Run(() => Query(root, title, desktop), 2500, "shell-selection");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Selection", $"读资源管理器选择失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>所有打开的资源管理器窗口的当前文件夹（去重、按打开顺序）。</summary>
    public static IReadOnlyList<string> AllFolders()
    {
        try
        {
            return StaRunner.Run(QueryAllFolders, 2500, "shell-folders") ?? Array.Empty<string>();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Selection", $"枚举资源管理器窗口失败：{ex.Message}");
            return Array.Empty<string>();
        }
    }

    // ============ COM（在 STA 线程上跑） ============

    private static ShellSelection? Query(IntPtr hwnd, string title, bool desktop)
    {
        dynamic? shell = CreateShell();
        if (shell == null) return null;

        try
        {
            if (desktop) return QueryDesktop(shell);
            return QueryExplorer(shell, hwnd, title);
        }
        finally
        {
            Release(shell);
        }
    }

    private static ShellSelection? QueryExplorer(dynamic shell, IntPtr hwnd, string title)
    {
        dynamic windows = shell.Windows();
        int count = (int)windows.Count;

        ShellSelection? best = null;
        bool bestTitleMatch = false;

        for (int i = 0; i < count; i++)
        {
            dynamic? window = null;
            try
            {
                window = windows.Item(i);
                if ((int)window.HWND != unchecked((int)hwnd.ToInt64())) continue;

                string location = SafeText(() => (string)window.LocationName);
                string folder = "";
                var paths = new List<string>();

                try
                {
                    dynamic document = window.Document;
                    folder = (string)document.Folder.Self.Path;

                    dynamic selected = document.SelectedItems();
                    int n = (int)selected.Count;
                    for (int j = 0; j < n; j++)
                    {
                        try
                        {
                            string path = (string)selected.Item(j).Path;
                            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
                        }
                        catch { /* 某一项取不到不影响其它项 */ }
                    }
                }
                catch
                {
                    // 「此电脑」/「控制面板」这类没有 Folder 视图，跳过
                    continue;
                }

                if (folder.Length == 0) continue;

                bool titleMatch = location.Length > 0 &&
                                  title.Contains(location, StringComparison.OrdinalIgnoreCase);

                // 同 HWND 的多个标签：标题对得上的才是活动标签；都没对上就留最后一个
                if (best == null || titleMatch || !bestTitleMatch)
                {
                    best = new ShellSelection(folder, paths);
                    bestTitleMatch = titleMatch;
                }

                if (titleMatch) break;
            }
            catch
            {
                // 某个窗口正在关闭 / 还没初始化完
            }
            finally
            {
                Release(window);
            }
        }

        return best;
    }

    private static ShellSelection? QueryDesktop(dynamic shell)
    {
        string folder = "";
        try { folder = (string)shell.NameSpace(0).Self.Path; } catch { }
        if (folder.Length == 0) folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (folder.Length == 0) return null;

        var names = ReadDesktopSelectionNames();
        if (names.Count == 0) return new ShellSelection(folder, Array.Empty<string>());

        var entries = DesktopEntries();
        var paths = new List<string>();
        foreach (string name in names)
        {
            string? path = ResolveDesktopItem(name, folder, entries);
            if (path != null) paths.Add(path);
        }

        return new ShellSelection(folder, paths);
    }

    private static IReadOnlyList<string> QueryAllFolders()
    {
        dynamic? shell = CreateShell();
        if (shell == null) return Array.Empty<string>();

        try
        {
            dynamic windows = shell.Windows();
            int count = (int)windows.Count;

            var folders = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < count; i++)
            {
                dynamic? window = null;
                try
                {
                    window = windows.Item(i);
                    string path = (string)window.Document.Folder.Self.Path;
                    if (!string.IsNullOrWhiteSpace(path) && seen.Add(path)) folders.Add(path);
                }
                catch { /* 跳过没有文件夹视图的窗口 */ }
                finally { Release(window); }
            }

            return folders;
        }
        finally
        {
            Release(shell);
        }
    }

    private static dynamic? CreateShell()
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            return type == null ? null : Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Selection", $"创建 Shell.Application 失败：{ex.Message}");
            return null;
        }
    }

    // ============ 桌面（UIA） ============

    private static readonly Lazy<UIA3Automation> Automation = new(() => new UIA3Automation
    {
        ConnectionTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromMilliseconds(800),
    });

    /// <summary>桌面图标没有可用的 COM 选择接口，只能从 UIA 读「哪些项处于选中状态」。</summary>
    private static List<string> ReadDesktopSelectionNames()
    {
        var names = new List<string>();
        try
        {
            IntPtr desktop = Win32.FindWindow("Progman", null);
            if (desktop == IntPtr.Zero) desktop = Win32.FindWindow("WorkerW", null);
            if (desktop == IntPtr.Zero) return names;

            AutomationElement root = Automation.Value.FromHandle(desktop);
            foreach (var item in root.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
            {
                try
                {
                    var pattern = item.Patterns.SelectionItem.PatternOrDefault;
                    if (pattern == null || !pattern.IsSelected.ValueOrDefault) continue;

                    string name = item.Properties.Name.ValueOrDefault ?? "";
                    if (name.Length > 0) names.Add(name);
                }
                catch { /* 单个图标读失败不影响其它 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Selection", $"读桌面选中项失败：{ex.Message}");
        }
        return names;
    }

    /// <summary>桌面目录里的全部条目（含公共桌面），按文件名建索引。</summary>
    private static List<string> DesktopEntries()
    {
        var entries = new List<string>();
        foreach (string dir in DesktopDirs())
        {
            try
            {
                if (Directory.Exists(dir)) entries.AddRange(Directory.EnumerateFileSystemEntries(dir));
            }
            catch { /* 权限问题跳过 */ }
        }
        return entries;
    }

    private static IEnumerable<string> DesktopDirs()
    {
        string user = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (user.Length > 0) yield return user;
        if (common.Length > 0 && !string.Equals(user, common, StringComparison.OrdinalIgnoreCase))
            yield return common;
    }

    private static string? ResolveDesktopItem(string name, string folder, List<string> entries)
    {
        string direct = Path.Combine(folder, name);
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;

        foreach (string entry in entries)
        {
            if (string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase))
                return entry;
            if (string.Equals(Path.GetFileNameWithoutExtension(entry), name, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    // ============ 小工具 ============

    private static bool IsDesktopClass(string className) =>
        className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
        className.Equals("SysListView32", StringComparison.OrdinalIgnoreCase);

    private static string WindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>HWND 在 COM 里是 32 位 LONG；比较时统一截到低 32 位，避免符号扩展对不上。</summary>
    private static string SafeText(Func<string> read)
    {
        try { return read() ?? ""; }
        catch { return ""; }
    }

    private static void Release(object? com)
    {
        if (com == null) return;
        try
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(com))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(com);
        }
        catch { /* 已经释放过 */ }
    }
}
