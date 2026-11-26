using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 全局快捷键（P7-c，照 Snow Shot 的 <c>GlobalShortcutManager</c> + 设置里的「全局快捷键」页）：
/// 首次启动播种、真按键命中、设置对话框（录制 / 清空 / 恢复默认 / 冲突）、暂停总开关，
/// 以及三个新的「选完就做」出口（识别并复制 / 识别并翻译 / 长截图）。
/// </summary>
/// <remarks>
/// 配置写进临时目录（跑完还原），别动开发机的 <c>configs/hotkeys.json</c>。
/// 明细规则在 <c>--selftest</c> 的「全局快捷键」用例里，这里只验真跑一遍。
/// </remarks>
internal static partial class Program
{
    private static void ShortcutsSection()
    {
        Console.WriteLine("---- 全局快捷键（P7-c）----");

        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var shortcutsType = asm.GetType("LTools.Features.CapturePro.CaptureShortcuts")!;
        var registryType = asm.GetType("LTools.Core.HotkeyRegistry")!;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var appConfigType = asm.GetType("LTools.Config.AppConfig")!;
        var seedingType = asm.GetType("LTools.Features.HotkeySeeding")!;
        var captureType = asm.GetType("LTools.Features.HotkeyCapture")!;

        // 配置写进临时目录：别动开发机的 configs/hotkeys.json
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "shortcuts-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string anchor = Path.Combine(configRoot, "LTools.json");
        configPathSet.Invoke(null, new object[] { anchor });

        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { System.Windows.Threading.Dispatcher.CurrentDispatcher });

