# 参数表单规格与重构方案（2026-11-13）

> 本文记录用户对动作参数表单的重新设计决策、约束规则与本次重构方案，是
> `docs/ACTION_SYSTEM.md` 里「参数表单」一节的设计规格。术语以 `CONTEXT.md` 为准，
> 实现约束落在 `AGENTS.md`，两者都不在这里另写一份。

## 1. 背景

上一轮把**所有**非开关类参数统一成「常驻文本框 + 辅助编辑器 + ▾ 来源」，枚举也从纯下拉
改成可编辑下拉，并给枚举参数打通了变量 / 内联取值。结果是参数表单变宽、变吵，且
「驱动键可能是变量」与「只显示当前操作支持的参数」互相矛盾：为了让变量驱动时用户还能填到
受控行，UI 只好把所有受控行全部显示出来（上一轮的 `forUi` 分支）。该实现已整体回退到
v2.6.6，本轮从干净基线重新做。

动作的**数据模型不变**：`ActionProgram`（变量表 + 步骤树）→ `Step`（`unit` / `in` / `out` /
`body` / `then` / `else`）→ `StepArg`（字面量 / 变量 / 内联取值三态）。参数长什么样完全由
代码里的 `UnitSpec.Inputs[]` / `InputSpec` 声明派生，动作 JSON 里没有任何「输入框 / 下拉 /
模式」字段。所以本轮是**UI 渲染层 + 可见性声明**的重构，不是数据格式升级。

## 2. 决策与约束（用户拍板，不得自行放宽）

| # | 决策 | 约束 / 规则 |
|---|---|---|
| 1 | 基线 | 丢弃上一轮 14 个未提交文件，回退到 v2.6.6 后重新做；不写迁移器、不认旧字段、不留别名 |
| 2 | 枚举边界 | **只有 `InputEditor.enumList` 用纯下拉**：不可编辑、不挂文本框、不挂「▾ 来源」、值只能是 `Options` 里的英文键。`key` / `macro` / `sub.run.subAction` 的候选会变或不是有穷集，一律走「文本框 + ▾」 |
| 3 | 值三态 | **保留 `StepArg` 三态**，只统一外观；不动 `StepArg`、`StepArgConverter`、`ActionEngine.ResolveArgs`、动作单元实现。整串 `${名}` 仍存成 `{"var":…}` 以保留 list / object 类型 |
| 4 | 图片缩略图 | 保留，但放在**输入行下方单独一行**；输入行本身始终严格两列 |
| 5 | 可见性 | **升级为「选项声明参数集合」**：由枚举选项正向声明它启用哪些参数，用户只能看到当前操作支持的输入框 |

由决策 2 直接推出的硬规则：

- 枚举参数永远是确定字面量，不能是变量 / 内联取值，因此 **UI 与运行期 / 静态校验用同一套
  可见性判断**，不需要也不允许再出现「变量驱动时全显示」的分支。
- 本机实证：7 个动作 / 257 个步骤 / 111 个非字面量参数里，没有任何枚举驱动键使用变量或
  内联取值，用的全是 `a` / `value` / `left` / `list` / `path` / `text` / `items` 这类普通值参数。

由决策 5 直接推出的硬规则：

- 「哪些参数随哪个操作显示」写在**驱动键（枚举）上**，不再逐个参数写 `key == 'value'`。
- 保存时被隐藏的参数照旧由 `SyncStepArgs` 丢掉，运行期照旧不把隐藏的必填项算成缺参数；
  两处必须继续调同一个 `VisibleWhen.IsVisible`。

## 3. 目标形态

一行参数 = `标签 118px` + 控件，控件按形态分：

| 参数形态 | 控件 | 说明 |
|---|---|---|
| `enumList`（53 个） | 纯下拉 `ComboBox` | 中文标签 → 存英文键；未知旧值原样保留并标「未知选项」，不静默落到第一项 |
| 其它参数 | `文本输入框 *` + `▾ 小按钮` | 两列；`Box` 常驻，能直接写字面量 / `${变量}` / 文本模板 |
| `boolean` / `flags` | 纯勾选 / 勾选组 | 不算输入框，保持现状 |
| `code` | 等宽多行 + 「检查语法」 | 保持现状，`Raw: true`，不挂「▾ 来源」 |
| `image` | 上方的两列 + 下方一行缩略图预览 | 唯一多一行的例外；预览只读，不替代文本输入框 |
| `key` / `macro` / `subAction` | 两列，候选收进「▾ 来源」 | 「从列表选…」进弹层，不在行内再摆一个下拉 |

