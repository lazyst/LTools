using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using LTools.Config;
using LTools.Features;

namespace PanelHostSmoke;

/// <summary>
/// 普通权限宿主的「只读初始化」冒烟：读得到数据、初始化本身**一个字节都不写**；
/// 动作清单仍归主程序（<c>ConfigStore.Save</c> 被拒），面板结构归宿主（<c>PanelLibrary.Save</c> 直接落盘）。
/// </summary>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static readonly Assembly Lt = typeof(PanelConfig).Assembly;

    private static int _pass, _fail;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string root = Path.Combine(Path.GetTempPath(), "ltools-host-" + Guid.NewGuid().ToString("N")[..8]);
        string configPath = Path.Combine(root, "LTools.json");
        string emptyRoot = Path.Combine(Path.GetTempPath(), "ltools-host-empty-" + Guid.NewGuid().ToString("N")[..8]);

        var configStore = Lt.GetType("LTools.Features.ConfigStore", true)!;
        var panelLibrary = Lt.GetType("LTools.Features.Panel.PanelLibrary", true)!;
        var registry = Lt.GetType("LTools.Features.ActionRegistry", true)!;
        var hotkeys = Lt.GetType("LTools.Core.HotkeyRegistry", true)!;
        var appRole = Lt.GetType("LTools.Core.AppRole", true)!;

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "configs"));

            // ---- ① 按主程序的方式建一份数据根（会写盘） ----
            Call(configStore, "Initialize", configPath);

            var action = new ActionDto { Name = "宿主冒烟动作", Icon = "app" };
            Check("主程序路径能加动作", (bool)Call(configStore, "AddAction", action)!, action.Id);
            Call(configStore, "Save");
            Check("动作落盘", File.Exists(Path.Combine(root, "actions", action.Id, "action.json")));

            var panel = (PanelConfig)Get(panelLibrary, "Current")!;
            if (panel.Global.Scenes.Count == 0) panel.Global.Scenes.Add(new PanelSceneDto { Id = "s1", Name = "全局" });
            var group = panel.Global.Scenes[0].Groups.FirstOrDefault();
            if (group == null)
            {
                group = new PanelGroupDto { Id = "g1", Name = "常用" };
                panel.Global.Scenes[0].Groups.Add(group);
            }

            group.Actions.Add(action.Id);
            Call(panelLibrary, "Save");
            Check("面板配置落盘", File.Exists(Path.Combine(root, "configs", "panel.json")));

            // ---- ② 换成宿主身份（窗口 / 交互归宿主）→ 只读初始化 → 快照 ----
            Call(appRole, "MarkUiHost");
            Dictionary<string, string> before = Snapshot(root);
            Call(configStore, "InitializeReadOnly", configPath);
            Dictionary<string, string> after = Snapshot(root);

            Check("只读初始化没写任何文件", Same(before, after), Diff(before, after));
            Check("只读标记已置位", (bool)Get(configStore, "ReadOnly")!);
            Check("动作读得到", Actions(registry).Any(a => a.Id == action.Id && a.Name == "宿主冒烟动作"), Actions(registry).Count.ToString());
            Check("面板读得到", GroupOf(panelLibrary)?.Actions.Contains(action.Id) == true);

            var file = (HotkeyFile)hotkeys.GetProperty("File", Any)!.GetValue(null)!;
            Check("快捷键读得到（播种过的组还在）", file.Groups.Count > 0, file.Groups.Count.ToString());

            // ---- ③ 只读进程：动作清单仍拒绝写；面板结构归宿主（它自己就是 configs/ 的写者）----
            before = Snapshot(root);
            Call(configStore, "Save");
            after = Snapshot(root);

            Check("动作清单 Save 被拒绝：一个字节都没写", Same(before, after), Diff(before, after));

            // 面板结构从 M2-a 起就是宿主在写（以前要交回提权进程，`panel.replace` 已删）：
            // 改一个分组名 → Save 应该真的落到 configs/panel.json，而且只动这一个文件。
            var hostGroup = GroupOf(panelLibrary)!;
            hostGroup.Name = "宿主改的名";
            before = Snapshot(root);
            bool panelSaved = (bool)Call(panelLibrary, "Save")!;
            after = Snapshot(root);

            Check("面板 Save 成功（宿主就是写者）", panelSaved);
            Check("面板结构写进了 panel.json",
                File.ReadAllText(Path.Combine(root, "configs", "panel.json")).Contains("宿主改的名"));
            Check("面板保存只动了 panel.json", Diff(before, after) == "改了 configs\\panel.json", Diff(before, after));

            // ---- ④ 空数据根：只读初始化不生成默认面板 ----
            Directory.CreateDirectory(emptyRoot);
            Call(configStore, "InitializeReadOnly", Path.Combine(emptyRoot, "LTools.json"));
            Check("空数据根不写默认面板", Directory.GetFiles(emptyRoot, "*", SearchOption.AllDirectories).Length == 0,
                string.Join(",", Directory.GetFiles(emptyRoot, "*", SearchOption.AllDirectories)));
        }
        catch (Exception ex)
        {
            Check("冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            TryDelete(root);
            TryDelete(emptyRoot);
        }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 反射小工具 ============

    private static object? Call(Type type, string name, params object?[] args) =>
        Find(type, name, args.Length).Invoke(null, args);

    private static object? Get(Type type, string name) => type.GetProperty(name, Any)!.GetValue(null);

    private static MethodInfo Find(Type type, string name, int count) =>
        type.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count)
        ?? throw new MissingMethodException(type.Name + "." + name + "/" + count);

    private static List<ActionDto> Actions(Type registry) =>
        ((IReadOnlyCollection<ActionDto>)registry.GetProperty("All", Any)!.GetValue(null)!).ToList();

    private static PanelGroupDto? GroupOf(Type panelLibrary)
    {
        var panel = (PanelConfig)Get(panelLibrary, "Current")!;
        return panel.Global.Scenes.SelectMany(s => s.Groups).FirstOrDefault();
    }

    // ============ 目录快照 ============

    private static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return map;

        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(file);
            map[Path.GetRelativePath(root, file)] =
                $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}:{File.GetLastWriteTimeUtc(file).Ticks}";
        }

        return map;
    }

    private static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out string? v) && v == pair.Value);

    private static string Diff(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var parts = new List<string>();
        foreach (var pair in a)
        {
            if (!b.TryGetValue(pair.Key, out string? value)) parts.Add("少了 " + pair.Key);
            else if (value != pair.Value) parts.Add("改了 " + pair.Key);
        }

        foreach (string key in b.Keys.Where(k => !a.ContainsKey(k))) parts.Add("多了 " + key);

        return parts.Count == 0 ? "" : string.Join(" / ", parts);
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* 临时目录 */ }
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
    }
}
