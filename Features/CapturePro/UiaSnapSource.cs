using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.UIA3;
using Interop.UIAutomationClient;
using LTools.Core;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>
/// 控件树的真实取数：UI Automation + <b>缓存请求</b>（照上游 <c>snow-ui-selector</c> 的
/// <c>NativeProvider</c>：<c>ElementFromHandleBuildCache</c> / <c>BuildUpdatedCache</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 旧实现（FlaUI 的 <c>FindAllChildren</c> + 逐个属性读）**每展开一层、每个孩子要 3–4 次跨进程调用**：
/// 一个几百孩子的 Pane 就是一两次全趟，慢应用上直接撞 500 ms 的连接超时，
/// 于是整条支路被记成「读失败」，表现就是「这个窗口只能吸整窗」。
/// 这里照上游改成一次调用取回一层的全部孩子（只缓存矩形 / 是否离屏 / 类型 / 名字 / id / 类名 / 是否可用），
/// 层数再深也只有「一层一次调用」。
/// </para>
/// <para>
/// 树的过滤条件用上游同一份 <c>ControlViewCondition</c>（去掉纯排版的中间层）。
/// 兄弟顺序仍然按 provider 给的顺序（UIA 的兄弟顺序不是 z 序），
/// 「等大的结构 Pane 挡在内容前面」由 <see cref="SnapTree"/> 的结构回溯兜住。
/// </para>
/// <para>
/// 线程：只在后台那趟（<c>expand: true</c>）被调用；UI 线程只读 <see cref="SnapTree"/> 的缓存。
/// </para>
/// </remarks>
internal sealed class UiaSnapSource : ISnapSource
{
    // UIA 属性号（与 UIAutomationClient.h 一致；Interop.UIAutomationClient 没导出常量类）
    private const int PropBoundingRectangle = 30001;
    private const int PropControlType = 30003;
    private const int PropName = 30005;
    private const int PropAutomationId = 30011;
    private const int PropClassName = 30012;
    private const int PropIsEnabled = 30020;
    private const int PropIsOffscreen = 30022;

    /// <summary>UIA 的 <c>Pane</c> / <c>Group</c> 控件类型号：等大时允许结构回溯。</summary>
    private const int ControlTypeGroup = 50026;
    private const int ControlTypePane = 50033;

    private readonly UIA3Automation _automation = new()
    {
        // 与既有口径一致：慢应用首次建树经常超过 400 ms
        ConnectionTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromMilliseconds(900),
    };

    private readonly object _gate = new();
    private IUIAutomationCacheRequest? _request;

    public object? Root(IntPtr hwnd)
    {
        IUIAutomation automation = Native();
        IUIAutomationCacheRequest request = Request(automation);

        try
        {
            return automation.ElementFromHandleBuildCache(hwnd, request);
        }
        catch (Exception ex) when (IsVanished(ex))
        {
            return null;    // 这一下窗口没了 / 不暴露 UIA
        }
    }

    public IReadOnlyList<SnapChild>? Children(object node)
    {
        var element = (IUIAutomationElement)node;
        IUIAutomationCacheRequest request = Request(Native());

        // 这一步是整棵树的唯一跨进程调用：连同孩子一起缓存回来
        IUIAutomationElement cached = element.BuildUpdatedCache(request);

        IUIAutomationElementArray? array;
        try
        {
            array = cached.GetCachedChildren();
        }
        catch (Exception ex)
        {
            // 空集合与「provider 不认缓存里的孩子」在这里都表现成没有孩子；
            // 真正的超时由 BuildUpdatedCache / 外层重试兜住。
            AppLog.Debug("snap", "缓存读孩子失败：" + ex.Message);
            return null;
        }

        int count = array?.Length ?? 0;
        if (count <= 0) return null;

        var list = new List<SnapChild>(count);
        for (int i = 0; i < count; i++)
        {
            IUIAutomationElement child;
            try { child = array!.GetElement(i); }
            catch { continue; }
            if (child == null) continue;

            Rectangle bounds;
            try
            {
                tagRECT rect = child.CachedBoundingRectangle;
                bounds = Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
            }
            catch { continue; }

            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            int type;
            bool offscreen;
            try
            {
                type = child.CachedControlType;
                offscreen = child.CachedIsOffscreen != 0;
            }
            catch { continue; }

            if (offscreen) continue;

            list.Add(new SnapChild(child, bounds, type is ControlTypePane or ControlTypeGroup));
        }

        return list.Count == 0 ? null : list;
    }

    public string Label(object node)
    {
        var element = (IUIAutomationElement)node;
        return ControlTargets.SnapLabel(TypeOf(element), Read(() => element.CachedName, ""));
    }

    public object? Describe(object node)
    {
        var element = (IUIAutomationElement)node;

        Rectangle bounds = Read(() =>
        {
            tagRECT rect = element.CachedBoundingRectangle;
            return Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
        }, Rectangle.Empty);

        return ControlTargets.DescribeCached(
            bounds,
            Read(() => element.CachedName, ""),
            Read(() => element.CachedAutomationId, ""),
            TypeOf(element),
            Read(() => element.CachedClassName, ""),
            Read(() => element.CachedIsEnabled != 0, false),
            Read(() => element.CachedIsOffscreen != 0, false));
    }

    private static string TypeOf(IUIAutomationElement element) =>
        ControlTargets.TypeName(Read(() => element.CachedControlType, 50025));

    private IUIAutomation Native() => (IUIAutomation)((UIA3Automation)_automation).NativeAutomation;

    /// <summary>每个窗口共用的缓存请求：只读一层（元素 + 孩子），属性一次取全。</summary>
    private IUIAutomationCacheRequest Request(IUIAutomation automation)
    {
        lock (_gate)
        {
            if (_request != null) return _request;

            IUIAutomationCacheRequest request = automation.CreateCacheRequest();
            request.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Element
                              | Interop.UIAutomationClient.TreeScope.TreeScope_Children;
            request.TreeFilter = automation.ControlViewCondition;
            request.AutomationElementMode =
                Interop.UIAutomationClient.AutomationElementMode.AutomationElementMode_Full;

            request.AddProperty(PropBoundingRectangle);
            request.AddProperty(PropControlType);
            request.AddProperty(PropName);
            request.AddProperty(PropAutomationId);
            request.AddProperty(PropClassName);
            request.AddProperty(PropIsEnabled);
            request.AddProperty(PropIsOffscreen);

            _request = request;
            return request;
        }
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    /// <summary>元素已经消失 / 这个窗口根本没有 UIA：当成「不暴露控件」，别把整次会话记成失败。</summary>
    private static bool IsVanished(Exception ex) =>
        ex is InvalidOperationException or System.Runtime.InteropServices.COMException;
}
