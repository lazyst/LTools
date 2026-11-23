using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Features;

/// <summary>
/// 面板「复制引用 / 复制为副本 / 粘贴」冒烟：临时数据根 + 反射驱动 internal 类型，
/// 断言分组里的引用、动作清单、副本的 <c>files/</c> 与菜单文案。
/// </summary>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static readonly Assembly Lt = typeof(PanelConfig).Assembly;

    private static int _pass, _fail;
    private static Type _ops = null!, _clip = null!, _pasteMenu = null!, _registry = null!, _panel = null!;

    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-panel-clip-" + Guid.NewGuid().ToString("N")[..8]);
        string configPath = Path.Combine(root, "configs", "LTools.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        var configStore = Lt.GetType("LTools.Features.ConfigStore", true)!;
        var actionStore = Lt.GetType("LTools.Core.Storage.ActionStore", true)!;
        _panel = Lt.GetType("LTools.Features.Panel.PanelLibrary", true)!;
        _ops = Lt.GetType("LTools.Features.Panel.PanelActionOps", true)!;
        _clip = Lt.GetType("LTools.Features.Panel.ActionClipboard", true)!;
        _pasteMenu = Lt.GetType("LTools.Views.Controls.PanelPasteMenu", true)!;
        _registry = Lt.GetType("LTools.Features.ActionRegistry", true)!;

        string oldConfig = (string)configStore.GetProperty("ConfigPath")!.GetValue(null)!;
        string oldAnchor = (string)actionStore.GetProperty("Anchor")!.GetValue(null)!;

        try
        {
            SetStatic(configStore, "ConfigPath", configPath);
            SetStatic(actionStore, "Anchor", configPath);
            Call(_panel, "Initialize", configPath);

            Run(root, configPath);
        }
        catch (Exception ex)
        {
            Check("未抛异常", false, ex.ToString());
        }
        finally
        {
            SetStatic(configStore, "ConfigPath", oldConfig);
            SetStatic(actionStore, "Anchor", oldAnchor);
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run(string root, string configPath)
    {
        // ---- 夹具：一个全局场景、两个分组 ----
        var panel = (PanelConfig)Get(_panel, "Current")!;
        if (panel.Global.Scenes.Count == 0)
            panel.Global.Scenes.Add(new PanelSceneDto { Id = NewId(), Name = "全局" });
        var scene = panel.Global.Scenes[0];
        var a = Group(scene, "A");
        var b = Group(scene, "B");

        // 源动作 + 它自己的模板图
        string src = NewAction("复制源");
        string srcFiles = Path.Combine(root, "configs", "actions", src, "files");
        Directory.CreateDirectory(srcFiles);
        File.WriteAllBytes(Path.Combine(srcFiles, "tpl.png"), new byte[] { 0x89, 0x50, 1, 2, 3 });

        // ---- ① 复制引用 → 粘到另一个分组：同一个动作、不新建 ----
        Call(_clip, "CopyReference", src);
        Check("粘贴（引用）返回 true", (bool)Call(_ops, "Paste", b, -1)!);
        Check("粘的是同一个动作 Id", b.Actions.Count == 1 && b.Actions[0] == src,
            $"B=[{string.Join(",", b.Actions)}]");
        Check("引用不新建动作", Actions().Count == 1, $"动作数={Actions().Count}");

        // ---- ② 剪贴板为空：什么都不粘 ----
        Call(_clip, "Clear");
        Check("剪贴板空时不粘", !(bool)Call(_ops, "Paste", b, -1)! && b.Actions.Count == 1);

        // ---- ③ 复制为副本 → 粘到指定位置：新动作 + files/ ----
        Call(_clip, "CopyCopy", src);
        Check("粘贴（副本）返回 true", (bool)Call(_ops, "Paste", a, 0)!);
        Check("副本插到第 0 位", a.Actions.Count == 1 && a.Actions[0] != src,
            $"A=[{string.Join(",", a.Actions)}]");

        var all = Actions();
        Check("副本进了动作清单", all.Count == 2, $"动作数={all.Count}");
        var copy = all.FirstOrDefault(x => x.Id != src);
        Check("副本换了新 Id", copy != null);
        if (copy != null)
        {
            Check("副本名字加「 副本」", copy.Name == "复制源 副本", $"实际「{copy.Name}」");
            Check("副本带自己的模板图",
                File.Exists(Path.Combine(root, "configs", "actions", copy.Id, "files", "tpl.png")));
            Check("副本落盘 action.json",
                File.Exists(Path.Combine(root, "configs", "actions", copy.Id, "action.json")));
        }

        // ---- ④ 跨场景：引用粘到上下文场景的分组 ----
        var ctxScene = new PanelSceneDto { Id = NewId(), Name = "浏览器" };
        var c = Group(ctxScene, "C");
        panel.Context.Scenes.Add(ctxScene);
        Call(_clip, "CopyReference", src);
        Check("粘到上下文场景返回 true", (bool)Call(_ops, "Paste", c, -1)!);
        Check("上下文分组拿到引用", c.Actions.Count == 1 && c.Actions[0] == src);

        // ---- ⑤ 菜单：空剪贴板不挂「粘贴」，有内容时挂标题 + 「粘贴到…」树 ----
        Call(_clip, "Clear");
        var empty = new ContextMenu();
        Check("空剪贴板不挂粘贴项",
            !(bool)Call(_pasteMenu, "Append", empty, b, Noop, -1)! && empty.Items.Count == 0);

        Call(_clip, "CopyReference", src);
        var menu = new ContextMenu();
        Check("有内容时挂上粘贴项", (bool)Call(_pasteMenu, "Append", menu, b, Noop, -1)!);
        var here = menu.Items.OfType<MenuItem>().FirstOrDefault();
        Check("粘贴项标题带动作名", here?.Header as string == "粘贴「复制源」", $"实际「{here?.Header}」");

        var to = menu.Items.OfType<MenuItem>().LastOrDefault();
        Check("有「粘贴到…」", to?.Header as string == "粘贴到…", $"实际「{to?.Header}」");
        if (to != null)
        {
            var heads = Descendants(to).Select(m => m.Header as string).ToList();
            Check("「粘贴到…」里能选到别的分组", heads.Contains("B") && heads.Contains("A"),
                string.Join(" / ", heads));
            Check("「粘贴到…」里有上下文场景", heads.Contains("浏览器"), string.Join(" / ", heads));
        }

        // ---- ⑥ 源被删：粘不了，剪贴板清空 ----
        Call(_clip, "CopyReference", src);
        _registry.GetMethod("Remove", Any)!.Invoke(null, new object[] { src });
        Check("源被删后粘不了", !(bool)Call(_ops, "Paste", b, -1)!);
        Check("源被删后剪贴板清空", !(bool)Get(_clip, "Has")!);

        // ---- ⑦ 从文件 / 文件夹建动作：落进分组、落盘、Id 对齐 ----
        var drop = Lt.GetType("LTools.Features.Panel.PanelDrop", true)!;
        int before = a.Actions.Count;

        var folderAction = new ActionDto
        {
            Name = "项目A",
            Icon = "folder",
            Program = new ActionProgram { Name = "项目A", Icon = "folder", Description = @"C:\tmp\项目A" },
        };
        folderAction.Program.Steps.Add(new Step
        {
            Unit = "file.explorer",
            In = { ["operation"] = StepArg.FromText("open"), ["path"] = StepArg.FromText(@"C:\tmp\项目A") },
        });

        var fileAction = new ActionDto
        {
            Name = "报表",
            Icon = "file",
            Program = new ActionProgram { Name = "报表", Icon = "file", Description = @"C:\tmp\报表.xlsx" },
        };
        fileAction.Program.Steps.Add(new Step
        {
            Unit = "file.openFile",
            In = { ["path"] = StepArg.FromText(@"C:\tmp\报表.xlsx"), ["workdir"] = StepArg.FromText(@"C:\tmp") },
        });

        int added = (int)Call(drop, "Insert", new List<ActionDto> { folderAction, fileAction }, a, before)!;
        Check("Insert 返回真正加进去的条数", added == 2, $"added={added}");
        Check("两条都插进分组末尾", a.Actions.Count == before + 2
            && a.Actions[before] == folderAction.Id && a.Actions[before + 1] == fileAction.Id,
            $"A=[{string.Join(",", a.Actions)}]");
        Check("动作体 Id 与外层对齐", fileAction.Program!.Id == fileAction.Id && folderAction.Program!.Id == folderAction.Id);
        Check("文件动作落盘 action.json",
            File.Exists(Path.Combine(root, "configs", "actions", fileAction.Id, "action.json")));
        Check("文件夹动作落盘 action.json",
            File.Exists(Path.Combine(root, "configs", "actions", folderAction.Id, "action.json")));

        string fileJson = File.ReadAllText(Path.Combine(root, "configs", "actions", fileAction.Id, "action.json"));
        Check("落盘内容带的是一步「打开文件」", fileJson.Contains("file.openFile") && fileJson.Contains("报表.xlsx"));
    }

    // ============ 夹具 ============

    private static PanelGroupDto Group(PanelSceneDto scene, string name)
    {
        var group = new PanelGroupDto { Id = NewId(), Name = name };
        scene.Groups.Add(group);
        return group;
    }

    private static string NewAction(string name)
    {
        var dto = new ActionDto { Id = NewId(), Name = name, Icon = "app" };
        _registry.GetMethod("Register", Any)!.Invoke(null, new object[] { dto });
        return dto.Id;
    }

    private static List<ActionDto> Actions() =>
        ((IReadOnlyCollection<ActionDto>)_registry.GetProperty("All", Any)!.GetValue(null)!).ToList();

    private static string NewId() =>
        (string)_registry.GetMethod("NextId", Any)!.Invoke(null, null)!;

    private static readonly Action<PanelGroupDto, int> Noop = (_, _) => { };

    private static IEnumerable<MenuItem> Descendants(ItemsControl root)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in Descendants(item)) yield return child;
        }
    }

    // ============ 反射小工具 ============

    private static object? Call(Type type, string name, params object?[] args) =>
        Find(type, name, args.Length).Invoke(null, args);

    private static object? Get(Type type, string name) =>
        type.GetProperty(name, Any)!.GetValue(null);

    private static void SetStatic(Type type, string name, object? value)
    {
        var prop = type.GetProperty(name, Any)!;
        (prop.GetSetMethod(true) ?? throw new InvalidOperationException(name)).Invoke(null, new[] { value });
    }

    private static MethodInfo Find(Type type, string name, int count) =>
        type.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count)
        ?? throw new MissingMethodException(type.Name + "." + name + "/" + count);

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? "  [" + detail + "]" : ""));
        if (ok) _pass++; else _fail++;
    }
}
