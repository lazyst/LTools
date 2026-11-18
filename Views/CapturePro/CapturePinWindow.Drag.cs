using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的鼠标手势：拖动 / 平移、滚轮缩放与不透明度、悬停 chrome、光标与边框。
/// </summary>
/// <remarks>
/// 贴图不抢焦点、收不到键盘，所以这里的一切都只用鼠标表达：双击关闭（缩略图里是还原）、
/// <c>Ctrl</c>+单击多选、<c>Ctrl</c>+拖动在取景框里移动窗口、滚轮缩放、<c>Ctrl</c>/<c>Shift</c>+滚轮改不透明度。
/// </remarks>
public partial class CapturePinWindow
{
    // ============ 鼠标 ============

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        RaiseToTop();
        PinStacking.Promote(this);      // 点了哪张，它就在记录里排到组最前

        // 点在自己的按钮上（右上角 ✕）就交给按钮：这个处理器在隧道阶段先到，
        // 要是照常走「按下 = 开始拖动」并 CaptureMouse()，按钮永远收不到 MouseUp、
        // Click 不会触发——用户得连点好几次、凑巧双击才关得掉（实测）。
        if (IsOnButton(e.OriginalSource as DependencyObject)) return;

        // 色卡上的复制按钮：点中就把那一行规范 CSS 放进剪贴板（不拖动、不关窗）
        if (CopyAt(e.GetPosition(this)))
        {
            e.Handled = true;
            return;
        }

        // 双击：缩略图里 = 还原成整张图（不是关掉，83px 的小方块太容易误关）；
        // 普通贴图 = 关闭（比右键菜单快一步；锁定也允许，它不会改位置）
        if (e.ClickCount == 2)
        {
            if (_thumb) SetThumbnail(false);
            else Close();
            e.Handled = true;
            return;
        }

