namespace LTools.Core.Actions;

/// <summary>
/// 功能单元的分类顺序（编辑器左栏与「内联取值」下拉共用一份）。
/// </summary>
/// <remarks>
/// <para>
/// 单元自己的 <see cref="UnitSpec.Category"/> 只决定「属于哪一类」；「哪一类排前面」在这里统一决定。
/// 以前左栏用 <c>switch</c>、取值下拉按分类字符串的文化序排，同一个分类在两处位置不一样，
/// 新增分类还容易漏改一处——现在都走 <see cref="Rank"/>。
/// </para>
/// <para>
/// 分类名会显示在界面上（左栏标题、取值下拉的「分类 · 名称」），改动时同步
/// <c>README</c> / <c>docs/ACTION_SYSTEM.md</c> 的分类表；动作 JSON 里不存分类，旧动作不受影响。
/// </para>
/// </remarks>
internal static class UnitCategories
{
    /// <summary>按「用户先想干什么」排：先控制流程，再输入，再内容处理，最后是系统与扩展。</summary>
    private static readonly string[] Order =
    {
        "流程",
        "键鼠与输入",
        "剪贴板",
        "文本与数据",
        "屏幕",
        "文件",
        "窗口与程序",
        "界面",
        "LTools 内置功能",
        "脚本",
    };

    /// <summary>排序权重；不在表里的分类（含空）排最后，按名字稳定排序。</summary>
    public static int Rank(string? category)
    {
        if (string.IsNullOrEmpty(category)) return Order.Length;
        int index = Array.IndexOf(Order, category);
        return index < 0 ? Order.Length : index;
    }
}