「▾ 来源」弹层（`ValueField.SourceButton`）保持现有能力：变量（名字 / 类型 / 谁写进来的）、
选择文件 / 文件夹 / 图片 / 窗口 / 进程、屏幕区域 / 取点 / 取色、录制按键、速记分类 / 时间
格式 / 文件筛选 / 分隔符、剪贴板记录、取值动作单元（二级页）、清空，输入即筛选。
**只列这个参数适用的项**，由 `InputSpec.Pickers` / `PickerKind` 声明，默认按 `InputEditor` 推。

`region` 参数不再有胶囊面板，框选入口只在「▾ 来源 → 屏幕区域…」；框完仍由
`StepEditorDialog.ApplyPickedRegion` 先写 `rect` 文本、再把配套枚举切到 `rect`。

## 4. 可见性：选项可见参数

### 4.1 声明

`InputSpec` 新增 `OptionParams`（`IReadOnlyDictionary<string, IReadOnlyList<string>>?`），
写在**枚举驱动参数**上：键 = 选项值（英文键），值 = 该选项启用哪些参数键。

```csharp
new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
    Required: true, Default: Value.FromText("click"), Options: Operations,
    OptionParams: new()
    {
        ["click"] = new[] { "x", "y" },
        ["drag"]  = new[] { "x", "y", "dx", "dy", "button", "steps", "stepMs" },
        ["down"]  = new[] { "button" },
        // …
    })
```

一个动作单元最多一个带 `OptionParams` 的驱动键。没被任何选项列出的参数、以及驱动键自己，
不归它管（按无规则处理 = 始终可见）。`Advanced` 只决定「出现时放主区还是折叠区」，与可见性
正交；`DynamicInputs`（子动作按名参数）不参与。

### 4.2 求值口径（唯一实现仍在 `Core/Actions/VisibleWhen`）

`IsVisible(input, args, spec)` 按两步 AND 求值，参数对话框、运行期必填判断、静态校验、
保存校验全部调它：

1. **选项门槛**：在 `spec.Inputs` 里找带 `OptionParams` 的驱动键 → 取它当前值（缺失时回落
   `Default`）→ 查该选项启用的参数集合。若 `input.Key` 被这个驱动键管辖（出现在任一选项的
   集合里）却**不在当前选项的集合里**，返回 `false`。
2. **附加条件**：`input.VisibleWhen` 仍按原规则求值（`key == 'value'`、`||`、不带引号的
   `true` / `false`），并新增 `key in ('a','b',…)` 写法。两项都通过才可见。

两种兜底都按「可见」处理（宁可多显示一行，也不把表单藏空）：

- **规则写不认识**（`VisibleWhen` 解析不出来）：不藏这一行；
- **当前值是合法选项、但没被列进 `OptionParams`**：该选项不启用任何受管参数（等价于空集合）。
  例如 `window.operate` 只给 `move` / `resize` 列了 `x` / `y` / `w` / `h`，切到 `maximize`
  时这几个参数收起；
- **未知旧值 / 空值**（值不在 `Options` 里，如手改配置或旧键名）：受管参数按可见处理，
  不因为认不出值就整片消失。

### 4.3 例外：布尔 / 跨键条件

旧 `VisibleWhen` 里绝大多数驱动键是枚举（`operation` / `mode` / `region` / `source` /
`method`），迁到 `OptionParams`；剩下 5 处（4 类）保留 `VisibleWhen` 作为附加条件：

- `input.mouse` 的 `steps` / `stepMs`：`gradual == true || operation == 'drag'`
- `file.selectedFiles` 的 `waitMs`：`useClipboardFallback == true`（高级参数）
- 程序 / 应用类动作单元的 `keepWindow`：`capture == false`
- `flow.if` / `flow.while` 的 `right`：由 `Compare.BinaryOps` 生成，保持 `VisibleWhen`

`flow.if` 的 `right` 仍由 `Compare.Ops` / `Compare.BinaryOps` 两个数组一起决定，不迁到
`OptionParams`（它同时驱动摘要与静态校验，单独迁会拆散同一份语义）。

