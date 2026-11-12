using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具的实际实现。所有工具共用一个回执形状与审计入口，避免每个工具各写一份 try/catch。
/// </summary>
internal static class AiToolHandlers
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(), new ValueJsonConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>统一的工具调用入口：执行、写审计、把异常翻译成 JSON 回执。</summary>
    public static string Call(string tool, string? draftId, Func<Dictionary<string, object?>> body)
    {
        long started = Environment.TickCount64;
        try
        {
            var payload = body();
            payload["ok"] = true;
            AiAudit.Write(tool, draftId, "ok", Environment.TickCount64 - started);
            return Serialize(payload);
        }
        catch (AiToolException ex)
        {
            AiAudit.Write(tool, draftId, ex.Code, Environment.TickCount64 - started);
            return Error(ex.Code, ex.Message);
        }
        catch (DraftConflictException ex)
        {
            AiAudit.Write(tool, draftId, "conflict", Environment.TickCount64 - started);
            return Error("version_conflict", ex.Message);
        }
        catch (ActionException ex)
        {
            AiAudit.Write(tool, draftId, "invalid", Environment.TickCount64 - started);
            return Error("invalid_action", ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            AiAudit.Write(tool, draftId, "missing", Environment.TickCount64 - started);
            return Error("not_found", ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("Ai", $"工具 {tool} 失败", ex);
            AiAudit.Write(tool, draftId, "error", Environment.TickCount64 - started);
            return Error("internal_error", ex.Message);
        }
    }

    // ============ 读 ============

    public static Dictionary<string, object?> Health()
    {
        var settings = AiSettings.Load();
        return new Dictionary<string, object?>
        {
            ["version"] = typeof(AiTools).Assembly.GetName().Version?.ToString(3) ?? "",
            ["enabled"] = settings.Enabled,
            ["running"] = McpServer.IsRunning,
            ["port"] = settings.Port,
            ["endpoint"] = $"http://127.0.0.1:{settings.Port}/mcp",
            ["allowWrites"] = settings.AllowWrites,
            ["writesAllowed"] = settings.WritesAllowed,
            ["approvalMode"] = settings.ApprovalMode.ToString(),
            ["tokenPresent"] = settings.Token.Length > 0,
            ["draftCount"] = DraftStore.List().Count,
            ["stagingRoot"] = DraftStore.Root,
            ["knowledgeDir"] = KnowledgePack.Dir,
            ["lastError"] = McpServer.LastError ?? settings.LastError,
        };
    }

    public static Dictionary<string, object?> DocsGet(string name)
    {
        name = (name ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0) name = "format";
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["content"] = KnowledgePack.Read(name),
        };
    }

    public static Dictionary<string, object?> UnitsList(string? query)
    {
        query = (query ?? "").Trim();
        var units = StepRegistry.Default.Units
            .Where(u => query.Length == 0
                        || u.Spec.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Category.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || u.Spec.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.Spec.Category, StringComparer.Ordinal)
            .ThenBy(u => u.Spec.Key, StringComparer.OrdinalIgnoreCase)
            .Select(u => new Dictionary<string, object?>
            {
                ["key"] = u.Spec.Key,
                ["name"] = u.Spec.Name,
                ["category"] = u.Spec.Category,
                ["kind"] = u.Spec.Kind.ToString(),
                ["description"] = u.Spec.Description,
                ["inputs"] = u.Spec.Inputs.Count,
                ["outputs"] = u.Spec.Outputs.Count,
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = units.Count,
            ["units"] = units,
        };
    }

    public static Dictionary<string, object?> UnitsGet(string key)
    {
        key = (key ?? "").Trim();
        if (!StepRegistry.Default.TryGet(key, out var unit))
            throw new AiToolException("unknown_unit", $"未知的功能单元：{key}（先用 ltools_units_list 查）");

        var spec = unit.Spec;
        var payload = new Dictionary<string, object?>
        {
            ["key"] = spec.Key,
            ["name"] = spec.Name,
            ["category"] = spec.Category,
            ["kind"] = spec.Kind.ToString(),
            ["body"] = spec.Body.ToString(),
            ["dynamicInputs"] = spec.DynamicInputs,
            ["description"] = spec.Description,
            ["inputs"] = spec.Inputs.Select(input => new Dictionary<string, object?>
            {
                ["key"] = input.Key,
                ["label"] = input.Label,
                ["type"] = input.Type?.ToString(),
                ["editor"] = input.Editor.ToString(),
                ["required"] = input.Required,
                ["default"] = input.Default,
                ["visibleWhen"] = input.VisibleWhen,
                ["options"] = input.Options,
                ["labels"] = input.Labels,
                ["raw"] = input.Raw,
                ["description"] = input.Description,
                ["hint"] = InputHint.Of(input),
            }).ToList(),
            ["outputs"] = spec.Outputs.Select(output => new Dictionary<string, object?>
            {
                ["key"] = output.Key,
                ["label"] = output.Label,
                ["type"] = output.Type?.ToString(),
                ["defaultVariable"] = OutputNaming.Suggest(spec.Key, output),
                ["description"] = output.Description,
            }).ToList(),
        };

        // 「调用子动作」的参数名来自用户的子动作库（参数名 = 输入变量名），
        // 单元声明里只有一个 subAction，不一起给出签名就只能瞎猜。
        if (string.Equals(spec.Key, "sub.run", StringComparison.OrdinalIgnoreCase))
            payload["subActions"] = SubActionStore.LoadAll().Select(Signature).ToList();

        return payload;
    }

    public static Dictionary<string, object?> ActionsList(string? query, int limit)
    {
        query = (query ?? "").Trim();
        limit = Math.Clamp(limit, 1, 500);

        var rows = AiUi.Run(() => ActionRegistry.All
            .Where(a => query.Length == 0
                        || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || a.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(a => new Dictionary<string, object?>
            {
                ["id"] = a.Id,
                ["name"] = a.Name,
                ["icon"] = a.Icon,
                ["type"] = MacroAction.MacroIdOf(a) != null ? "macro" : "action",
                ["stepCount"] = a.Program?.Steps.Count,
            })
            .ToList());

        return new Dictionary<string, object?>
        {
            ["count"] = rows.Count,
            ["actions"] = rows,
        };
    }

    public static Dictionary<string, object?> ActionsGet(string id)
    {
        id = (id ?? "").Trim();
        var action = AiUi.Run(() => ActionRegistry.FindById(id))
            ?? throw new AiToolException("not_found", $"正式动作不存在：{id}");

        var program = AiUi.Run(() => action.Program?.Clone());
        return new Dictionary<string, object?>
        {
            ["id"] = action.Id,
            ["name"] = action.Name,
            ["icon"] = action.Icon,
            ["type"] = MacroAction.MacroIdOf(action) != null ? "macro" : "action",
            ["program"] = program,
        };
    }

    public static Dictionary<string, object?> SubActionsList(string? query, int limit)
    {
        query = (query ?? "").Trim();
        limit = Math.Clamp(limit, 1, 200);

        var rows = SubActionStore.LoadAll()
            .Where(d => query.Length == 0
                        || d.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (d.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || d.Variables.Any(v => (v.Key ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .Select(d =>
            {
                var row = Signature(d);
                row["icon"] = d.Icon;
                row["description"] = d.Description;
                row["stepCount"] = d.Steps.Count;
                return row;
            })
            .ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = rows.Count,
            ["subActions"] = rows,
        };
    }

    public static Dictionary<string, object?> SubActionsGet(string id)
    {
        id = (id ?? "").Trim();
        var def = SubActionStore.Load(id)
            ?? throw new AiToolException("not_found", $"子动作不存在：{id}");

        return new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["icon"] = def.Icon,
            ["description"] = def.Description,
            ["revision"] = SubActionStore.Revision(def.Id),
            ["program"] = def.Clone(),
        };
    }

    // ============ 子动作写入（AI） ============

    /// <summary>
    /// 只校验不落盘：AI 写完先跑一遍，拿到和编辑器「校验」同一套问题清单。
    /// </summary>
    public static Dictionary<string, object?> SubActionsCheck(JsonElement program)
    {
        var def = ParseSubAction(program);
        var result = ActionValidator.ValidateSubAction(def, SubActionStore.Revision(def.Id));
        return CheckPayload(result, result.Ok, parseError: null);
    }

    /// <summary>
    /// 整份写入一个子动作（新建或覆盖）。两道闸与 <c>staging_promote</c> 一致：
    /// <see cref="RequireWrites"/> + <see cref="RequireConfirm"/>；写前跑静态校验，有 error 就拒绝。
    /// </summary>
    public static Dictionary<string, object?> SubActionsWrite(JsonElement program, string? expectedRevision)
    {
        RequireWrites();

        var def = ParseSubAction(program);
        def.Id = (def.Id ?? "").Trim();
        def.Name = (def.Name ?? "").Trim();
        if (def.Id.Length == 0)
            throw new AiToolException("invalid_program", "子动作缺少 id（subactions/<id>.json 的文件名就是 id）");
        if (def.Name.Length == 0)
            throw new AiToolException("invalid_program", "子动作缺少 name");

        string? current = SubActionStore.Revision(def.Id);
        bool exists = current != null;

        var validation = ActionValidator.ValidateSubAction(def);
        if (!validation.Ok)
        {
            var errors = validation.Issues.Where(i => i.Severity == IssueSeverity.error).Take(5).ToList();
            throw new AiToolException("check_failed",
                $"静态校验有 {validation.Errors} 个 error，先修好再写入：{string.Join("；", errors.Select(i => i.Message))}");
        }

        // CAS：给了期望 revision 就必须对得上（文件内容 sha256，谁写的都能重算）。
        if (!string.IsNullOrWhiteSpace(expectedRevision) &&
            !string.Equals(expectedRevision.Trim(), current ?? "", StringComparison.OrdinalIgnoreCase))
            throw new DraftConflictException(exists
                ? "子动作已被改动（期望的 revision 与当前不一致），先重新读取"
                : "子动作不存在（或已被删除），先重新读取");

        RequireConfirm(exists ? "修改子动作" : "新建子动作",
            exists
                ? $"AI 要覆盖子动作「{def.Name}」（{def.Id}），调用它的动作会一起变。"
                : $"AI 要新建子动作「{def.Name}」（{def.Id}），共 {def.Steps.Count} 步。");

            SubActionStore.Save(def, createNew: !exists);

        return new Dictionary<string, object?>
        {
            ["id"] = def.Id,
            ["name"] = def.Name,
            ["created"] = !exists,
            ["steps"] = def.Steps.Count,
            ["inputs"] = def.Variables.Count(v => v.Direction == VariableDirection.input),
            ["outputs"] = def.Variables.Count(v => v.Direction == VariableDirection.output),
            ["revision"] = SubActionStore.Revision(def.Id),
            ["warnings"] = validation.Warnings,
        };
    }

    public static Dictionary<string, object?> SubActionsDelete(string id)
    {
        RequireWrites();

        id = (id ?? "").Trim();
        var def = SubActionStore.Load(id) ?? throw new AiToolException("not_found", $"子动作不存在：{id}");
        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;

        var refs = SubActionStore.FindReferences(id);
        string detail = refs.Count == 0
            ? $"确定删除子动作「{name}」（{id}）？目前没有动作在调用它。此操作不可恢复。"
            : $"确定删除子动作「{name}」（{id}）？下面 {refs.Count} 处调用之后会报「找不到子动作」：\n"
              + string.Join("\n", refs.Take(8).Select(r => $"· {r.ActionName} · {r.Where}"));

        RequireConfirm("删除子动作", detail);
        SubActionStore.Delete(id);

        return new Dictionary<string, object?>
        {
            ["deleted"] = true,
            ["id"] = id,
            ["name"] = name,
            ["references"] = refs.Count,
        };
    }

    /// <summary>
    /// 子动作的签名（id / 名称 / 输入 / 输出）。给 <c>sub.run</c> 的单元规格一起返回，
    /// agent 才知道该传哪些参数名——这些名字不在单元声明里，是每个子动作自己的变量。
    /// </summary>
    private static Dictionary<string, object?> Signature(SubActionDef def) => new()
    {
        ["id"] = def.Id,
        ["name"] = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name,
        ["inputs"] = def.Variables
            .Where(v => v.Direction == VariableDirection.input)
            .Select(v => new Dictionary<string, object?>
            {
                ["key"] = v.Key,
                ["type"] = v.Type.ToString(),
                ["required"] = IsEmptyValue(v.Seed()),
                ["default"] = v.Seed(),
                ["description"] = v.Label,
            })
            .ToList(),
        ["outputs"] = def.Variables
            .Where(v => v.Direction == VariableDirection.output)
            .Select(v => new Dictionary<string, object?>
            {
                ["key"] = v.Key,
                ["type"] = v.Type.ToString(),
                ["description"] = v.Label,
            })
            .ToList(),
    };

    private static bool IsEmptyValue(Value value) => value.Kind switch
    {
        ValueKind.text => value.AsText().Length == 0,
        ValueKind.list => value.Items.Count == 0,
        ValueKind.@object => value.Members.Count == 0,
        _ => false,
    };

    public static Dictionary<string, object?> StagingList()
    {
        var drafts = DraftStore.List().Select(meta => new Dictionary<string, object?>
        {
            ["id"] = meta.Id,
            ["title"] = meta.Title,
            ["status"] = meta.Status.ToString(),
            ["origin"] = meta.Origin,
            ["createdAt"] = meta.CreatedAt,
            ["updatedAt"] = meta.UpdatedAt,
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
            ["baseActionId"] = meta.BaseActionId,
            ["promotedActionId"] = meta.PromotedActionId,
            ["summary"] = meta.Summary,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["count"] = drafts.Count,
            ["drafts"] = drafts,
        };
    }

    // ============ 草稿 ============

    public static Dictionary<string, object?> StagingCreate(string title, string? description, string? copyFromActionId)
    {
        RequireWrites();
        var meta = AiUi.Run(() => DraftStore.Create(title, description, copyFromActionId, origin: "mcp"));
        return new Dictionary<string, object?>
        {
            ["draftId"] = meta.Id,
            ["title"] = meta.Title,
            ["revision"] = DraftStore.ComputeRevision(meta.Id),
            ["directory"] = Path.GetFullPath(DraftStore.DirectoryOf(meta.Id)),
            ["editFile"] = DraftStore.ProgramPath(meta.Id),
            ["filesDir"] = Path.GetFullPath(DraftStore.FilesDir(meta.Id)),
            ["baseActionId"] = meta.BaseActionId,
        };
    }

    public static Dictionary<string, object?> StagingOpen(string draftId)
    {
        var snapshot = DraftStore.Read(draftId);
        return new Dictionary<string, object?>
        {
            ["draftId"] = snapshot.Meta.Id,
            ["title"] = snapshot.Meta.Title,
            ["status"] = snapshot.Meta.Status.ToString(),
            ["revision"] = snapshot.Revision,
            ["savedRevision"] = snapshot.Meta.SavedRevision,
            ["editVersion"] = snapshot.Meta.EditVersion,
            ["directory"] = snapshot.Directory,
            ["programPath"] = DraftStore.ProgramPath(snapshot.Meta.Id),
            ["filesDir"] = Path.GetFullPath(DraftStore.FilesDir(snapshot.Meta.Id)),
            ["baseActionId"] = snapshot.Meta.BaseActionId,
            ["promotedActionId"] = snapshot.Meta.PromotedActionId,
            ["summary"] = snapshot.Meta.Summary,
            ["files"] = snapshot.Files.Select(f => new Dictionary<string, object?>
            {
                ["name"] = f.Name,
                ["bytes"] = f.Bytes,
            }).ToList(),
            ["program"] = snapshot.Program,
            ["error"] = snapshot.Error,
        };
    }

    public static Dictionary<string, object?> StagingWrite(string draftId, JsonElement program, string? expectedRevision)
    {
        RequireWrites();
        ActionProgram parsed = ParseProgram(program);
        string revision = DraftStore.WriteProgram(draftId, parsed, expectedRevision);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["revision"] = revision,
            ["name"] = parsed.Name,
            ["steps"] = parsed.Steps.Count,
        };
    }

    public static Dictionary<string, object?> StagingReadFile(string draftId, string path)
    {
        byte[] bytes = DraftStore.ReadFile(draftId, path);
        if (bytes.Length > 16 * 1024 * 1024)
            throw new AiToolException("too_large", $"文件太大（{bytes.Length} 字节），不要通过 MCP 读它");

        return new Dictionary<string, object?>
        {
            ["name"] = path,
            ["bytes"] = bytes.Length,
            ["base64"] = Convert.ToBase64String(bytes),
        };
    }

    public static Dictionary<string, object?> StagingWriteFile(string draftId, string path, string contentBase64)
    {
        RequireWrites();
        byte[] bytes;
        try { bytes = Convert.FromBase64String((contentBase64 ?? "").Trim()); }
        catch (FormatException) { throw new AiToolException("invalid_base64", "contentBase64 不是合法的 base64"); }

        if (bytes.Length > 8 * 1024 * 1024)
            throw new AiToolException("too_large", $"文件太大（{bytes.Length} 字节），单文件上限 8 MB");

        string full = DraftStore.WriteFile(draftId, path, bytes);
        return new Dictionary<string, object?>
        {
            ["name"] = path,
            ["bytes"] = bytes.Length,
            ["path"] = full,
            ["revision"] = DraftStore.ComputeRevision(draftId),
        };
    }

    public static Dictionary<string, object?> StagingDeleteFile(string draftId, string path)
    {
        RequireWrites();
        bool deleted = DraftStore.DeleteFile(draftId, path);
        return new Dictionary<string, object?>
        {
            ["deleted"] = deleted,
            ["revision"] = DraftStore.ComputeRevision(draftId),
        };
    }

    public static Dictionary<string, object?> StagingCheck(string draftId)
    {
        var snapshot = DraftStore.Read(draftId);
        var result = ActionValidator.Validate(snapshot.Program, snapshot.Revision, DraftStore.FilesDir(draftId),
        draftCheck: true);

        return CheckPayload(result, result.Ok && snapshot.Program != null, snapshot.Error);
    }

    public static Dictionary<string, object?> StagingSave(string draftId, string expectedRevision, string? summary)
    {
        RequireWrites();
        var meta = DraftStore.Save(draftId, expectedRevision, summary);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
            ["status"] = meta.Status.ToString(),
        };
    }

    public static Dictionary<string, object?> StagingReset(string draftId)
    {
        RequireWrites();
        var meta = DraftStore.Reset(draftId);
        return new Dictionary<string, object?>
        {
            ["draftId"] = draftId,
            ["revision"] = DraftStore.ComputeRevision(draftId),
            ["savedRevision"] = meta.SavedRevision,
            ["editVersion"] = meta.EditVersion,
        };
    }

    public static Dictionary<string, object?> StagingDelete(string draftId)
    {
        RequireWrites();
        var meta = DraftStore.Find(draftId) ?? throw new AiToolException("not_found", $"草稿不存在：{draftId}");
        RequireConfirm("删除草稿", $"确定删除草稿「{meta.Title ?? meta.Id}」（{meta.Id}）？此操作不可恢复。");
        DraftStore.Delete(draftId);
        return new Dictionary<string, object?> { ["deleted"] = true, ["draftId"] = draftId };
    }

    public static Dictionary<string, object?> StagingRun(string draftId, string? startPath, string? expectedRevision)
    {
        var snapshot = DraftStore.Read(draftId);
        if (snapshot.Program == null)
            throw new AiToolException("invalid_program", snapshot.Error ?? "草稿内容不完整，无法试运行");

        if (!string.IsNullOrWhiteSpace(expectedRevision) && snapshot.Revision != null &&
            !string.Equals(expectedRevision.Trim(), snapshot.Revision, StringComparison.OrdinalIgnoreCase))
            throw new DraftConflictException("草稿内容已被改动（期望的 revision 与当前不一致），先重新读取");

        var validation = ActionValidator.Validate(snapshot.Program, snapshot.Revision, DraftStore.FilesDir(draftId),
        draftCheck: true);
        if (!validation.Ok)
        {
            var errors = validation.Issues.Where(i => i.Severity == IssueSeverity.error).Take(5).ToList();
            string detail = string.Join("；", errors.Select(i => $"{i.Label}{i.Message}"));
            throw new AiToolException("check_failed", $"静态校验有 {validation.Errors} 个 error，先修好再试运行：{detail}");
        }

        string name = string.IsNullOrWhiteSpace(snapshot.Program.Name)
            ? snapshot.Meta.Title ?? "AI 草稿"
            : snapshot.Program.Name;

        // 草稿里的图写在 files/x.png：把草稿目录当资源根，试运行就不用写绝对路径。
        var run = AiRuns.Start(draftId, name, snapshot.Program.Clone(), startPath,
            DraftStore.FilesDir(draftId));
        if (run == null)
            throw new AiToolException("busy", "已有自动化正在运行，等它结束再试（不排队，避免两套键鼠注入打架）");

        return new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["draftId"] = draftId,
            ["state"] = run.State,
            ["revision"] = snapshot.Revision,
        };
    }

    public static Dictionary<string, object?> RunOutput(string runId, int cursor, int limit)
    {
        var (run, items, next, hasMore) = AiRuns.Read(runId, cursor, limit);
        if (run == null) throw new AiToolException("not_found", $"试运行记录不存在：{runId}");

        return new Dictionary<string, object?>
        {
            ["runId"] = run.Id,
            ["draftId"] = run.DraftId,
            ["name"] = run.Name,
            ["state"] = run.State,
            ["finished"] = run.Finished,
            ["steps"] = run.Steps,
            ["elapsedMs"] = Math.Round(run.ElapsedMs),
            ["error"] = run.Error,
            ["traces"] = items.Select(item => new Dictionary<string, object?>
            {
                ["seq"] = item.Seq,
                ["path"] = item.Trace.Path,
                ["label"] = item.Trace.Label,
                ["depth"] = item.Trace.Depth,
                ["unit"] = item.Trace.UnitKey,
                ["unitName"] = item.Trace.UnitName,
                ["status"] = item.Trace.Status.ToString(),
                ["elapsedMs"] = item.Trace.ElapsedMs,
                ["error"] = item.Trace.Error,
                ["outputs"] = item.Trace.Outputs,
            }).ToList(),
            ["next"] = next,
            ["hasMore"] = hasMore,
        };
    }

    public static Dictionary<string, object?> RunStop(string runId)
    {
        bool stopped = AiRuns.Stop(runId);
        var run = AiRuns.Find(runId) ?? throw new AiToolException("not_found", $"试运行记录不存在：{runId}");
        return new Dictionary<string, object?>
        {
            ["stopped"] = stopped,
            ["state"] = run.State,
        };
    }

    public static Dictionary<string, object?> StagingPromote(string draftId, string mode, string? expectedRevision)
    {
        RequireWrites();
        mode = (mode ?? "").Trim().ToLowerInvariant();
        if (mode is not ("new" or "replace"))
            throw new AiToolException("invalid_mode", "mode 只能是 new 或 replace");

        var meta = DraftStore.Find(draftId) ?? throw new AiToolException("not_found", $"草稿不存在：{draftId}");
        RequireConfirm(
            mode == "replace" ? "覆盖正式动作" : "采纳为新动作",
            $"把草稿「{meta.Title ?? meta.Id}」（{meta.Id}）{(mode == "replace" ? "覆盖来源正式动作" : "采纳为一个新的正式动作")}？");

        var result = DraftPromoter.Promote(draftId, mode, expectedRevision);
        return new Dictionary<string, object?>
        {
            ["actionId"] = result.ActionId,
            ["created"] = result.Created,
            ["importedImages"] = result.ImportedImages,
            ["draftId"] = draftId,
        };
    }

    // ============ 内部 ============

    private static ActionProgram ParseProgram(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                string text = element.GetString() ?? "";
                return JsonSerializer.Deserialize<ActionProgram>(text, DraftStore.Json)
                       ?? throw new AiToolException("invalid_program", "program 是空的");
            }

            if (element.ValueKind != JsonValueKind.Object)
                throw new AiToolException("invalid_program", "program 必须是 JSON 对象");

            return element.Deserialize<ActionProgram>(DraftStore.Json)
                   ?? throw new AiToolException("invalid_program", "program 解析结果为空");
        }
        catch (JsonException ex)
        {
            throw new AiToolException("invalid_program", "program 解析失败：" + ex.Message);
        }
    }

    private static SubActionDef ParseSubAction(JsonElement element)
    {
        try
        {
            if (element.ValueKind == JsonValueKind.String)
                return SubActionStore.Deserialize(element.GetString() ?? "");

            if (element.ValueKind != JsonValueKind.Object)
                throw new AiToolException("invalid_program", "program 必须是 JSON 对象（子动作定义）");

            return element.Deserialize<SubActionDef>(SubActionStore.JsonOptions)
                   ?? throw new AiToolException("invalid_program", "program 解析结果为空");
        }
        catch (JsonException ex)
        {
            throw new AiToolException("invalid_program", "program 解析失败：" + ex.Message);
        }
    }

    /// <summary>校验回执（草稿 check 与子动作 check 共用同一形状）。</summary>
    private static Dictionary<string, object?> CheckPayload(ActionValidationResult result, bool ok,
        string? parseError) => new()
        {
            ["ok"] = ok,
            ["revision"] = result.Revision,
            ["errors"] = result.Errors,
            ["warnings"] = result.Warnings,
            ["issues"] = result.Issues.Select(issue => new Dictionary<string, object?>
            {
                ["path"] = issue.Path,
                ["label"] = issue.Label,
                ["unit"] = issue.Unit,
                ["key"] = issue.Key,
                ["message"] = issue.Message,
                ["severity"] = issue.Severity.ToString(),
            }).ToList(),
            ["parseError"] = parseError,
        };

    private static void RequireWrites()
    {
        var settings = AiSettings.Load();
        if (!settings.WritesAllowed)
        {
            throw new AiToolException("writes_disabled",
                "写入没开启：让用户在 LTools 设置 → AI 里打开「允许写入」，并把审批模式从「只读」改掉");
        }
    }

    private static void RequireConfirm(string action, string detail)
    {
        var settings = AiSettings.Load();
        if (settings.ApprovalMode == AiApprovalMode.full) return;
        if (!AiUi.Confirm(action, detail))
            throw new AiToolException("denied", "用户拒绝了这次操作");
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value, JsonOpt);

    private static string Error(string code, string message) =>
        Serialize(new Dictionary<string, object?>
        {
            ["ok"] = false,
            ["error"] = code,
            ["message"] = message,
        });
}
