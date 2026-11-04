using System;
using System.Windows;
using System.Windows.Controls;

namespace LTools.Views.Controls;

/// <summary>
/// 标题栏里的「普通动作 / 组合动作」分段切换：把两种动作类型从「类型下拉框里的最后一项」
/// 提升为一眼可见、一点即换的入口。
/// </summary>
/// <remarks>
/// 控件只负责呈现与通知：用户点另一段时触发 <see cref="SelectionChanged"/>，由宿主对话框
/// 置跳转标志并关闭自己，再交给 <see cref="Views.ActionEditor"/> 的循环改开另一种编辑器。
/// 程序化设置 <see cref="IsComposite"/> 不会触发事件（避免宿主初始化时自己跳走）。
/// </remarks>
public partial class ActionKindSwitch : UserControl
{
    private bool _sync;

    public ActionKindSwitch()
    {
        InitializeComponent();
    }

    /// <summary>用户切换了类型（读 <see cref="IsComposite"/> 判断目标类型）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>当前是否选中「组合动作」。程序化赋值不触发 <see cref="SelectionChanged"/>。</summary>
    public bool IsComposite
    {
        get => CompositeItem.IsChecked == true;
        set
        {
            if (IsComposite == value) return;
            _sync = true;
            try
            {
                CompositeItem.IsChecked = value;
                NormalItem.IsChecked = !value;
            }
            finally
            {
                _sync = false;
            }
        }
    }

    /// <summary>点在同一段上（已是该类型）时 RadioButton 不会再次 Checked，天然不会重复触发。</summary>
    private void Item_Checked(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