### 4.4 声明校验

`UnitSpec.Validate()` 在注册时校验：

- `OptionParams` 的键必须都在该参数的 `Options` 里；
- 值里出现的参数键必须都在 `spec.Inputs` 里（拼错立刻报错，不要等运行时「参数永远不显示」）；
- 同一个动作单元不允许两个驱动键都写 `OptionParams`；
- `OptionParams` 只允许写在 `enumList` 参数上。

## 5. 三态在两列里的表达

`StepArg` 三态不变，只是不再用面板替换文本输入框：

| 态 | 文本框 | 「▾ 来源」里 |
|---|---|---|
| 字面量 | 直接写文本，可含 `${变量}` 模板 | 插入变量 / 各类选择器 / 清空 |
| 变量 | 显示 `${名}`，整串存成 `{"var":…}`（保留 list / object 类型） | 换变量 / 切回文本 / 清空 |
| 内联取值 | 显示只读描述（如「取值：截图找图」） | 「改参数…」（同一参数表单递归编辑）/ 切回文本 / 清空 |

在文本框里写字 = 切回字面量（放弃变量态）；内联态下文本框只读，**点一下文本框**或选 ▾ 里的
「切回文本」就清掉取值动作单元、回到可写字状态。状态行 `StatusText` 仍是唯一说明处：
「该填什么」/「来自变量 X（类型 · 第 2 步写）」/「取值：… 还缺必填参数」/「必填：这一项还没填」。
`AddRow` 不要在 `ValueField` 外面再挂一行 `InputHint`。

## 6. 非目标

- 不改动作 JSON 格式、不写迁移器、不为旧字段留别名；
- 不动 `StepArg` 三态、`ActionEngine.ResolveArgs`、`StepRegistry`、任何动作单元的 `Execute`；
- 不给枚举参数加「用变量 / 取值动作单元动态决定操作」的能力（与决策 5 冲突）；
- 不把 `Advanced` 折叠并进可见性；
- 不改「▾ 来源」弹层的取值方式清单，只把 `key` / `macro` / `subAction` 的候选收进来。

## 7. 实施顺序

1. 回退 v2.6.6（已完成）；
2. 写本文档 + 同步 `CONTEXT.md` / `AGENTS.md` / `docs/ACTION_SYSTEM.md` / `CHANGELOG.md`；
3. 数据模型层：`InputSpec.OptionParams`、`UnitSpec.Validate()`、`VisibleWhen.IsVisible()`；
4. 把 53 条枚举驱动的 `VisibleWhen` 声明迁到各自驱动键的 `OptionParams`；
5. `ValueField` 两列化：`Box` 常驻，删区域胶囊 / 取值卡片，图片缩略图移到输入行下方；
6. `StepEditorDialog` 去掉 `key` / `macro` / `sub.run.subAction` 特例，统一走 `ValueField`；
   `PickerKind` 加「从列表选…」，让候选进弹层；
7. `InputHint` 文案、`README.md`、冒烟工程重写；
8. Debug / Release 构建 0 警、冒烟全绿、真机点代表动作单元。

## 8. 验收

- **构建**：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 各 0 错 0 警；
- **冒烟**（仓库外无头工程）：`OptionParams` 声明校验、两步可见性求值（枚举命中 / 不命中 /
  boolean 附加条件 / `in` 语法）、`enumList` 是纯下拉且无文本框、非枚举参数是两列且 `Box`
  常驻、三态切换与 round-trip、图片缩略图在输入行下方、框选区域后切 `rect` 并保存；
- **真机**：`input.mouse`（切操作时参数行跟着变）、`screen.capture`（区域 + 框选）、图片类
  参数（缩略图）、`input.sendKeys`（按键候选）、`input.macro`（宏候选）、`sub.run`（子动作候选
  与动态参数表）。

## 9. 风险

`OptionParams` 是本轮唯一动 `InputSpec` 的地方，会牵动 `OptionLabels`、`StepSummary`、
`ActionValidator`、AI 知识包 `KnowledgePack` / `units_list`，加上 53 条声明改写，工作量大于
「三列改两列」。上一轮的 `value-fallback-smoke`（110 项）是围绕「文本兜底」写的，需整体重写。
