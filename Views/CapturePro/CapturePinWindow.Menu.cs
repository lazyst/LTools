using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的右键菜单与分组 / 多选：菜单只在按下时现建（见 <see cref="Window_RightDown"/>）。
/// </summary>
public partial class CapturePinWindow
{
    // ============ 分组 / 多选 ============

    /// <summary>换分组（由 <see cref="CapturePinManager.MoveToGroup"/> 调；只管显示，藏 / 显在那边）。</summary>
    internal void SetGroup(string group)
    {
        _group = string.IsNullOrWhiteSpace(group) ? PinGroups.Default : group;
        RefreshBadges();
    }

    /// <summary>多选状态（由 <see cref="CapturePinManager"/> 调）。</summary>
    internal void SetSelected(bool on)
    {
        if (_selected == on) return;
        _selected = on;
        ApplyFrame();
        RefreshBadges();
    }

    /// <summary>
    /// 左上角两枚徽标：已选 + 分组名（默认组不显示，免得每张都挂一行）。
    /// 缩略图方块里一律收起——83 DIP 的方块会被盖掉一半。
    /// </summary>
    private void RefreshBadges()
    {
        SelectionBadge.Visibility = _selected && !_thumb ? Visibility.Visible : Visibility.Collapsed;

        bool named = !_thumb && !PinGroups.Same(_group, PinGroups.Default);
        GroupBadgeText.Text = _group;
        GroupBadge.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ 右键菜单 ============

    /// <summary>
    /// 菜单在**按下时**装好：WPF 的菜单服务在 <c>ContextMenuOpening</c> 触发时已经取过菜单，
    /// 那里现赋的话第一次右键只会存下菜单、不弹出（与步骤列表同一个坑）。
    /// </summary>
    private void Window_RightDown(object sender, MouseButtonEventArgs e)
    {
        RaiseToTop();
        ContextMenu = BuildMenu();
    }

    private ContextMenu BuildMenu()
    {
        _menu?.IsOpen = false;

        var menu = new ContextMenu { PlacementTarget = this };

        // 实时贴图：最上面一个「转成普通贴图」的出口，其余会破坏「跟着刷新」的项一律置灰
        if (_live)
        {
            var stop = new MenuItem { Header = "转为图像（停止实时）" };
            stop.Click += (_, _) => StopLive();
            menu.Items.Add(stop);
            menu.Items.Add(new Separator());
        }

        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) => CopyImage();
        menu.Items.Add(copy);

        // 文字 / 色卡贴图另有「复制原文」：照上游 copyOriginalContent（复制的是原文本，不是渲染出来的图）
        if (_cardKind != PinContentKind.Image && _cardText.Length > 0)
        {
            var copyText = new MenuItem { Header = "复制原文" };
            copyText.Click += (_, _) => NativeClipboard.SetText(_cardText);
            menu.Items.Add(copyText);
        }

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => CaptureSave.TrySave(null, _content);
        menu.Items.Add(save);

        // 标注这张贴图：覆盖层开在它上面，标完换回内容（菜单必须先收起，ShowDialog 挂在开着
        // 的 Popup 上会让菜单看不见也点不动）
        var annotate = new MenuItem { Header = "标注…", IsEnabled = !_live };
        annotate.Click += (_, _) =>
        {
            _menu?.IsOpen = false;
            Dispatcher.BeginInvoke(new Action(AnnotateContent),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        menu.Items.Add(annotate);

        var zoom = MenuFx.Submenu(menu, "缩放");
        zoom.IsEnabled = !_live;
        AddZoom(zoom, "100%", 1);
        AddZoom(zoom, "50%", 0.5);
        AddZoom(zoom, "200%", 2);
        AddZoom(zoom, "400%", 4);

        var opacity = MenuFx.Submenu(menu, "不透明度");
        AddOpacity(opacity, "100%", 1);
        AddOpacity(opacity, "80%", 0.8);
        AddOpacity(opacity, "60%", 0.6);
        AddOpacity(opacity, "40%", 0.4);

        menu.Items.Add(new Separator());

        AddGroupMenu(menu);
        AddBatchMenu(menu);

        menu.Items.Add(new Separator());

        var pin = new MenuItem { Header = "置顶", IsCheckable = true, IsChecked = Topmost };
        pin.Click += (_, _) =>
        {
            Topmost = !Topmost;
            RaiseToTop();
            _ctBar?.SetTopmost(Topmost);   // 穿透控件是同级窗口，置顶态要跟贴图一致
        };
        menu.Items.Add(pin);

        // 点击穿透：开着一张点不到的图时，旁边会浮出「不透明度 / 移动 / 退出」三个控件
        // （见 CapturePinWindow.ClickThrough.cs）。实时贴图不参加——它跟着源窗口刷新，
        // 点不到就没法转回普通贴图了。
        var clickThrough = new MenuItem
        {
            Header = "点击穿透",
            IsCheckable = true,
            IsChecked = _clickThrough,
            IsEnabled = !_live,
        };
        clickThrough.Click += (_, _) => SetClickThrough(!_clickThrough);
        menu.Items.Add(clickThrough);

        var locked = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = _locked };
        locked.Click += (_, _) => SetLocked(!_locked);
        menu.Items.Add(locked);

        // 局部显示：长截图钉在屏幕上比屏幕还大时用——取景框固定，拖着看
        var viewport = new MenuItem
        {
            Header = "局部显示",
            IsCheckable = true,
            IsChecked = _viewport,
            IsEnabled = !_live,
        };
        viewport.Click += (_, _) => SetViewport(!_viewport);
        menu.Items.Add(viewport);

        // 缩略图模式：缩成一个小方块让开路（与局部显示互斥，双击方块可还原）
        var thumb = new MenuItem
        {
            Header = "缩略图模式",
            IsCheckable = true,
            IsChecked = _thumb,
            IsEnabled = !_live,
        };
        thumb.Click += (_, _) => SetThumbnail(!_thumb);
        menu.Items.Add(thumb);

        // 成组移动：只有一张时没有「组」，置灰（开关本身记着，等第二张贴上来才生效）
        var group = new MenuItem
        {
            Header = "成组移动",
            IsCheckable = true,
            IsChecked = CapturePinManager.GroupMove,
            IsEnabled = CapturePinManager.Count > 1,
        };
        group.Click += (_, _) => CapturePinManager.GroupMove = !CapturePinManager.GroupMove;
        menu.Items.Add(group);

        menu.Items.Add(new Separator());

        var manager = new MenuItem { Header = "贴图管理…" };
        manager.Click += (_, _) => PinManagerWindow.Toggle();
        menu.Items.Add(manager);

        var textStyle = new MenuItem { Header = "文字样式…" };
        textStyle.Click += (_, _) =>
        {
            _menu?.IsOpen = false;      // 模态框不能挂在开着的 popup 上
            Dispatcher.BeginInvoke(new Action(() => PinTextStyleWindow.Show(null)),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        menu.Items.Add(textStyle);

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        var closeAll = new MenuItem { Header = "关闭全部贴图" };
        closeAll.Click += (_, _) => CapturePinManager.CloseAll();
        menu.Items.Add(closeAll);

        _menu = menu;
        return menu;
    }

    private void AddZoom(ItemsControl parent, string header, double zoom)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => SetZoom(zoom);
        parent.Items.Add(item);
    }

    /// <summary>
    /// 「分组」子菜单：先「移到」（勾的是这张图现在的组），再「只看」（勾的是当前的过滤），
    /// 中间夹「新建分组并移入…」（建完直接挪过去，省一步）。
    /// </summary>
    private void AddGroupMenu(ContextMenu menu)
    {
        var root = MenuFx.Submenu(menu, "分组");
        root.IsEnabled = !_live;               // 实时贴图换组 = 藏起来，等于把「跟着看」关掉了

        foreach (var name in CapturePinManager.Groups)
        {
            string target = name;
            var item = new MenuItem
            {
                Header = $"移到「{name}」",
                IsCheckable = true,
                IsChecked = PinGroups.Same(target, _group),
            };
            item.Click += (_, _) => MoveToGroup(target);
            root.Items.Add(item);
        }

        var create = new MenuItem { Header = "新建分组并移入…" };
        create.Click += (_, _) =>
        {
            _menu?.IsOpen = false;      // 模态输入框不能挂在开着的 popup 上
            Dispatcher.BeginInvoke(new Action(NewGroupAndMove),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        root.Items.Add(create);

        root.Items.Add(new Separator());

        var all = new MenuItem
        {
            Header = "显示全部分组",
            IsCheckable = true,
            IsChecked = CapturePinManager.ActiveGroup == null,
        };
        all.Click += (_, _) => CapturePinManager.SetActiveGroup(null);
        root.Items.Add(all);

        foreach (var name in CapturePinManager.Groups)
        {
            string target = name;
            var item = new MenuItem
            {
                Header = $"只看「{name}」",
                IsCheckable = true,
                IsChecked = PinGroups.Same(target, CapturePinManager.ActiveGroup),
            };
            item.Click += (_, _) => CapturePinManager.SetActiveGroup(target);
            root.Items.Add(item);
        }
    }

    /// <summary>挪到某个分组；挪出当前正看着的组会当场藏起来，给一句 toast 免得像「贴图丢了」。</summary>
    private void MoveToGroup(string group)
    {
        CapturePinManager.MoveToGroup(this, group);
        if (!IsVisible && CapturePinManager.ActiveGroup != null)
            ToastService.Show($"已移到「{_group}」，当前只看「{CapturePinManager.ActiveGroup}」",
                "贴图分组", 2600, ToastKind.info, key: "pin-group");
    }

    /// <summary>弹输入框建一个新分组并把这张图挪进去（贴图是 Topmost 且不抢焦点，框必须自己置顶）。</summary>
    private void NewGroupAndMove()
    {
        var (ok, value) = InputDialog.Show(null, "新建分组",
            $"分组名（最多 {PinGroups.MaxName} 字）", "", topmost: true);
        if (!ok) return;

        string? name = CapturePinManager.CreateGroup(value);
        if (name == null)
        {
            ToastService.Show("分组名不能为空", "贴图分组", 2400, ToastKind.warning);
            return;
        }

        MoveToGroup(name);
    }

    /// <summary>
    /// 「批量」子菜单：全选 / 取消 / 关闭 / 另存 / 锁定 / 缩略图，全部作用在多选上。
    /// 一张没选时后四项置灰；贴图不抢焦点、收不到键盘，所以底部留一句「Ctrl+单击」的提示。
    /// </summary>
    private void AddBatchMenu(ContextMenu menu)
    {
        int count = CapturePinManager.Selected.Count;
        var root = MenuFx.Submenu(menu, count > 0 ? $"批量（已选 {count} 张）" : "批量");

        var all = new MenuItem { Header = "全选", IsEnabled = CapturePinManager.Visible.Count > 0 };
        all.Click += (_, _) => CapturePinManager.SelectAll();
        root.Items.Add(all);

        var none = new MenuItem { Header = "取消选择", IsEnabled = count > 0 };
        none.Click += (_, _) => CapturePinManager.ClearSelection();
        root.Items.Add(none);

        root.Items.Add(new Separator());

        var close = new MenuItem
        {
            Header = count > 0 ? $"关闭所选（{count} 张）" : "关闭所选",
            IsEnabled = count > 0,
        };
        close.Click += (_, _) => CapturePinManager.CloseSelected();
        root.Items.Add(close);

        // 另存要弹文件夹选择，先收起菜单（模态框挂在开着的 popup 上会点不动）
        var save = new MenuItem { Header = "另存所选…", IsEnabled = count > 0 };
        save.Click += (_, _) =>
        {
            var images = CapturePinManager.Selected.Select(p => p.Image).ToArray();
            _menu?.IsOpen = false;
            Dispatcher.BeginInvoke(new Action(() => CaptureSave.TrySaveMany(null, images)),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        root.Items.Add(save);

        bool allLocked = count > 0 && CapturePinManager.Selected.All(p => p.Locked);
        var locked = new MenuItem
        {
            Header = allLocked ? "解锁所选" : "锁定所选",
            IsEnabled = count > 0,
        };
        locked.Click += (_, _) => CapturePinManager.SetSelectedLocked(!allLocked);
        root.Items.Add(locked);

        bool allThumb = count > 0 && CapturePinManager.Selected.All(p => p.Thumbnail);
        var thumb = new MenuItem
        {
            Header = allThumb ? "还原所选" : "缩略图所选",
            IsEnabled = count > 0,
        };
        thumb.Click += (_, _) => CapturePinManager.SetSelectedThumbnail(!allThumb);
        root.Items.Add(thumb);

        root.Items.Add(new Separator());
        root.Items.Add(new MenuItem { Header = "按住 Ctrl 再单击可多选", IsEnabled = false });
    }

    private void AddOpacity(ItemsControl parent, string header, double opacity)
    {
        var item = new MenuItem { Header = header };
        // 菜单改的是**用户不透明度**；穿透态下生效的是它自己那份（见 ApplyEffectiveOpacity）
        item.Click += (_, _) => SetOpacityPercent((int)Math.Round(opacity * 100));
        parent.Items.Add(item);
    }

    /// <summary>锁定：不拖动、不缩放，边框亮起来告诉用户「这是锁住的」。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        ApplyFrame();
        if (locked && (_dragging || _panning || _maybeSelect))
        {
            _dragging = false;
            _panning = false;
            _maybeSelect = false;
            EndShake(true);
            CapturePinManager.EndMove();
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        }
        _ctBar?.SetLocked(locked);
        ApplyCursor();
    }

    private void CopyImage()
    {
        if (NativeClipboard.SetImage(_content)) ToastService.Show("贴图已复制", "贴图", 1600);
        else ToastService.Show("复制贴图失败", "贴图", 0, ToastKind.error);
    }
}
