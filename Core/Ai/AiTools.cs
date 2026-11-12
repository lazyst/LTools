using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具面（v1）。只有「读正式动作」和「写草稿」，**没有任何直接改正式动作的工具**。
/// </summary>
/// <remarks>
/// 工具名与参数说明是给外部 agent 看的契约；实现都在 <see cref="AiToolHandlers"/>。
/// 返回统一为 JSON 文本：成功带 <c>ok: true</c>，失败带 <c>ok: false</c> + <c>error</c> + <c>message</c>。
/// </remarks>
[McpServerToolType]
public sealed class AiTools
{
    [McpServerTool(Name = "ltools_health"), Description(
        "检查 LTools 的 AI 接入状态：版本、开关、端口、审批模式、草稿数、最近错误。不返回令牌。")]
    public static string Health() => AiToolHandlers.Call("ltools_health", null, AiToolHandlers.Health);

    [McpServerTool(Name = "ltools_docs_get"), Description(
        "读随包分发的知识文件：format（动作 JSON 格式与工作流）/ units（全部动作单元目录）/ examples（完整示例）。")]
    public static string DocsGet(
        [Description("format / units / examples")] string name) =>
        AiToolHandlers.Call("ltools_docs_get", null, () => AiToolHandlers.DocsGet(name));

    [McpServerTool(Name = "ltools_units_list"), Description(
        "列出动作单元（可按 key / 名称 / 分类 / 说明过滤）。先用它确认能力存在，别臆造动作单元键。")]
    public static string UnitsList(
        [Description("可选：过滤词")] string? query = null) =>
        AiToolHandlers.Call("ltools_units_list", null, () => AiToolHandlers.UnitsList(query));

    [McpServerTool(Name = "ltools_units_get"), Description(
        "取一个动作单元的完整规格：输入（类型 / 必填 / 默认 / 可见条件 / 枚举选项）、输出、子步骤。")]
    public static string UnitsGet(
        [Description("动作单元键，如 ui.toast")] string key) =>
        AiToolHandlers.Call("ltools_units_get", null, () => AiToolHandlers.UnitsGet(key));

    [McpServerTool(Name = "ltools_actions_list"), Description(
        "列出用户的正式动作（只读）。改动作必须先 staging_create(copyFromActionId:...) 复制到暂存区。")]
    public static string ActionsList(
        [Description("可选：按名称 / Id 过滤")] string? query = null,
        [Description("最多返回多少条，默认 50")] int limit = 50) =>
        AiToolHandlers.Call("ltools_actions_list", null, () => AiToolHandlers.ActionsList(query, limit));

    [McpServerTool(Name = "ltools_actions_get"), Description(
        "读一个正式动作的完整 ActionProgram（只读）。要修改请先复制到暂存区。")]
    public static string ActionsGet(
        [Description("动作 Id，如 a3")] string id) =>
        AiToolHandlers.Call("ltools_actions_get", id, () => AiToolHandlers.ActionsGet(id));

    [McpServerTool(Name = "ltools_subactions_list"), Description(
        "列出全局子动作（只读）：id / 名称 / 输入 / 输出 / 步数。动作里用 sub.run 调用，参数名 = 输入变量名。")]
    public static string SubActionsList(
        [Description("可选：按名称 / id / 变量名过滤")] string? query = null,
        [Description("最多返回多少条，默认 100")] int limit = 100) =>
        AiToolHandlers.Call("ltools_subactions_list", null, () => AiToolHandlers.SubActionsList(query, limit));

    [McpServerTool(Name = "ltools_subactions_get"), Description(
        "读一个全局子动作的完整定义（变量方向 + 步骤，只读）。用 sub.run 调用它，out 映射写回输出。")]
    public static string SubActionsGet(
        [Description("子动作 id（subactions/ 下的文件名）")] string id) =>
        AiToolHandlers.Call("ltools_subactions_get", id, () => AiToolHandlers.SubActionsGet(id));

    [McpServerTool(Name = "ltools_subactions_check"), Description(
        "校验一份子动作定义（不落盘）：与编辑器「校验」同一套规则，返回 ok / issues / errors / warnings。")]
    public static string SubActionsCheck(
        [Description("完整子动作定义（JSON 对象或 JSON 字符串：id / name / variables / steps）")]
        JsonElement program) =>
        AiToolHandlers.Call("ltools_subactions_check", null, () => AiToolHandlers.SubActionsCheck(program));

