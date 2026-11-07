using System.Text.Json.Serialization;

namespace LTools.Core.Actions;

/// <summary>单元种类。<see cref="pure"/> = 只算值，可以内联在任何「值」的位置。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnitKind
{
    /// <summary>有副作用的普通步骤。</summary>
    action,

    /// <summary>取值单元：找图 / 取剪贴板 / 读窗口…可以内联在参数或条件里。</summary>
    pure,
}

/// <summary>参数编辑器形态。只描述「应该长什么样」，由 UI 层实现。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputEditor
{
    text,
    multiline,
    number,
    boolean,
    enumList,
    file,
    folder,
    image,
    /// <summary>
    /// 屏幕区域 <c>x,y,w,h</c>：文本 + 「框选…」按钮，拖一块屏幕自动填进来。
    /// 配套的枚举参数（<c>region</c> / <c>mode</c>）由参数对话框按 <c>VisibleWhen</c> 自动切到 <c>rect</c>。
    /// </summary>
    region,
    variable,
    condition,
}

/// <summary>步骤可以携带哪种子步骤。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BodyKind
{
    none,

    /// <summary>循环体（<c>body</c>）。</summary>
    body,

    /// <summary>条件分支（<c>then</c> / <c>else</c>）。</summary>
    thenElse,
}

/// <summary>
/// 一个输入参数的声明。编辑器、参数校验、步骤摘要都由这份元数据派生。
/// </summary>
/// <param name="Key">存储键。</param>
/// <param name="Label">界面显示名。</param>
/// <param name="Type">期望的值类型；<c>null</c> = 任意类型。</param>
/// <param name="Editor">编辑器形态。</param>
/// <param name="Required">是否必填。</param>
/// <param name="Default">默认值。</param>
/// <param name="VisibleWhen">显示条件（如 <c>operation == 'write'</c>），由 UI 解释。</param>
/// <param name="Options">枚举选项（<see cref="InputEditor.enumList"/> 用），存的是英文键。</param>
/// <param name="Labels">
/// 该参数私有的选项中文名，优先于 <see cref="OptionLabels"/> 的全局表。
/// 只在同一个键于不同单元含义不同时才用（如 <c>add</c>：列表里是「追加一项」、计算里是「加法」）。
/// </param>
public sealed record InputSpec(
    string Key,
    string Label,
    ValueKind? Type = null,
    InputEditor Editor = InputEditor.text,
    bool Required = false,
    Value? Default = null,
    string? VisibleWhen = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyDictionary<string, string>? Labels = null);

/// <summary>一个输出参数的声明。输出要写哪个变量由步骤的 <c>out</c> 映射决定。</summary>
/// <param name="Key">输出键。</param>
/// <param name="Label">界面显示名。</param>
/// <param name="Type">输出值类型；<c>null</c> = 任意。</param>
/// <param name="DefaultVariable">新建步骤时建议的变量名。</param>
/// <param name="Description">
/// 一句话说明「这个变量装的是什么」（含空值 / 前置条件等坑）。
/// 参数表单的提示行、变量插入菜单和将来的单元文档都由它派生，别再另写一份文案。
/// </param>
public sealed record OutputSpec(
    string Key,
    string Label,
    ValueKind? Type = null,
    string? DefaultVariable = null,
    string? Description = null);

/// <summary>功能单元的静态声明：键、名称、分类、输入/输出规格、是否携带子步骤。</summary>
public sealed class UnitSpec
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string Category { get; init; } = "";

    public string Icon { get; init; } = "";

    public string Description { get; init; } = "";

    public UnitKind Kind { get; init; } = UnitKind.action;

    public BodyKind Body { get; init; } = BodyKind.none;

    /// <summary>
    /// 允许步骤携带声明之外的参数（如「调用子动作」按输入变量名传值）。
    /// 这些参数照常求值，但不在 <see cref="Inputs"/> 里，编辑器需要另行生成表单。
    /// </summary>
    public bool DynamicInputs { get; init; }

    public IReadOnlyList<InputSpec> Inputs { get; init; } = Array.Empty<InputSpec>();

    public IReadOnlyList<OutputSpec> Outputs { get; init; } = Array.Empty<OutputSpec>();

    /// <summary>校验声明自身是否完整；注册时调用，避免坏单元漏到运行时。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key))
            throw new ActionException("单元缺少 Key");

        if (string.IsNullOrWhiteSpace(Name))
            throw new ActionException($"单元「{Key}」缺少 Name");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in Inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Key))
                throw new ActionException($"单元「{Key}」有输入缺少 Key");
            if (!seen.Add(input.Key))
                throw new ActionException($"单元「{Key}」的输入「{input.Key}」重复");
            if (input.Default != null && input.Type != null && input.Default.Kind != input.Type)
            {
                try { input.Default.Coerce(input.Type); }
                catch (ActionException ex)
                {
                    throw new ActionException(
                        $"单元「{Key}」的输入「{input.Key}」默认值类型不对：{ex.Message}", ex);
                }
            }
        }

        seen.Clear();
        foreach (var output in Outputs)
        {
            if (string.IsNullOrWhiteSpace(output.Key))
                throw new ActionException($"单元「{Key}」有输出缺少 Key");
            if (!seen.Add(output.Key))
                throw new ActionException($"单元「{Key}」的输出「{output.Key}」重复");
        }
    }
}
