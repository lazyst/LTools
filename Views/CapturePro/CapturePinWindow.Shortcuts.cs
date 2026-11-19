using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图窗口的快捷键（照上游 <c>pin_to_screen_shortcuts/*</c>）：键由全局钩子按
/// 「指针停在这张贴图上」投递（<see cref="PinShortcutRoute"/>），这里只负责把动作落到
/// 右键菜单里已有的那些方法上——**不另写一套实现**。
/// </summary>
/// <remarks>
/// 动作能不能做由各自的方法自己判（实时贴图不能标注 / 不能穿透、没有原文就没有「复制原文」…），
/// 做不了就什么都不发生：宁可没反应，也不假装成功。
/// </remarks>
public partial class CapturePinWindow
{
    /// <summary>执行一个快捷键动作（UI 线程；钩子只负责投递）。</summary>
    private void PerformShortcut(PinShortcutAction action)
    {
        switch (action)
        {
            case PinShortcutAction.Copy:
                CopyImage();
                return;

            case PinShortcutAction.CopyOriginal:
                if (_cardText.Length > 0) NativeClipboard.SetText(_cardText);
                return;

            case PinShortcutAction.SaveAs:
                CaptureSave.TrySave(null, ComposeContent());
                return;

            case PinShortcutAction.Ocr:
                ToggleOcrMode();
                return;

            case PinShortcutAction.Annotate:
                if (_live) return;
                Dispatcher.BeginInvoke(new Action(AnnotateContent), DispatcherPriority.Background);
                return;

            case PinShortcutAction.Thumbnail:
                if (_live) return;
                SetThumbnail(!_thumb);
                return;

            case PinShortcutAction.HideToTop:
                if (_live) return;
                ToggleHideToTop();
                return;

            case PinShortcutAction.ClickThrough:
                if (_live) return;
                SetClickThrough(!_clickThrough);
                return;

            case PinShortcutAction.Lock:
                SetLocked(!_locked);
                return;

            case PinShortcutAction.AlwaysOnTop:
                Topmost = !Topmost;
                RaiseToTop();
                _ctBar?.SetTopmost(Topmost);
                return;

            case PinShortcutAction.ShowBorder:
                if (_live) return;
                SetShowBorder(!_showBorder);
                return;

            case PinShortcutAction.ShowShadow:
                SetShowShadow(!_showShadow);
                return;

            case PinShortcutAction.Close:
                RequestUserClose();
                return;

            case PinShortcutAction.OpacityUp:
                SetOpacityPercent(_opacityPercent + PinSettings.OpacityStepPercent);
                return;

            case PinShortcutAction.OpacityDown:
                SetOpacityPercent(_opacityPercent - PinSettings.OpacityStepPercent);
                return;

            case PinShortcutAction.ZoomIn:
                SetZoom(PinMath.NextZoom(_zoom, 1, PinSettings.ZoomStepPercent));
                return;

            case PinShortcutAction.ZoomOut:
                SetZoom(PinMath.NextZoom(_zoom, -1, PinSettings.ZoomStepPercent));
                return;

            case PinShortcutAction.MoveUp: MoveByShortcut(0, -1); return;
            case PinShortcutAction.MoveDown: MoveByShortcut(0, 1); return;
            case PinShortcutAction.MoveLeft: MoveByShortcut(-1, 0); return;
            case PinShortcutAction.MoveRight: MoveByShortcut(1, 0); return;

            case PinShortcutAction.SelectAll:
                CapturePinManager.SelectAll();
                return;

            case PinShortcutAction.SaveSelected:
            {
                var images = CapturePinManager.Selected.Select(p => p.Image).ToArray();
                if (images.Length > 0) CaptureSave.TrySaveMany(null, images);
                return;
            }

            case PinShortcutAction.CopyFilesSelected:
            {
                var images = CapturePinManager.Selected.Select(p => p.Image).ToArray();
                if (images.Length == 0) return;

                int copied = PinCopyCache.Copy(images);
                ToastService.Show(copied > 0 ? $"已复制 {copied} 个文件到剪贴板" : "复制文件失败",
                    "贴图", copied > 0 ? 2600 : 3600,
                    copied > 0 ? ToastKind.success : ToastKind.warning, key: "pin-copy-files");
                return;
            }
        }
    }

    /// <summary>
    /// 键盘挪动（<c>move_cursor_*</c>）：1 DIP 一格，按住 Shift ×10。
    /// 走与拖动同一套夹取（<see cref="PinMath.ConstrainGroupMove"/>），
    /// 不会把贴图推出桌面，挪完同步进贴图历史。
    /// </summary>
    private void MoveByShortcut(int dx, int dy)
    {
        if (_locked || _live) return;

        double step = (Win32.GetAsyncKeyState(0xA0) & 0x8000) != 0
                      || (Win32.GetAsyncKeyState(0xA1) & 0x8000) != 0
            ? 10
            : 1;

        var box = new Rect(Left, Top, ActualWidth, ActualHeight);
        var delta = PinMath.ConstrainGroupMove(new Vector(dx * step, dy * step),
            new[] { box }, PinMath.VirtualDesktop, PinMath.KeepVisible);
        if (delta.X == 0 && delta.Y == 0) return;

        Left += delta.X;
        Top += delta.Y;
        CapturePinManager.Sync(this);
    }
}
