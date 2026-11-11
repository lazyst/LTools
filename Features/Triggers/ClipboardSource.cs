using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 剪贴板变化事件源（<c>clipboard.change</c>，见 <c>docs/AUTO_TRIGGER.md</c> §13.4/13.5/13.7）。
/// 一个 <c>HWND_MESSAGE</c> 消息窗订阅 <c>WM_CLIPBOARDUPDATE</c>（照 <c>MacroHotkey</c>）；
/// 回调只把工作投递到后台线程，读剪贴板 / 构造事件都在后台，别堵消息泵。
/// </summary>
/// <remarks>
/// <para>
/// 一次复制常会产生多条 <c>WM_CLIPBOARDUPDATE</c>（每个格式一条），用 40ms 的合并窗口
/// 让它们只读一次剪贴板；规则级洪泛仍由 <c>DebounceMs</c> 收（§13.7）。
/// </para>
/// <para>
/// LTools 自己写剪贴板（发送文本 / 粘贴 / 复制文件 / 还原快照）由
/// <see cref="NativeClipboard.LastOwnWriteTick"/> 打标，命中后一小段窗口内的更新算
/// <c>OwnEvent</c>，交给 <see cref="EventMatcher"/> 按 <c>ignoreOwnWrites</c>（默认开）排除。
/// </para>
/// <para>
/// <see cref="Start"/> / <see cref="Stop"/> 要在 UI 线程调用（<c>TriggerService</c> 在
/// <c>App.OnStartup</c> / <c>OnExit</c> 里调），<c>HwndSource</c> 必须在有消息泵的线程上创建 / 释放。
/// </para>
/// </remarks>
internal sealed class ClipboardSource : ITriggerSource
{
    /// <summary>LTools 写剪贴板后，这段窗口内的更新都算自触发。</summary>
    private const long OwnWriteWindowMs = 500;

    /// <summary>合并一次复制产生的多条格式更新。</summary>
    private const int MergeDelayMs = 40;

    private readonly object _gate = new();
    private Action<TriggerEvent>? _fire;
    private HwndSource? _source;
    private bool _hasRules;
    private int _pending;

    public void Start(Action<TriggerEvent> fire)
    {
        lock (_gate)
        {
            _fire = fire;
            if (_source == null)
            {
                try
                {
                    var p = new HwndSourceParameters("LToolsClipboardTrigger")
                    {
                        ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
                        WindowStyle = 0,
                        Width = 0,
                        Height = 0,
                    };
                    var src = new HwndSource(p);
                    src.AddHook(WndProc);
                    if (!Win32.AddClipboardFormatListener(src.Handle))
                    {
                        AppLog.Warn("Triggers", "AddClipboardFormatListener 失败，剪贴板事件源不生效");
                        src.RemoveHook(WndProc);
                        src.Dispose();
                    }
                    else
                    {
                        _source = src;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Triggers", "创建剪贴板消息窗失败：" + ex.Message);
                }
            }
        }
        Reload();
    }

    /// <summary>订阅是全局一次，只刷新「有没有这类规则」的标志。</summary>
    public void Reload()
    {
        bool has = TriggerRegistry.Active.Any(r =>
            r.Kind == TriggerKind.@event && r.Enabled
            && string.Equals(r.EventType, "clipboard.change", StringComparison.OrdinalIgnoreCase));
        lock (_gate) _hasRules = has;
    }

    public void Stop()
    {
        lock (_gate)
        {
            _fire = null;
            _hasRules = false;
            if (_source == null) return;
            var src = _source;
            _source = null;
            try
            {
                Win32.RemoveClipboardFormatListener(src.Handle);
                src.RemoveHook(WndProc);
                src.Dispose();
            }
            catch (Exception ex) { AppLog.Warn("Triggers", "释放剪贴板消息窗失败：" + ex.Message); }
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WmClipboardUpdate) return IntPtr.Zero;
        handled = true;
        // 消息回调里绝不读剪贴板（全局独占、可能被别的程序卡住）；合并 + 后台处理。
        if (Interlocked.Exchange(ref _pending, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(MergeDelayMs).ConfigureAwait(false);
                    FireFromClipboard();
                }
                catch (Exception ex) { AppLog.Warn("Triggers", "剪贴板事件处理失败：" + ex.Message); }
                finally { Interlocked.Exchange(ref _pending, 0); }
            });
        }
        return IntPtr.Zero;
    }

    private void FireFromClipboard()
    {
        Action<TriggerEvent> fire;
        lock (_gate)
        {
            if (!_hasRules || _fire == null) return;
            fire = _fire;
        }

        string text = NativeClipboard.TryGetText(out string t) ? t : "";
        string tags = NativeClipboard.FormatTags();
        long last = NativeClipboard.LastOwnWriteTick;
        bool own = last > 0 && Environment.TickCount64 - last < OwnWriteWindowMs;

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = text,
            ["formats"] = tags,
        };
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggerClipboardText"] = Value.FromText(text),
            ["triggerClipboardFormats"] = Value.FromText(tags),
        };

        fire(new TriggerEvent
        {
            EventType = "clipboard.change",
            Fields = fields,
            Values = values,
            Foreground = null,   // §13.3：用命中瞬间的 ForegroundAppCache.Current
            OwnEvent = own,
        });
    }
}
