using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LTools.Features;

/// <summary>
/// 键鼠宏「回放内容」的端到端冒烟：真注入 + 真键盘钩子 + 真光标。
/// </summary>
/// <remarks>
/// <para>
/// 只测「过滤到底有没有生效」，不测 UI：一段宏里同时有按键与鼠标移动，
/// 分别用「全部 / 仅键盘 / 仅鼠标」放一遍，看注入侧真的发生了什么——
/// 键盘侧靠本进程装的 <c>WH_KEYBOARD_LL</c> 数注入事件（钩子能看到 <c>LLKHF_INJECTED</c> 的事件），
/// 鼠标侧靠 <c>GetCursorPos</c>。
/// </para>
/// <para>
/// 安全：只注入 <c>F24</c>（几乎没人绑定的功能键）与「移动到某点」，<b>不点击、不滚轮</b>，
/// 结束时把光标放回原处；产品自己的钩子会忽略注入事件，所以不会触发 CapsLock / 面板手势。
/// </para>
/// <para>
/// 它用 <c>HintPath</c> 引用 <c>bin\Debug</c> 的 LTools.dll（与 MenuConeSmoke 一套）；先构建产品再跑。
/// </para>
/// </remarks>
internal static class Program
{
    private const ushort F24 = 0x87;

    private static int _failed;
    private static MethodInfo _playSync = null!;
    private static MethodInfo _playerStop = null!;
    private static PropertyInfo _playingProp = null!;
    private static Type _playbackType = null!;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Assembly asm = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "LTools.dll"));
        _playbackType = asm.GetType("LTools.Features.MacroStore+MacroPlayback", throwOnError: true)!;
        Type player = asm.GetType("LTools.Features.MacroPlayer", throwOnError: true)!;
        _playSync = player
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "PlaySync" && m.GetParameters().Length == 2);
        _playerStop = player.GetMethod("Stop", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        _playingProp = player.GetProperty("IsPlaying",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

        // 真键盘钩子：注入的键也要数到，所以这里不过滤 LLKHF_INJECTED
        var hookThread = new Thread(HookThread) { IsBackground = true, Name = "KeyHook" };
        hookThread.Start();
        if (!_ready.Wait(3000)) Check(false, "键盘钩子装上（3 秒内）");

        GetCursorPos(out POINT origin);
        (int targetX, int targetY) = OffsetPoint(origin, 40, 30);

        var macro = new MacroDto
        {
            Name = "回放内容冒烟",
            DurationMs = 160,
            Events =
            {
                new MacroEvent { TimeMs = 0, Kind = MacroEventKind.KeyDown, Vk = F24 },
                new MacroEvent { TimeMs = 40, Kind = MacroEventKind.MouseMove, X = targetX, Y = targetY },
                new MacroEvent { TimeMs = 80, Kind = MacroEventKind.KeyUp, Vk = F24 },
            },
        };

        try
        {
            // —— 仅键盘：鼠标事件不注入，按键照旧 ——
            SetCursorPos(origin.X, origin.Y);
            Thread.Sleep(150);
            ClearSeen();
            Play(macro, MacroPlaybackContent.Keyboard, "仅键盘");
            Thread.Sleep(200);
            GetCursorPos(out POINT afterKeys);
            Check(afterKeys.X == origin.X && afterKeys.Y == origin.Y,
                $"仅键盘：鼠标事件没有被注入（光标 {origin.X},{origin.Y} → {afterKeys.X},{afterKeys.Y}）");
            Check(SeenCount(F24) >= 2, $"仅键盘：按键照旧注入（收到 {SeenCount(F24)} 个 F24）");

            // —— 仅鼠标：鼠标照旧，按键一条都不注入 ——
            SetCursorPos(origin.X, origin.Y);
            Thread.Sleep(150);
            ClearSeen();
            Play(macro, MacroPlaybackContent.Mouse, "仅鼠标");
            Thread.Sleep(200);
            GetCursorPos(out POINT afterMouse);
            Check(Near(afterMouse, targetX, targetY),
                $"仅鼠标：鼠标移动照旧注入（期望 {targetX},{targetY}，实际 {afterMouse.X},{afterMouse.Y}）");
            Check(SeenCount(F24) == 0, $"仅鼠标：按键没有被注入（收到 {SeenCount(F24)} 个 F24）");

            // —— 全部：两边都放 ——
            SetCursorPos(origin.X, origin.Y);
            Thread.Sleep(150);
            ClearSeen();
            Play(macro, MacroPlaybackContent.All, "全部");
            Thread.Sleep(200);
            GetCursorPos(out POINT afterAll);
            Check(Near(afterAll, targetX, targetY),
                $"全部：鼠标照放（期望 {targetX},{targetY}，实际 {afterAll.X},{afterAll.Y}）");
            Check(SeenCount(F24) >= 2, $"全部：按键照放（收到 {SeenCount(F24)} 个 F24）");

            // —— 这一类一条都没有：必须明确失败，不假装放完了 ——
            var keysOnly = new MacroDto
            {
                Name = "只有键盘",
                DurationMs = 60,
                Events =
                {
                    new MacroEvent { TimeMs = 0, Kind = MacroEventKind.KeyDown, Vk = F24 },
                    new MacroEvent { TimeMs = 40, Kind = MacroEventKind.KeyUp, Vk = F24 },
                },
            };
            string? error = TryPlay(keysOnly, MacroPlaybackContent.Mouse);
            Check(error?.Contains("没有鼠标事件", StringComparison.Ordinal) == true,
                $"只有键盘的宏选「仅鼠标」要明确失败：{error ?? "<没有抛>"}");

            var mouseOnly = new MacroDto
            {
                Name = "只有鼠标",
                DurationMs = 60,
                Events = { new MacroEvent { TimeMs = 0, Kind = MacroEventKind.MouseMove, X = targetX, Y = targetY } },
            };
            error = TryPlay(mouseOnly, MacroPlaybackContent.Keyboard);
            Check(error?.Contains("没有键盘事件", StringComparison.Ordinal) == true,
                $"只有鼠标的宏选「仅键盘」要明确失败：{error ?? "<没有抛>"}");

            // 失败之后回放器要能继续用（_playing 已复位）
            ClearSeen();
            Play(macro, MacroPlaybackContent.Keyboard, "失败后重放");
            Thread.Sleep(150);
            Check(SeenCount(F24) >= 2, "上一次「没有内容」失败后还能正常回放（_playing 已复位）");

            // —— 无限循环（-1）：一直放，按停止能停 ——
            SetCursorPos(origin.X, origin.Y);
            Thread.Sleep(120);
            ClearSeen();
            var infinite = Task.Run(() =>
            {
                try
                {
                    _playSync.Invoke(null, new object[] { macro, Playback(MacroPlaybackContent.Keyboard, -1) });
                }
                catch (TargetInvocationException)
                {
                    // 被 Stop 打断是正常的收尾路径
                }
            });
            Thread.Sleep(700);
            Check(SeenCount(F24) >= 4, $"无限循环：700ms 里放了两轮以上（收到 {SeenCount(F24)} 个 F24）");
            Check(IsPlaying(), "无限循环：700ms 后还在放");
            _playerStop.Invoke(null, null);
            Check(infinite.Wait(3000), "无限循环：按停止后回放线程退出");
            Check(!IsPlaying(), "无限循环：已经停了");

            ConfigRoundTrip(asm);
            SettingsPage(asm);
        }
        finally
        {
            SetCursorPos(origin.X, origin.Y);
            StopHook();
        }

        Console.WriteLine(_failed == 0 ? "macroplayback-only：通过" : $"macroplayback-only：{_failed} 项失败");
        return _failed == 0 ? 0 : 1;
    }

    // ============ 回放 ============

    private static object Playback(MacroPlaybackContent content, int loops = 1)
        => Activator.CreateInstance(_playbackType, 1.0, loops, 0, content)!;

    private static bool IsPlaying() => (bool)_playingProp.GetValue(null)!;

    private static void Play(MacroDto macro, MacroPlaybackContent content, string what)
    {
        try
        {
            _playSync.Invoke(null, new object[] { macro, Playback(content) });
        }
        catch (TargetInvocationException ex)
        {
            Check(false, $"{what}：回放本身不该失败（{ex.InnerException?.Message}）");
        }
    }

    private static string? TryPlay(MacroDto macro, MacroPlaybackContent content)
    {
        try
        {
            _playSync.Invoke(null, new object[] { macro, Playback(content) });
        }
        catch (TargetInvocationException ex)
        {
            return ex.InnerException?.Message;
        }
        return null;
    }

    // ============ 配置往返 ============

    /// <summary>
    /// 全局默认（<c>settings.json</c> 的 <c>macros.playbackContent</c>）+ 每宏覆盖（<c>macros.json</c> 的 <c>content</c>）：
    /// 读得到、写得回、取消覆盖后跟随默认。
    /// </summary>
    private static void ConfigRoundTrip(Assembly asm)
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-macro-content-" + Guid.NewGuid().ToString("N")[..8]);
        string anchor = Path.Combine(root, "LTools.json");
        string configs = Path.Combine(root, "configs");
        Directory.CreateDirectory(configs);

        Type configStore = asm.GetType("LTools.Features.ConfigStore", true)!;
        Type macroStore = asm.GetType("LTools.Features.MacroStore", true)!;
        Type macroSettings = asm.GetType("LTools.Features.MacroSettings", true)!;

        string oldConfig = (string)configStore.GetProperty("ConfigPath")!.GetValue(null)!;
        try
        {
            string macrosFile = Path.Combine(configs, "macros.json");
            File.WriteAllText(macrosFile, """
                {
                  "SchemaVersion": 4,
                  "Macros": {
                    "PlaybackContent": "keyboard",
                    "Items": [
                      { "Id": "m1", "Name": "只放鼠标", "Content": "mouse" },
                      { "Id": "m2", "Name": "跟随默认" }
                    ]
                  }
                }
                """);

            configStore.GetProperty("ConfigPath")!.SetValue(null, anchor);
            macroStore.GetMethod("Invalidate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, null);

            // 让运行期设置读这份临时配置（全局默认 = 仅键盘）
            var cfg = LTools.Config.AppConfig.Load(anchor);
            macroSettings.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .First(m => m.Name == "Load")
                .Invoke(null, new object[] { cfg });

            Check(ContentOf(macroStore, "m1") == "Mouse", $"每宏覆盖：m1 = 仅鼠标（实际 {ContentOf(macroStore, "m1")}）");
            Check(ContentOf(macroStore, "m2") == "Keyboard",
                $"跟随全局：m2 = 全局默认的仅键盘（实际 {ContentOf(macroStore, "m2")}）");

            // 写回：把 m1 改成仅键盘 → 落盘与读回都要变
            SavePlayback(macroStore, "m1", "keyboard");
            Check(File.ReadAllText(macrosFile).Contains("\"Content\": \"keyboard\"", StringComparison.Ordinal),
                "写回后 macros.json 里有 content = keyboard");
            Check(ContentOf(macroStore, "m1") == "Keyboard", "改完读回来是仅键盘");

            // 取消覆盖（null）→ 字段不再落盘，读回来跟全局默认
            SavePlayback(macroStore, "m1", null);
            Check(!File.ReadAllText(macrosFile).Contains("\"Content\"", StringComparison.Ordinal),
                "取消覆盖后不再写 content 字段");
            Check(ContentOf(macroStore, "m1") == "Keyboard", "取消覆盖后跟随全局（仅键盘）");
        }
        catch (Exception ex)
        {
            Check(false, "配置往返不该抛异常：" + ex);
        }
        finally
        {
            configStore.GetProperty("ConfigPath")!.SetValue(null, oldConfig);
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    private static string ContentOf(Type macroStore, string id)
    {
        object playback = macroStore.GetMethod("PlaybackOf")!.Invoke(null, new object?[] { id })!;
        return playback.GetType().GetProperty("Content")!.GetValue(playback)!.ToString()!;
    }

    private static void SavePlayback(Type macroStore, string id, string? content)
    {
        Type overrideType = macroStore.GetNestedType("MacroPlaybackOverride",
            BindingFlags.Public | BindingFlags.NonPublic)!;
        object overrides = Activator.CreateInstance(overrideType, new object?[] { null, null, null, content })!;
        macroStore.GetMethod("SavePlaybackSettings")!.Invoke(null, new object?[] { id, null, overrides });
    }

    // ============ 设置页 ============

    /// <summary>
    /// 设置页「回放默认值 → 回放内容」这一行：三档都在、回显全局默认、选中即写盘并让运行期设置立刻生效。
    /// 只建控件、不显示窗口（<c>Reload</c> 不依赖可视树）。
    /// </summary>
    /// <remarks>
    /// 这一段必须落在冒烟自己的数据根（<c>AppContext.BaseDirectory</c>）：设置页保存走
    /// <c>MacroFileStore.Modify</c> → <c>ConfigIO.Resolve(null)</c> → <c>ConfigLocator.FindPath()</c>，
    /// 不认测试里临时改的 <c>ConfigStore.ConfigPath</c>（生产里两者本来就相等）。
    /// 跑完把 <c>macros.json</c> 还原 / 删掉，不留给下一次。
    /// </remarks>
    private static void SettingsPage(Assembly asm)
    {
        string dataRoot = AppContext.BaseDirectory;
        string configs = Path.Combine(dataRoot, "configs");
        string macrosFile = Path.Combine(configs, "macros.json");
        bool hadConfigs = Directory.Exists(configs);
        string? backup = File.Exists(macrosFile) ? File.ReadAllText(macrosFile) : null;

        Directory.CreateDirectory(configs);
        File.WriteAllText(macrosFile, """
            { "SchemaVersion": 4, "Macros": { "PlaybackContent": "keyboard", "Items": [] } }
            """);

        Type configStore = asm.GetType("LTools.Features.ConfigStore", true)!;
        Type macroSettings = asm.GetType("LTools.Features.MacroSettings", true)!;
        string oldConfig = (string)configStore.GetProperty("ConfigPath")!.GetValue(null)!;
        try
        {
            configStore.GetProperty("ConfigPath")!.SetValue(null, Path.Combine(dataRoot, "LTools.json"));

            // 控件里的 StaticResource 要能找到主题；只建一次 Application
            if (Application.Current == null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
                });
            }

            Type viewType = asm.GetType("LTools.Views.Controls.MacroView", throwOnError: true)!;
            var view = (FrameworkElement)Activator.CreateInstance(viewType)!;
            viewType.GetMethod("Reload", BindingFlags.Public | BindingFlags.Instance)!.Invoke(view, null);

            var combo = (ComboBox)view.FindName("PlaybackContentBox")!;
            Check(combo != null, "设置页有「回放内容」下拉");
            if (combo == null) return;
            Check(combo.Items.Count == 3, $"三档都在（实际 {combo.Items.Count} 项）");
            Check(combo.SelectedIndex == 1, $"回显全局默认「仅键盘」（实际第 {combo.SelectedIndex} 项）");

            combo.SelectedIndex = 2;   // 仅鼠标：选中即写盘
            string text = File.ReadAllText(macrosFile);
            Check(text.Contains("\"PlaybackContent\": \"mouse\"", StringComparison.Ordinal),
                "选中「仅鼠标」立刻写进 macros.json");
            Check(PlaybackContentOf(macroSettings) == "Mouse", "运行期设置立刻生效（不用重启）");

            combo.SelectedIndex = 0;
            Check(PlaybackContentOf(macroSettings) == "All", "切回「全部」也立刻生效");

            // 每宏「回放设置…」：下拉回显要和 meta.Content 对上（0 = 跟随默认）
            Type dialogType = asm.GetType("LTools.Views.MacroPlaybackDialog", throwOnError: true)!;
            ConstructorInfo ctor = dialogType
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(c => c.GetParameters().Length == 3);

            var dialog = (Window)ctor.Invoke(new object?[]
                { "m1", "名字", new MacroMeta { Id = "m1", Content = "mouse" } });
            var dialogBox = (ComboBox)dialog.FindName("ContentBox")!;
            Check(dialogBox.Items.Count == 4, $"对话框四档都在（实际 {dialogBox.Items.Count} 项）");
            Check(dialogBox.SelectedIndex == 3, $"对话框回显「仅鼠标」（实际第 {dialogBox.SelectedIndex} 项）");

            var inheritDialog = (Window)ctor.Invoke(new object?[] { "m1", "名字", new MacroMeta { Id = "m1" } });
            var inheritBox = (ComboBox)inheritDialog.FindName("ContentBox")!;
            Check(inheritBox.SelectedIndex == 0, $"没单独设置时回显「跟随默认」（实际第 {inheritBox.SelectedIndex} 项）");
        }
        catch (Exception ex)
        {
            Check(false, "设置页那行不该抛异常：" + ex);
        }
        finally
        {
            configStore.GetProperty("ConfigPath")!.SetValue(null, oldConfig);
            try
            {
                if (backup != null) File.WriteAllText(macrosFile, backup);
                else if (File.Exists(macrosFile)) File.Delete(macrosFile);
                if (!hadConfigs && Directory.Exists(configs) && !Directory.EnumerateFileSystemEntries(configs).Any())
                    Directory.Delete(configs);
            }
            catch { /* 清理失败不影响结论 */ }
        }
    }

    private static string PlaybackContentOf(Type macroSettings)
        => macroSettings.GetProperty("PlaybackContent", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!.ToString()!;

    // ============ 断言 ============

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) _failed++;
    }

    private static bool Near(POINT point, int x, int y) => Math.Abs(point.X - x) <= 2 && Math.Abs(point.Y - y) <= 2;

    private static (int X, int Y) OffsetPoint(POINT origin, int dx, int dy)
    {
        int left = GetSystemMetrics(SmXVirtualScreen);
        int top = GetSystemMetrics(SmYVirtualScreen);
        int width = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        int height = Math.Max(1, GetSystemMetrics(SmCyVirtualScreen));
        return (Math.Clamp(origin.X + dx, left, left + width - 1),
                Math.Clamp(origin.Y + dy, top, top + height - 1));
    }

    // ============ 键盘钩子（只数按键，不拦） ============

    private static readonly object SeenGate = new();
    private static readonly List<ushort> SeenKeys = new();
    private static readonly ManualResetEventSlim _ready = new(false);
    private static IntPtr _hook;
    private static uint _hookThreadId;
    private static readonly HookProc Proc = Hook;   // 必须保持引用，否则委托被回收后钩子回调指针失效

    private static void HookThread()
    {
        _hookThreadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WhKeyboardLl, Proc, IntPtr.Zero, 0);
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);   // 先把消息队列建出来，PostThreadMessage(WM_QUIT) 才有效
        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            // 只需要泵着：钩子回调由系统送到这条线程的队列里
        }

        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private static void StopHook()
    {
        if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    private static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmKeyUp || wParam == WmSysKeyDown || wParam == WmSysKeyUp))
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            lock (SeenGate) SeenKeys.Add((ushort)info.VkCode);
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static void ClearSeen()
    {
        lock (SeenGate) SeenKeys.Clear();
    }

    private static int SeenCount(ushort vk)
    {
        lock (SeenGate) return SeenKeys.Count(k => k == vk);
    }

    // ============ Win32 ============

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