        // Ctrl+单击 = 多选（贴图不抢焦点、收不到键盘，这是唯一的选择入口）。
        // 先不动手：取景框里 Ctrl 拖动是「移动窗口」，得看松手前有没有挪过再定。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _maybeSelect = true;
            _selectAt = CursorScreenDip();
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_locked) return;

        BeginGesture(e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>按下后真正开始的那一下手势：取景框里平移内容，其余情况移动窗口（含成组 / 多选）。</summary>
    private void BeginGesture(Point windowPos)
    {
        // 局部显示里拖动是「平移内容」：窗口不动，用窗口内坐标；
        // 移动窗口必须用**屏幕**坐标——窗口跟着走时窗口内坐标会互相抵消，
        // 每帧只落下最后一个增量，看着就是拖影 / 追不上手（见 DragTo）。
        if (IsPanGesture())
        {
            _panning = true;
            _dragStart = windowPos;
            _panStart = _pan;
            Cursor = Cursors.ScrollAll;
        }
        else
        {
            _dragging = true;
            _moveStart = CursorScreenDip();
            CapturePinManager.BeginMove(this);
            BeginShake(_moveStart);
            Cursor = Cursors.SizeAll;
        }
    }

    private bool IsPanGesture() => _viewport && (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    /// <summary>
    /// 拖到光标的这个**屏幕**位置（DIP）。位移一律是「现在 − 按下时」，
    /// 与 <see cref="Window.Left"/> / <see cref="Window.Top"/> 同一坐标系，
    /// 所以窗口正好停在光标带走的那段距离上，不会一顿一顿地回弹。
    /// </summary>
    internal void DragTo(Point screenDip)
    {
        CapturePinManager.MoveBy(this, screenDip.X - _moveStart.X, screenDip.Y - _moveStart.Y);
    }

    /// <summary>
    /// 光标在「屏幕 DIP」里的位置：<c>GetCursorPos</c> 给物理像素，
    /// 按窗口自己的 <c>TransformFromDevice</c> 换成 DIP（与 <c>Left/Top</c> 同一套换算）。
    /// </summary>
    private Point CursorScreenDip()
    {
        if (!Win32.GetCursorPos(out var pt)) return new Point(Left, Top);

        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            if (tfd.M11 > 0) sx = tfd.M11;
            if (tfd.M22 > 0) sy = tfd.M22;
        }
        return new Point(pt.X * sx, pt.Y * sy);
    }

    /// <summary>事件源是不是某个按钮（贴图里的 ✕）：按钮的点击不能当成拖动。</summary>
    private static bool IsOnButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        // Ctrl 按下还没挪：挪过 4 DIP 就作废「多选」，改判成拖动 / 平移
        if (_maybeSelect)
        {
            Point now = CursorScreenDip();
            if (Math.Abs(now.X - _selectAt.X) < 4 && Math.Abs(now.Y - _selectAt.Y) < 4)
            {
                e.Handled = true;
                return;
            }

            _maybeSelect = false;

            // 非取景框里 Ctrl+拖动 = 把内容拖出去（照上游 exportDragEnabledAt）；
            // 取景框里 Ctrl 拖动仍是「移动窗口」。锁定的贴图照样能拖出——导出不改几何。
            if (!_viewport && !_live && !_clickThrough)
            {
                e.Handled = true;
                try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
                BeginExportDrag();
                return;
            }

            if (_locked)                  // 锁着的贴图不跟着走，这一下什么都不做
            {
                try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
                return;
            }
            BeginGesture(e.GetPosition(this));
        }

        if (!_dragging && !_panning)
        {
            // 悬停在色卡的复制图标上：手型 + 「复制 HEX 颜色值」提示（照上游 PointingHandCursor / setToolTip）
            var hit = CopyHitAt(e.GetPosition(this));
            Cursor = hit != null ? Cursors.Hand : !_locked && _viewport ? Cursors.ScrollAll : Cursors.Arrow;
            string? tip = hit?.Label;
            if (!Equals(ToolTip, tip)) ToolTip = tip;
            return;
        }

        if (_panning)
        {
            Point at = e.GetPosition(this);
            _pan = PinMath.ClampPan(_panStart + (at - _dragStart), ContentSize, new Size(Width, Height));
            ApplyContent();
            e.Handled = true;
            return;
        }

        // 移动窗口一律交给登记处：单张时它只夹自己，成组移动时按「整组一个位移」夹。
        // 同一份光标位置顺手喂给抖动识别（拖动中快速来回甩 = 隐藏同组其他贴图）。
        Point cursor = CursorScreenDip();
        DragTo(cursor);
        SampleShake(cursor);
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_maybeSelect)
        {
            _maybeSelect = false;
            CapturePinManager.ToggleSelect(this);
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
            e.Handled = true;
            return;
        }

        if (!_dragging && !_panning) return;

        _dragging = false;
        _panning = false;
        EndShake(false);
        CapturePinManager.EndMove();
        ApplyCursor();
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        e.Handled = true;
    }

    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_locked) return;

        // Ctrl + 滚轮 = 不透明度；单独滚 = 缩放（缩放锚在光标下，见 PinMath.AnchoredOrigin）
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            // 滚轮改的是**用户不透明度**，穿透态下那一份不参与（照上游 applyEffectiveOpacity）
            SetOpacityPercent((int)Math.Round(PinMath.NextOpacity(_opacityPercent / 100.0, e.Delta) * 100));
            return;
        }

        if (_thumb) return;      // 缩略图里滚轮不改缩放（要改先双击还原）

        double next = PinMath.NextZoom(_zoom, e.Delta);
        if (Math.Abs(next - _zoom) < 0.0001) return;

        Point at = e.GetPosition(this);

        if (_viewport)
        {
            // 取景框不跟图走：只换内容缩放，再用锚点算出该看哪一块
            Vector pan = PinMath.ZoomAnchoredPan(_pan, at, _zoom, next);
            ApplyZoom(next);
            _pan = PinMath.ClampPan(pan, ContentSize, new Size(Width, Height));
            ApplyContent();
            return;
        }

        var origin = new Point(Left, Top);
        var oldSize = new Size(Width, Height);

        ApplyZoom(next);

        Point moved = PinMath.AnchoredOrigin(origin, oldSize, new Size(Width, Height), at);
        Left = moved.X;
        Top = moved.Y;
    }

    /// <summary>换缩放（滚轮与右键菜单共用；菜单点完按窗口中心锚定）。</summary>
    public void SetZoom(double zoom)
    {
        if (_live) return;                    // 实时那块永远 100%：缩放会让尺寸与选区对不上
        if (_thumb) SetThumbnail(false);      // 缩略图里改缩放 = 先还原（方块里缩放没有意义）
        double target = PinMath.ClampZoom(zoom);

        if (_viewport)
        {
            var center = new Point(Width / 2, Height / 2);
            Vector pan = PinMath.ZoomAnchoredPan(_pan, center, _zoom, target);
            ApplyZoom(target);
            _pan = PinMath.ClampPan(pan, ContentSize, new Size(Width, Height));
            ApplyContent();
            return;
        }

        var anchor = new Point(ActualWidth / 2, ActualHeight / 2);
        var origin = new Point(Left, Top);
        var oldSize = new Size(Width, Height);

        ApplyZoom(target);

        Point moved = PinMath.AnchoredOrigin(origin, oldSize, new Size(Width, Height), anchor);
        Left = moved.X;
        Top = moved.Y;
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = PinMath.ClampZoom(zoom);
        FitWindowToContent();      // 局部显示里是空操作：取景框大小不跟图走
        ApplyContent();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        RefreshHoverChrome();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        RefreshHoverChrome();
    }

    /// <summary>
    /// 悬停界面（右上角 ✕、左下角状态提示）的显隐。
    /// 小窗口与缩略图里按钮会盖住内容，<see cref="PinMath.ShowsHoverButtons"/> 直接不给
    /// （出口仍在：双击、右键菜单）；提示只在局部显示时出现，退回去就收起。
    /// </summary>
    /// <remarks>
    /// 判「悬停」要写成 <c>_hovering || IsMouseOver</c>：无头冒烟里 <c>MouseEnter</c> 是反射调的，
    /// <c>IsMouseOver</c> 恒为 false，只看它的话冒烟永远看不到按钮。
    /// </remarks>
    private void RefreshHoverChrome()
    {
        bool hover = _hovering || IsMouseOver;

        // 穿透态鼠标根本点不到贴图，✕ 摆出来也点不动（退出走控制栏的圆按钮），干脆不画
        CloseBtn.Visibility = hover && !_clickThrough && PinMath.ShowsHoverButtons(Width, Height, _thumb)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hover && _viewport && !_thumb)
        {
            ViewHintText.Text = "局部显示 · 拖动看别处 · Ctrl 拖动移窗口";
            ViewHint.Visibility = Visibility.Visible;
        }
        else
        {
            ViewHint.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 空闲光标：取景框里用四向箭头暗示「拖 = 平移」，其它情况是普通箭头
    /// （拖动中的 <c>SizeAll</c> 在按下时临时设，松手回到这里）。
    /// 穿透态一律箭头：那是提示「这张图现在拖不动」。
    /// </summary>
    private void ApplyCursor() =>
        Cursor = !_clickThrough && !_locked && _viewport ? Cursors.ScrollAll : Cursors.Arrow;

    /// <summary>
    /// 底色 / 描边这两层要不要露：实时贴图让给 DWM 缩略图；缩略图模式要那块不透明底；
    /// 图自带圆角 / 阴影时不能铺不透明底（透明的地方会画成深灰），自带效果时也不再叠窗口描边。
    /// </summary>
    private void ApplyChrome()
    {
        bool backdrop = !_live && (_thumb || !_appearance.HasShape);
        Backdrop.Visibility = backdrop ? Visibility.Visible : Visibility.Collapsed;
        ApplyFrame();
    }

    /// <summary>
    /// 边框颜色同时表达几种状态：多选（2px 亮蓝）优先，其次拖入 / 拖出（2px 强调蓝，
    /// 照上游 <c>pin_to_screen/border_active_color</c> 的 #69B1FF），再其次锁定（亮蓝）、
    /// 局部显示（淡蓝），最后是默认的 1px 白边。
    /// </summary>
    /// <remarks>
    /// 图里已经烘进阴影 / 边框（<see cref="PinAppearance.SuppressesFrame"/>）时整圈描边收起：
    /// 那圈内边距也是图的一部分，再包一个方框就成了「画框」。
    /// 直角 / 只有圆角时描边跟着圆角走（<see cref="PinAppearance.RadiusDip"/>）。
    /// </remarks>
    private void ApplyFrame()
    {
        if (_live || _appearance.SuppressesFrame)
        {
            Frame.Visibility = Visibility.Collapsed;
            return;
        }

        Frame.Visibility = Visibility.Visible;
        Frame.CornerRadius = new CornerRadius(_thumb ? 0 : _appearance.RadiusDip(_scale, _zoom));

        if (_selected)
        {
            // 2px 描边是「压在图上」画的（见 XAML 里 Frame 的注释），加粗不会挤动内容
            Frame.BorderThickness = new Thickness(2);
            Frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            return;
        }

        if (_fileDragActive || _exporting)
        {
            Frame.BorderThickness = new Thickness(2);
            Frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x69, 0xB1, 0xFF));
            return;
        }

        Frame.BorderThickness = new Thickness(1);
        Frame.BorderBrush = new SolidColorBrush(_locked
            ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : _viewport
                ? Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)
                : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }
}
