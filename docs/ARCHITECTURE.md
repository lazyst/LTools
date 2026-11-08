# 架构与数据布局（分阶段改造方案）

> 目标：把「一份大配置 + 共享资源池 + 巨型窗口」改成**按域存放、按动作归属、可单独备份与回收**的结构。
> 本文是执行清单：每个阶段都能独立构建、独立验收、独立回滚。

## 0. 不变量（改造期间始终成立）

1. **动作只能经 `ConfigStore` 改**，写入仍走「读磁盘 → 改 → 写回」，不引入第二套内存真源。
2. **每个持久化文件只有一个属主**：谁能写它、写哪些字段，在本文第 2 节列表里唯一。
3. **所有 JSON 带 `schemaVersion`**；升级只向前迁移，旧文件改名保留，不删除。
4. **路径解析只有一个入口**：数据根内相对路径走 `AppPaths` / `ImageLibrary`，不在各处手拼。
5. **引用型资源按引用回收**（宏、模板图、悬浮项、面板槽位），删除动作后立刻清理。
6. **构建 0 错 0 警**，每个阶段真机走一遍相关路径再提交。

## 1. 现状（改造前）

数据根（dev = 仓库根，prod = exe 同级）：

```
LTools.json              动作 + 菜单组 + 面板 + 悬浮 + 宏元数据 + 日志 + 终端路径 + UI 偏好
LTools.example.json      模板（随包分发）
macros/<id>.json         宏事件
subactions/<id>.json     全局子动作（已经是一文件一个）
resources/images/        模板图 + 运行期截图（按 shot- 前缀 + 开始时间界线回收）
resources/user/          用户素材（永不自动删）
staging/<id>/            AI 草稿（program.json + files/）
ai/                      令牌 / 审计 / 知识包
速记/  logs/  ocr-models/
```

问题：

| 问题 | 影响 |
| --- | --- |
| `LTools.json` 一个文件装七种域，15 处调用点各自「整份 load → 改 → 保存」 | 改一个导航页会重写全部动作；写冲突与写放大都集中在这一个文件 |
| 坏一处全丢；diff 不可读 | 无法按域恢复、评审、分享 |
| 模板图与运行期截图混在 `resources/images` | 回收靠文件名前缀 + 时间界线，规则脆弱 |
| 备份清单硬编码（LTools.json / resources / 速记 / macros / staging） | 新增目录不会自动进备份 |
| `SuperPanel` 1458 行、`ActionEditorWindow` 1318 行、`ConfigHelper` 1277 行、`PanelManageView` 1227 行 | 改一处要在大文件里翻，评审与 AI 定位都慢 |
| 迁移逻辑分散（ini / LegacyType / PanelConfig / FlowMigration） | 没有统一 schema 版本，不可回滚 |

已经做对的一半：`subactions/` 是一子动作一文件、`staging/<id>/` 是一草稿一目录。
动作目录化只是把同一套做法推进到动作本身。

## 2. 目标布局

```
configs/
  settings.json      全局行为：CapsLock 开关、终端路径、鼠标速度、运行提示、提示位置、日志
  ui.json            界面状态：导航页、展开的分组、最近使用、OCR 偏好、编辑器折叠态
  menus.json         快捷菜单组（10 个槽位，项为动作 Id 引用）
  panel.json         超级面板：开关 / 长按阈值 / 场景与分组 / 尺寸 / 上次页
  floating.json      悬浮项偏好（悬浮项本身仍然不落盘）
  macros.json        宏设置 + 宏元数据（事件仍在 macros/<id>.json）
actions/<id>/
  action.json        动作体（名称 / 图标 / 说明 / 变量 / 步骤）
  files/             该动作自己的模板图与脚本（P2）
resources/
  images/            共享 / 历史模板图（P2 起新增的模板图优先放动作目录）
  user/              用户素材（永不自动删）
trash/<时间戳>/<id>/  删除的动作目录（保留 30 天，可手工捞回）
staging/  ai/  macros/  subactions/  速记/  logs/  ocr-models/   不变
```

**属主表**