    [McpServerTool(Name = "ltools_subactions_write"), Description(
        "新建或整份写入一个全局子动作（需要写入开关 + 审批）。写前自动静态校验，有 error 就拒绝；" +
        "expectedRevision 给了就按 CAS 校验（文件内容 sha256，先用 subactions_get 拿）。")]
    public static string SubActionsWrite(
        [Description("完整子动作定义（id 就是文件名，如 sub-1a2b3c4d）")] JsonElement program,
        [Description("可选：期望的当前 revision（覆盖已有子动作时用）")] string? expectedRevision = null) =>
        AiToolHandlers.Call("ltools_subactions_write", null,
            () => AiToolHandlers.SubActionsWrite(program, expectedRevision));

    [McpServerTool(Name = "ltools_subactions_delete"), Description(
        "删除一个全局子动作（需要写入开关 + 审批）。会先列出调用它的动作位置。")]
    public static string SubActionsDelete(
        [Description("子动作 id")] string id) =>
        AiToolHandlers.Call("ltools_subactions_delete", id, () => AiToolHandlers.SubActionsDelete(id));

    [McpServerTool(Name = "ltools_staging_list"), Description(
        "列出暂存区草稿：标题、来源动作、更新时间、状态（草稿 / 已保存 / 已采纳）。")]
    public static string StagingList() =>
        AiToolHandlers.Call("ltools_staging_list", null, AiToolHandlers.StagingList);

    [McpServerTool(Name = "ltools_staging_create"), Description(
        "新建草稿。改用户已有动作时传 copyFromActionId（只读复制到草稿，正式动作不会被改）。")]
    public static string StagingCreate(
        [Description("草稿标题")] string title,
        [Description("可选：一句话说明")] string? description = null,
        [Description("可选：从哪个正式动作复制")] string? copyFromActionId = null) =>
        AiToolHandlers.Call("ltools_staging_create", null,
            () => AiToolHandlers.StagingCreate(title, description, copyFromActionId));

    [McpServerTool(Name = "ltools_staging_open"), Description(
        "读草稿：program、文件清单、revision、绝对目录（也可以直接用 fs 工具编辑该目录）。")]
    public static string StagingOpen(
        [Description("草稿 id")] string draftId) =>
        AiToolHandlers.Call("ltools_staging_open", draftId, () => AiToolHandlers.StagingOpen(draftId));

    [McpServerTool(Name = "ltools_staging_write"), Description(
        "整份写入草稿的 program.json（CAS：expectedRevision 与当前内容不一致时拒绝，要求重读）。")]
    public static string StagingWrite(
        [Description("草稿 id")] string draftId,
        [Description("完整 ActionProgram（JSON 对象）")] JsonElement program,
        [Description("可选：期望的当前 revision")] string? expectedRevision = null) =>
        AiToolHandlers.Call("ltools_staging_write", draftId,
            () => AiToolHandlers.StagingWrite(draftId, program, expectedRevision));

    [McpServerTool(Name = "ltools_staging_read_file"), Description(
        "读草稿 files/ 下的图片，返回 base64 与字节数。")]
    public static string StagingReadFile(
        [Description("草稿 id")] string draftId,
        [Description("相对 files/ 的路径，如 shot.png")] string path) =>
        AiToolHandlers.Call("ltools_staging_read_file", draftId,
            () => AiToolHandlers.StagingReadFile(draftId, path));

    [McpServerTool(Name = "ltools_staging_write_file"), Description(
        "写草稿 files/ 下的图片（base64）。v1 只接受 png / jpg / jpeg / bmp / gif / webp。")]
    public static string StagingWriteFile(
        [Description("草稿 id")] string draftId,
        [Description("相对 files/ 的路径，如 shot.png")] string path,
        [Description("文件内容（base64）")] string contentBase64) =>
        AiToolHandlers.Call("ltools_staging_write_file", draftId,
            () => AiToolHandlers.StagingWriteFile(draftId, path, contentBase64));