        Window dialog = null;
        try
        {
            // ---------- 1) 首次启动播种：「截图 Pro」组出现四条默认值 ----------
            object fresh = Activator.CreateInstance(appConfigType)!;
            seedingType.GetMethod("Ensure", Stat)!.Invoke(null, new object[] { anchor, fresh });

            object reloaded = appConfigType.GetMethod("Load", Stat)!.Invoke(null, new object[] { anchor })!;
            object file = reloaded.GetType().GetProperty("Hotkeys")!.GetValue(reloaded)!;
            var groups = ((IEnumerable)file.GetType().GetProperty("Groups")!.GetValue(file)!)
                .Cast<object>().ToList();

            object group = groups.FirstOrDefault(g =>
                (string)g.GetType().GetProperty("Name")!.GetValue(g)! == "截图 Pro");
            Check(group != null, "首次启动播种出「截图 Pro」组");
            if (group == null) return;

            var bindings = ((IEnumerable)group.GetType().GetProperty("Bindings")!.GetValue(group)!)
                .Cast<object>().ToList();
            Check(bindings.Count == 4, $"默认四条绑定（实际 {bindings.Count}）");

            int seedVersion = (int)file.GetType().GetProperty("CaptureSeedVersion")!.GetValue(file)!;
            Check(seedVersion == 1, "播种版本记到 1（删掉的条目不会因重启回来）");

            var refs = bindings
                .Select(b => (string)b.GetType().GetProperty("ActionId")!.GetValue(b)!)
                .ToList();
            Check(refs.Contains("common.capture") && refs.Contains("ltools.captureCopy") &&
                  refs.Contains("common.pinClipboard") && refs.Contains("ltools.pinRestoreLast"),
                "四条默认值就是截图 / 截图并复制 / 贴剪贴板 / 恢复上一张");

            string Hit(ushort vk)
            {
                registryType.GetMethod("ClearLatches", Stat)!.Invoke(null, null);
                return registryType.GetMethod("TryFire", Stat)!.Invoke(null, new object[] { vk }) as string;
            }

            Check(Hit(0x70) == "common.capture", "真按键 F1 命中「截图」");

            registryType.GetProperty("GlobalEnabled")!.SetValue(null, false);
            Check(Hit(0x70) == null, "暂停全局快捷键之后 F1 不再触发");
            registryType.GetProperty("GlobalEnabled")!.SetValue(null, true);
            Check(Hit(0x70) == "common.capture", "恢复之后 F1 又能触发");

            // ---------- 2) 设置对话框：行数 / 录制 / 写盘 / 冲突 / 恢复默认 / 清空 ----------
            var dialogType = asm.GetType("LTools.Views.CaptureShortcutsWindow")!;
            dialog = (Window)dialogType.GetConstructor(Type.EmptyTypes)!.Invoke(null);
            dialog.Show();
            Pump(dialog);

            var all = ((IEnumerable)shortcutsType.GetField("All", Stat)!.GetValue(null)!)
                .Cast<object>().ToList();
            var rows = (IList)Field(dialog, "_rows")!;
            Check(rows.Count == all.Count, $"对话框一行一个动作（{rows.Count} / {all.Count}）");

            string ActionIdOf(object row)
            {
                object action = row.GetType().GetProperty("Action")!.GetValue(row)!;
                return (string)action.GetType().GetProperty("Id")!.GetValue(action)!;
            }

            object ActionOf(object row) => row.GetType().GetProperty("Action")!.GetValue(row)!;

            object RowOf(string id) => rows.Cast<object>().First(r => ActionIdOf(r) == id);

            TextBox ChipOf(object row, string name) =>
                (TextBox)row.GetType().GetProperty(name)!.GetValue(row)!;

            FrameworkElement StatusOf(object row) =>
                (FrameworkElement)row.GetType().GetProperty("Status")!.GetValue(row)!;

            var setHotkey = captureType.GetMethod("SetHotkey", Stat)!;
            var chordsOf = shortcutsType.GetMethod("ChordsOf", Stat)!;
            var commit = dialogType.GetMethod("CommitRow", Private)!;
            var clearRow = dialogType.GetMethod("ClearRow", Private)!;
            var restore = dialogType.GetMethod("Restore_Click", Private)!;
            object dialogFile = Field(dialog, "_file")!;

            List<string> ChordsOf(object action) =>
                ((IEnumerable)chordsOf.Invoke(null, new[] { dialogFile, action })!).Cast<string>().ToList();

            var shotRow = RowOf("screenshot");
            var shotAction = ActionOf(shotRow);
            Check(ChipOf(shotRow, "First").Text.Contains("F1", StringComparison.OrdinalIgnoreCase),
                "「截图」第一个格子显示 F1（实际 " + ChipOf(shotRow, "First").Text + "）");
            Check(ChipOf(shotRow, "Second").Text.Length > 0, "第二个格子是「＋ 添加」占位");

            // 改绑成 Ctrl+F9：真按键不再命中，写盘里能读回来
            setHotkey.Invoke(null, new object[] { ChipOf(shotRow, "First"), "ctrl+f9" });
            commit.Invoke(dialog, new[] { shotRow });
            Pump(dialog);

            Check(ChordsOf(shotAction).SequenceEqual(new[] { "ctrl+f9" }), "改绑写进内存表（只剩 Ctrl+F9）");
            string hotkeysJson = File.ReadAllText(Path.Combine(configRoot, "configs", "hotkeys.json"));
            Check(hotkeysJson.Contains("ctrl+f9", StringComparison.Ordinal),
                "改绑写进 configs/hotkeys.json");
            Check(Hit(0x70) == null, "改绑之后 F1 不再触发截图");

            // 撞键 = 两行都标「冲突」
            setHotkey.Invoke(null, new object[] { ChipOf(shotRow, "First"), "f3" });
            commit.Invoke(dialog, new[] { shotRow });
            Pump(dialog);
            var pinRow = RowOf("pin_clipboard_content");
            Check(StatusOf(shotRow).Visibility == Visibility.Visible &&
                  StatusOf(pinRow).Visibility == Visibility.Visible,
                "和「贴剪贴板」撞了 F3 → 两行都标冲突");

            // 恢复默认：冲突消失，F1 回来
            restore.Invoke(dialog, new object[] { null!, new RoutedEventArgs() });
            Pump(dialog);
            Check(StatusOf(shotRow).Visibility != Visibility.Visible, "恢复默认后不再是冲突");
            Check(ChordsOf(shotAction).SequenceEqual(new[] { "f1" }), "恢复默认后「截图」回到 F1");
            Check(Hit(0x70) == "common.capture", "恢复默认后 F1 又能触发");

            // 清空一行
            clearRow.Invoke(dialog, new[] { shotRow });
            Pump(dialog);
            Check(ChordsOf(shotAction).Count == 0, "清空按钮把这一行的快捷键删干净");
            Check(Hit(0x70) == null, "清空之后 F1 不触发");
            Check(ChipOf(shotRow, "First").Text == "＋ 添加", "清空后回到「＋ 添加」占位");

            // 收尾：恢复默认，给后面的段落留一份正常配置
            restore.Invoke(dialog, new object[] { null!, new RoutedEventArgs() });
            Pump(dialog);

            // 暂停总开关（对话框顶部那个勾选）真能翻运行期状态
            var enableBox = (CheckBox)Field(dialog, "EnableBox")!;
            enableBox.IsChecked = false;
            Pump(dialog);
            Check(!(bool)registryType.GetProperty("GlobalEnabled")!.GetValue(null)!,
                "取消「启用全局快捷键」= 暂停运行期快捷键");
            enableBox.IsChecked = true;
            Pump(dialog);
            Check((bool)registryType.GetProperty("GlobalEnabled")!.GetValue(null)!,
                "勾回来 = 恢复");

            // ---------- 3) 三个新的「选完就做」出口 ----------
            var pendingType = typeof(CaptureOverlayWindow).GetNestedType("PendingAction", BindingFlags.Public)!;
            object Pending(string name) => Enum.Parse(pendingType, name);

            var monitor = new System.Drawing.Rectangle(0, 0, 400, 300);
            var shot = BuildShot(400, 300);
            var ctor = typeof(CaptureOverlayWindow)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            CaptureOverlayWindow Make(object pending)
            {
                // 可选参数按名字喂：构造函数加新开关（selectionOnly）也不用改这里
                var parameters = ctor.GetParameters();
                var args = new object?[parameters.Length];
                args[0] = shot;
                args[1] = monitor;
                args[2] = 1.0;
                args[3] = 1.0;
                for (int i = 4; i < parameters.Length; i++)
                    args[i] = parameters[i].Name == "pending" ? pending : parameters[i].DefaultValue;

                var overlay = (CaptureOverlayWindow)ctor.Invoke(args);
                var smart = Field(overlay, "_smart");
                var modeType = asm.GetType("LTools.Features.CapturePro.SmartSelectionMode")!;
                smart.GetType().GetMethod("BeginSession")!
                    .Invoke(smart, new[] { Enum.Parse(modeType, "Disabled") });
                return overlay;
            }

            bool Flag(CaptureOverlayWindow w, string name) =>
                (bool)typeof(CaptureOverlayWindow).GetProperty(name)!.GetValue(w)!;
            string FinishOf(CaptureOverlayWindow w) =>
                typeof(CaptureOverlayWindow).GetProperty("Finish", Private)!.GetValue(w)!.ToString()!;
            bool Confirmed(CaptureOverlayWindow w) =>
                (bool)typeof(CaptureOverlayWindow).GetProperty("Confirmed")!.GetValue(w)!;
            string DefaultFinishOf(CaptureOverlayWindow w) =>
                Member(w, "DefaultFinish")!.ToString()!;

            // 识别并复制：结果窗照开，算完自动复制
            var recognizeCopy = Make(Pending("RecognizeCopy"));
            recognizeCopy.Show();
            Pump(recognizeCopy);
            Check(DefaultFinishOf(recognizeCopy) == "RecognizeCopy", "「截图并识别复制」的默认出口");
            SetSelection(recognizeCopy, new Rect(40, 30, 180, 120));
            Pump(recognizeCopy);
            Invoke(recognizeCopy, "FirePendingAction");
            Pump(recognizeCopy);
            Check(Flag(recognizeCopy, "RecognizeRequested") &&
                  Flag(recognizeCopy, "RecognizeCopyRequested"),
                "识别并复制：两个请求都带出去");
            Check(FinishOf(recognizeCopy) == "RecognizeCopy", "识别并复制：完成动作记为识别复制");
            Check(!recognizeCopy.IsVisible, "识别并复制：覆盖层关掉（结果窗接着开）");

            // 识别并翻译：同一个结果窗，算完自动翻一遍
            var translate = Make(Pending("Translate"));
            translate.Show();
            Pump(translate);
            Check(DefaultFinishOf(translate) == "Recognize", "「截图并翻译」先走识别出口");
            SetSelection(translate, new Rect(40, 30, 180, 120));
            Pump(translate);
            Invoke(translate, "FirePendingAction");
            Pump(translate);
            Check(Flag(translate, "RecognizeRequested") && Flag(translate, "TranslateRequested"),
                "识别并翻译：识别 + 翻译两个请求都带出去");
            Check(FinishOf(translate) == "Recognize", "识别并翻译：完成动作记为识别");
            Check(!translate.IsVisible, "识别并翻译：覆盖层关掉");

            // 长截图：框完直接进长截图模式（找不到可滚的窗口就退回普通框选）
            var scrolling = Make(Pending("Scrolling"));
            scrolling.Show();
            Pump(scrolling);
            Check(DefaultFinishOf(scrolling) == "Copy", "「截图并长截图」不是复制出口");
            SetSelection(scrolling, new Rect(40, 30, 200, 140));
            Pump(scrolling);
            Invoke(scrolling, "FirePendingAction");
            Pump(scrolling);

            bool scrollMode = (bool)Field(scrolling, "_scrollMode")!;
            var scrollToolbar = Field(scrolling, "Toolbar") as FrameworkElement;
            Console.WriteLine(scrollMode
                ? "info 长截图：进了长截图模式"
                : "info 长截图：没有可滚窗口，已退回普通框选");
            Check(scrolling.IsVisible && !Confirmed(scrolling),
                "长截图：没有当成一次普通截图结束");
            Check(scrollMode || scrollToolbar is { Visibility: Visibility.Visible },
                "长截图：要么进了长截图模式，要么工具条退回来");
            Check(!Flag(scrolling, "ScrollRequested"), "长截图：还没出结果");

            if (scrollMode) InvokeWith(scrolling, "ExitScrollMode", new object[] { null! });
            Pump(scrolling);
            scrolling.Close();
            Pump(scrolling);
        }
        finally
        {
            if (dialog != null)
            {
                dialog.Close();
                Pump(dialog);
            }

            registryType.GetProperty("GlobalEnabled")!.SetValue(null, true);
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}