| 文件 | 唯一写入者 | 读方 |
| --- | --- | --- |
| `configs/settings.json` | `SettingsStore` | 设置页、托盘、钩子 |
| `configs/ui.json` | `UiStore` | 设置页、选择器、编辑器 |
| `configs/menus.json` | `MenuStore`（经 `MenuSystem.SaveToConfig`） | `MenuSystem`、备份 |
| `configs/panel.json` | `PanelStore`（经 `PanelLibrary.Save`） | 面板、设置页 |
| `configs/floating.json` | `FloatingStore`（经 `FloatingLibrary.SavePreferences`） | 悬浮窗、设置页 |
| `configs/macros.json` | `MacroFileStore`（经 `MacroStore`） | 宏菜单、编辑器 |
| `actions/<id>/…` | `ActionStore`（经 `ConfigBundle` / `ConfigStore`） | 引擎、面板、编辑器、AI 工具 |

所有单域写入经 `ConfigIO.ModifyDomain`（一把锁 + 严格读）；读仍合并整份聚合，所以首次升级能带上
旧文件的种子，分域完成后每次只碰被点的那个文件。

## 3. 阶段

### P1 · 配置分域（已完成）

- 新增 `Config/ConfigBundle.cs` + `Config/ConfigFiles.cs`：把 `AppConfig` 的字段拆成 6 个域文件，
  每个文件带 `schemaVersion`；`AppConfig.Load/Save` 改为「合并读 / 只写变更的域」。
- 迁移：`configs/` 不存在时从 `LTools.json`（或 `LTools.example.json`）读种子；首次保存写出 6 个文件后，
  旧文件改名 `LTools.json.migrated`（不动内容，随时可回滚）。
- 原子写：先写 `<file>.tmp` 再 `File.Move(overwrite)`；坏文件按域备份为 `<file>.bad-<时间戳>`（每域只留 3 份）。
- 备份：导出改为打包 `configs/`；导入同时兼容「旧备份只有 LTools.json」。
- 验收：启动后 6 个文件齐全；改导航页只改 `ui.json`；删 `configs/` 并把 `LTools.json.migrated` 改回来能完整恢复。

### P2 · 一个动作一个目录（已完成）

- 新增 `Core/Storage/ActionStore.cs`：`actions/<id>/action.json` + `actions/<id>/files/`；
  菜单组独立成 `configs/menus.json`，动作清单不再进配置文件。
- 迁移：仍以旧 `configs/actions.json` 为种子，首存后归档 `actions.json.migrated`；
  **`actions/` 目录存在（含空）即权威**，所以删光动作不会被旧清单复活。
- 写回只碰变了的动作：`ActionStore.SaveAll` 逐条比较 `action.json` 内容，没变不写；
  清单里没有的目录移入 `trash/<时间戳>/`（保留 30 天，启动时清理过期项）。
- 采纳草稿 = 把 `staging/<id>/files/*` 复制进 `actions/<id>/files/`，动作里的 `files/x.png` **原样保留**，
  不再改写路径、也不再搬进共享的 `resources/images`。
- 运行期资源根：正式运行（`AutomationRunner.Run`）与编辑器（`ActionEditor.Show`）都传该动作的 `files/`
  （`ActionStore.ResourceRootFor`），解析顺序仍是 `ImageLibrary.Resolve` 的四级回退。
- 备份：导出打包 `actions/`（含动作私有图）；导入旧备份时让现有 `actions/` 退位，
  让恢复出来的 `configs/actions.json` 生效，下次保存再重建目录。
- 暂时保留：编辑器新截的模板图仍进 `resources/images`，运行期截图仍与它混放——统一搬到
  `cache/screenshots` 与共享素材去重放到 P5。

### P3 · 存储层收口（已完成）

- 新增 `Config/ConfigDomain.cs` + `Config/DomainStores.cs`：`SettingsStore` / `UiStore` / `MenuStore` /
  `PanelStore` / `FloatingStore` / `MacroFileStore`；每个 store 只读改写自己那个域文件
  （`ConfigIO.ModifyDomain` 一把锁 + 严格读，写仍走 `WriteIfChanged`）。
- 15 处 `ConfigIO.Modify` 全部改成域写入；跨域只剩「菜单组 + 动作清单」
  （`ConfigIO.Modify(ConfigDomain.menus | ConfigDomain.actions, …)`，`MenuSystem.SaveToConfig` 用）。
- 迁移收尾：域文件不全时第一次写入自动扩成整份（6 个域 + `actions/`）并归档旧文件；
  分域完成后改一个开关只动目标 `configs/*.json`，不再扫动作目录、不再重写别的域。