    [McpServerTool(Name = "ltools_staging_delete_file"), Description("删草稿 files/ 下的一个文件。")]
    public static string StagingDeleteFile(
        [Description("草稿 id")] string draftId,
        [Description("相对 files/ 的路径")] string path) =>
        AiToolHandlers.Call("ltools_staging_delete_file", draftId,
            () => AiToolHandlers.StagingDeleteFile(draftId, path));

    [McpServerTool(Name = "ltools_staging_check"), Description(
        "静态校验草稿：返回 ok、revision、issues（path / label / message / severity）。error 必须清零才能运行 / 采纳。")]
    public static string StagingCheck(
        [Description("草稿 id")] string draftId) =>
        AiToolHandlers.Call("ltools_staging_check", draftId, () => AiToolHandlers.StagingCheck(draftId));

    [McpServerTool(Name = "ltools_staging_save"), Description(
        "把当前内容冻结为已保存版本（CAS），并记一份 history（只留最近 3 份）。")]
    public static string StagingSave(
        [Description("草稿 id")] string draftId,
        [Description("期望的当前 revision")] string expectedRevision,
        [Description("可选：这次改了什么")] string? summary = null) =>
        AiToolHandlers.Call("ltools_staging_save", draftId,
            () => AiToolHandlers.StagingSave(draftId, expectedRevision, summary));

    [McpServerTool(Name = "ltools_staging_reset"), Description("回到最近一次保存的版本。")]
    public static string StagingReset(
        [Description("草稿 id")] string draftId) =>
        AiToolHandlers.Call("ltools_staging_reset", draftId, () => AiToolHandlers.StagingReset(draftId));

    [McpServerTool(Name = "ltools_staging_delete"), Description(
        "删除一个草稿（一次一个；沙箱模式下要用户确认）。")]
    public static string StagingDelete(
        [Description("草稿 id")] string draftId) =>
        AiToolHandlers.Call("ltools_staging_delete", draftId, () => AiToolHandlers.StagingDelete(draftId));

    [McpServerTool(Name = "ltools_staging_run"), Description(
        "后台试运行草稿（会真的动键鼠）。已有自动化在跑时返回 busy，不排队。返回 runId。")]
    public static string StagingRun(
        [Description("草稿 id")] string draftId,
        [Description("可选：只从某个顶层步骤路径开始，如 /steps/2")] string? startPath = null,
        [Description("可选：期望的当前 revision")] string? expectedRevision = null) =>
        AiToolHandlers.Call("ltools_staging_run", draftId,
            () => AiToolHandlers.StagingRun(draftId, startPath, expectedRevision));

    [McpServerTool(Name = "ltools_run_output"), Description(
        "按 cursor 分页读试运行轨迹与结果。next 是下一次的 cursor。")]
    public static string RunOutput(
        [Description("runId")] string runId,
        [Description("上次回执的 next，默认 0")] int cursor = 0,
        [Description("本次最多返回多少条，默认 200")] int limit = 200) =>
        AiToolHandlers.Call("ltools_run_output", null, () => AiToolHandlers.RunOutput(runId, cursor, limit));

    [McpServerTool(Name = "ltools_run_stop"), Description("停止一次正在跑的试运行（停止热键不变）。")]
    public static string RunStop(
        [Description("runId")] string runId) =>
        AiToolHandlers.Call("ltools_run_stop", null, () => AiToolHandlers.RunStop(runId));

    [McpServerTool(Name = "ltools_staging_promote"), Description(
        "把草稿采纳为正式动作：mode=new 新建 / mode=replace 覆盖来源动作（来源动作被改过时拒绝）。" +
        "沙箱模式下由 LTools 弹原生确认；full 模式直通。")]
    public static string StagingPromote(
        [Description("草稿 id")] string draftId,
        [Description("new 或 replace")] string mode,
        [Description("可选：期望的当前 revision")] string? expectedRevision = null) =>
        AiToolHandlers.Call("ltools_staging_promote", draftId,
            () => AiToolHandlers.StagingPromote(draftId, mode, expectedRevision));
}

/// <summary>工具层的可预期错误：<see cref="Code"/> 直接进回执。</summary>
internal sealed class AiToolException : Exception
{
    public AiToolException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