- 删除动作收口到 `Core/Storage/ActionRemoval`：顺序固定「内存清单 / 菜单项 → 面板槽位 →
  菜单组 + 动作目录 → 宏 / 模板图 / 悬浮项回收」；启动时按磁盘动作目录幂等清掉菜单与面板里的
  悬空引用（在 `MenuSystem` / `PanelLibrary` 加载之前跑，没悬空就不写盘）。
- 验收：Debug/Release 0/0；用自检程序逐个域写入，只有目标文件 mtime 变化、`actions/*.json` 不动；
  人为塞一个不存在的动作 Id 进菜单 / 面板，启动修复只重写这两个文件。回滚：文件格式不变，退回旧版本即可。

### P4 · 代码结构（窗口与单元已完成，迁移入口待做）

- `Views` 里 1000+ 行的窗口拆成「外壳 + 部件」（partial，类名 / XAML 不变，只搬代码）：
  - `SuperPanel.xaml.cs`（1669 → 449 行）= 外壳 + 未配置提示 + 关闭保存；
    `SuperPanel.Build.cs`（渲染 / 场景标签 / 分区 / 分组导航 / 磁贴）、
    `SuperPanel.Navigation.cs`（搜索 / 选中 / 键盘 / 左栏页面切换 / 窗口事件）、
    `SuperPanel.Menus.cs`（右键菜单 + 磁贴 CRUD）。
  - `ActionEditorWindow.xaml.cs`（1568 → 355 行）= 外壳 + 字段 / 构造函数 + 行模型；
    `ActionEditorWindow.Steps.cs`（步骤列表 / 工具箱 / 增删改）、
    `ActionEditorWindow.DragSort.cs`（拖动排序）、
    `ActionEditorWindow.Variables.cs`（变量表）、
    `ActionEditorWindow.Run.cs`（保存 + 试运行）。
- 单元实现按分类分文件（`Features/Units/<分类>/<XxxUnit>.cs`，一个单元一个文件），
  共用的静态小工具留在同目录（`FileText.cs` / `Crc32.cs`）；新增单元仍只在 `BuiltInUnits` 加一行。
- 待做：迁移入口统一到 `Core/Storage/Migrations`（`schemaVersion` 驱动，一次只跑一步）；
  当前迁移逻辑仍散在 `ConfigBundle` / `ActionPrograms` 里。

### P5 · 资源与备份

- 运行期截图搬到 `cache/screenshots/`（可随时清空），不再靠 `shot-` 前缀 + 时间界线区分归属。
- 跨动作共享素材去重（按内容哈希），避免同一张图在多个动作目录里各存一份。
- 备份清单改为「目录白名单 + manifest 声明」，新增目录自动纳入。
- 启动时做一次孤儿检查（动作目录里无人引用的图 → 移入 `trash`，不直接删）。
- 提供「导出单个动作」= 打包一个动作目录。

## 4. 每阶段验收与回滚

| 阶段 | 验收 | 回滚 |
| --- | --- | --- |
| P1 | Debug/Release 0/0；6 个文件生成；只改一个域时其它文件 mtime 不变；旧文件仍可恢复 | 删 `configs/`，把 `LTools.json.migrated` 改回 `LTools.json` |
| P2 | 采纳草稿后 `actions/<id>/files/` 里直接有图、引用仍是 `files/x.png`；删动作进 `trash/`；改一个动作只重写它自己的 `action.json`；删光动作后重启不复活 | 把 `configs/actions.json.migrated` 改回 `actions.json`、删 `actions/`，回退版本直接读它 |
| P3 | 逐个域写入只有目标 `configs/*.json` 的 mtime 变化、`actions/*.json` 不动；并发保存不互相覆盖；菜单 / 面板里的悬空动作 Id 在启动时被清掉 | 域文件格式不变，退回旧版本仍能读 |
| P4 | 窗口拆完后交互与原版一致（面板建窗 / 切暂存区 / 编辑器载入动作均正常）；单元清单数量不变 | 纯代码改动，git revert |

## 5. 明确不做

- 不做跨文件事务 / 数据库：单进程 + 每域单写者 + 启动修复引用，够用且可读。
- 不为兼容旧版本同时写两份配置（迁移期除外）。
- 不把 `速记/`、`logs/`、`ai/` 收进 `configs/`：它们不是一个域，且速记是用户资产。
