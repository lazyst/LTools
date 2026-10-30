# LTools 资源目录与配置备份（导出 / 导入）

> **状态**：已实现（待手测）　**关联**：默认工作目录已落地（commit `b1d28be`）；SQLite 评估后决定**暂不引入**。
>
> **背景**：用户希望在应用目录下放一个「资源目录」存放脚本 / 程序等，`runCommand` 动作可用相对路径引用；并新增配置备份的导出 / 导入，一个包同时备份设置与资源目录（含速记）。

---

## 1. 目标与非目标

### 1.1 目标

- 提供用户自有的 `resources/` 目录，存放脚本 / 可执行文件 / 其他素材。
- `runCommand` / `launchApp` 可用相对路径引用其中文件（默认工作目录已是应用目录）。
- 配置备份：导出 / 导入 `.ltbak` 包，含 `LTools.json` + `resources/` + `速记/`（速记可取消勾选）。
- 导入时弹安全警告并列出引用 `resources/` 的动作（防投毒）。

### 1.2 非目标

- 不引入 SQLite / 数据库。
- 不做云同步、版本历史、增量备份。
- 不对 `resources/` 做沙箱或权限隔离（与应用同信任域）。
- 不修改 `速记/` 的存储格式（仍为文本文件树）。

---

## 2. 数据根目录（DataRoot）—— 前置重构

当前 `AppPaths.AppDirectory` = `AppContext.BaseDirectory`，dev 下落到 `bin/Debug/net10.0-windows`，与 `ConfigLocator` 的 dev（仓库根）不一致。三样用户数据（配置 / 资源目录 / 速记）应共用同一根，故引入 **`DataRoot`**：

| 布局 | `DataRoot` |
|---|---|
| dev（仓库根含 `LTools.csproj`） | 仓库根 |
| prod | `exe` 同级（`AppContext.BaseDirectory`） |

`AppPaths` 重构：

```csharp
public static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory); // exe 所在
public static bool IsDevLayout => File.Exists(Path.Combine(RepoRoot, "LTools.csproj"));
public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
public static string DataRoot => IsDevLayout ? RepoRoot : AppDirectory;        // 用户数据根
public static string ResourceDir => Path.Combine(DataRoot, "resources");
public static string DefaultWorkdir() => Directory.Exists(DataRoot) ? DataRoot
    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
```

- `ConfigLocator` 的 `RepoRoot` / `IsDevLayout` 改为委托 `AppPaths`，删除重复。
- `DefaultWorkdir` 由 `AppDirectory` 改为 `DataRoot`（行为变化：dev 下默认工作目录由 `bin/Debug` 变仓库根——已与用户确认）。
- `速记/` 路径：`QuickNote.ComputeDefaultDir` 已是「配置文件所在目录 + 速记」，与 `DataRoot` 一致，无需改。

---

## 3. 资源目录 `resources/`

- 路径：`AppPaths.ResourceDir` = `DataRoot/resources`。
- 启动时 `EnsureResourceDir()`：不存在则 `CreateDirectory`，并写入一份 `README.txt`（仅首次；已存在不覆盖）。
- 仓库提交 `resources/README.txt`（dev 用）+ `resources/.gitkeep`；**发布 zip 不带 `resources/`**，由首启生成（与 `LTools.json` 同理：升级解压不碰用户已有目录）。
- `README.txt` 内容（约 5 行）：用途说明、相对路径用法（`resources\foo.ps1`）、升级不会删除本目录、可放任意文件。

---

## 4. 备份格式 `.ltbak`

本质是 zip（扩展名 `.ltbak`，关联「打开方式」非本应用职责）。包内固定结构：

```
manifest.json
LTools.json
resources/...        （含时）
速记/...             （含时）
```

`manifest.json`：

```json
{
  "format": "ltools-backup",
  "formatVersion": 1,
  "appVersion": "2.2.6",
  "createdAt": "2026-10-31T12:00:00",
  "includes": ["config", "resources", "notes"]
}
```

- 始终含 `config`；`resources` / `notes` 按 `includes` 决定。
- zip 条目名 UTF-8（`ZipFile` 默认），保留中文路径。
- `formatVersion` 预留向后兼容；导入端只认 `1`，高于则提示版本不支持。

---

## 5. 导出流程

入口：设置面板「备份与恢复」页 →「导出…」。

1. 范围复选框：`☑ 资源目录` `☑ 速记`（配置固定含）。默认全勾。
2. `SaveFileDialog`：默认文件名 `LTools-backup-yyyyMMdd-HHmmss.ltbak`，过滤器 `LTools 备份 (*.ltbak)|*.ltbak`。
3. 后台线程执行（避免阻塞钩子 / UI）：
   - 读 `ConfigLocator.FindPath()` 的 `LTools.json`。
   - 按 includes 收集 `resources/`、`速记/` 文件树（跳过 `.bak-*`）。
   - 写 `manifest.json` + 各条目到临时 zip，完成后移到目标路径（避免半写文件被当作有效备份）。
4. 体积 > 50 MB 时导出前确认。
5. 完成后 `TrayService.Notify` 提示路径。

---

## 6. 导入流程

入口：设置面板「备份与恢复」页 →「导入…」。

1. `OpenFileDialog`：过滤器 `LTools 备份 (*.ltbak)|*.ltbak`。
2. 解压到临时目录 `%TEMP%/ltools-import-<guid>/`（用 `ZipFile.ExtractToDirectory`，.NET 内置 zip-slip 校验）。
3. 校验 `manifest.json`：`format == "ltools-backup"`、`formatVersion == 1`、`includes` 合法、`LTools.json` 存在且可解析为 `AppConfig`。
4. **安全扫描**（仅针对 `resources/`，配置本身不算可执行）：
   - 统计可执行文件数：扩展名 ∈ `{.exe,.bat,.cmd,.ps1,.vbs,.js,.msi,.com,.scr,.lnk,.wsf,.jar}`。
   - 列出引用 `resources` 的动作：解析导入的 `LTools.json`，`Actions` 中 `Type ∈ {launchApp, runCommand}` 且 `Cmd` / `Target` 含 `resources`（不区分大小写、忽略分隔符差异）的条目，取「名称 + 片段」。
5. 弹 `ConfirmDialog` 警告：
   > 此备份含 **N** 个可执行文件，并将替换当前配置。导入后以下动作可能以**管理员权限**执行其中脚本：
   > • 名称1 — `resources\foo.ps1`
   > • …
   > 仅导入来源可信的备份。继续？
6. 用户确认后执行**整体替换**（仅替换 `includes` 中的项，未含项保持不动）：

   | 项 | 备份当前 | 写入导入 |
   |---|---|---|
   | 配置 | `LTools.json` → `LTools.json.bak-<ts>` | 覆盖写导入的 `LTools.json` |
   | 资源目录 | `resources/` → `resources.bak-<ts>` | 移入导入的 `resources/` |
   | 速记 | `速记/` → `速记.bak-<ts>` | 移入导入的 `速记/` |

   - 跨卷移动（`%TEMP%` 可能与 `DataRoot` 不同卷）：`Directory.Move` 跨卷抛异常 → 用 `CopyTo` + `Delete`。
   - 每步失败回滚：还原对应 `.bak`。
7. `ConfigStore.Reload()`（刷新菜单 / 动作清单 / 超级面板 / 终端路由）。
8. `TrayService.Notify` 成功，并提示「旧数据已备份为 `*.bak-<ts>`」。

---

## 7. 设置面板 UI（`ConfigHelper`）

新增导航项 `tag="backup"`，标题「备份与恢复」：

```
┌─ 导出 ─────────────────────────────┐
│  范围：☑ 资源目录  ☑ 速记（配置固定）│
│  [ 导出… ]                          │
│  <状态文字>                         │
├─ 导入 ─────────────────────────────┤
│  [ 导入… ]                          │
│  说明：仅导入信任来源的备份；备份   │
│  含可执行文件，导入后可能以管理员    │
│  权限执行。                         │
└─────────────────────────────────────┘
```

- 复用现有 `ConfirmDialog`（导入警告）、`OpenFileDialog` / `SaveFileDialog`。
- 操作期间禁用按钮，状态文字显示「正在导出 / 导入…」。

---

## 8. 动作编辑器改动（`ActionEditorDialog`）

- 「工作目录」标签 + 行旁新增「打开资源目录」按钮：资源管理器打开 `AppPaths.ResourceDir`（不存在先 `EnsureResourceDir`）。
- `launchApp` / `runCommand` 两处下方加 hint `TextBlock`：「提示：脚本建议放 `resources\`，工作目录留空即可用相对路径」。
- 不新增宏（`${app}` 等）：默认工作目录已是 `DataRoot`，相对路径足够。

---

## 9. 错误处理与边界

| 情况 | 处理 |
|---|---|
| 导入文件非本格式 zip | 校验失败 → 提示「不是有效的 LTools 备份」 |
| `formatVersion` > 1 | 提示「备份来自更新版本，请升级 LTools 后导入」 |
| 解压条目路径越界（zip slip） | `ExtractToDirectory` 抛 → 捕获并提示损坏 |
| 配置写失败（被占用） | 复用 `AppConfig.Save` 现有重试；仍失败回滚 `.bak` |
| 跨卷移动 | `CopyTo` + `Delete`，非 `Directory.Move` |
| `resources/` 与 `速记/` 同名 `.bak` 已存在 | `.bak-<ts>-<短GUID>` 加后缀避免冲突 |
| 备份体积过大 | 导出 > 50MB 确认；导入 > 100MB 确认 |

---

## 10. 文件清单

| 文件 | 改动 |
|---|---|
| `Core/AppPaths.cs` | 改：新增 `DataRoot` / `ResourceDir` / `RepoRoot` / `IsDevLayout`；`DefaultWorkdir` 用 `DataRoot` |
| `Core/ConfigLocator.cs` | 改：`RepoRoot` / `IsDevLayout` 委托 `AppPaths`，删重复 |
| `Core/BackupService.cs` | 新：`ExportAsync` / `ImportAsync` + 清单 + 安全扫描 + 回滚 |
| `Core/ResourceDir.cs` | 新：`Ensure()` / `OpenInExplorer()` |
| `Views/ConfigHelper.xaml` + `.cs` | 改：新增「备份与恢复」导航页 |
| `Views/ActionEditorDialog.xaml` + `.cs` | 改：「打开资源目录」按钮 + hint |
| `resources/README.txt`、`resources/.gitkeep` | 新（提交，dev 用；发布 zip 不带） |
| `App.xaml.cs` | 改：启动调用 `ResourceDir.Ensure()` |
| `CHANGELOG.md` | 改：未发布条目 |
| `docs/RESOURCE_DIR_AND_BACKUP.md` | 本 spec |

---

## 11. 实现步骤

- [x] 1. `AppPaths` 重构（`DataRoot` / `ResourceDir` / dev 对齐），`ConfigLocator` 委托；`DefaultWorkdir` 改 `DataRoot`；构建 0 错 0 警，回归默认工作目录行为。
- [x] 2. `ResourceDir.Ensure()` + `OpenInExplorer()`；`App` 启动调用；提交 `resources/README.txt` + `.gitkeep`。
- [x] 3. `BackupService`：`manifest` 模型 + `Export`（zip 写 + 范围）。
- [x] 4. `BackupService.Import` / `Inspect`：解压 + 校验 + 安全扫描 + 整体替换 + 回滚。
- [x] 5. `ConfigHelper`「备份与恢复」页 + 按钮接线。
- [x] 6. `ActionEditorDialog`：「打开资源目录」按钮 + hint。
- [x] 7. `CHANGELOG` + 文档收尾；构建 0 错 0 警。（全流程手测待用户进行）

---

## 12. 验收标准

- dev 下默认工作目录 = 仓库根；`resources/` 出现在仓库根并含 `README.txt`。
- 放 `resources/foo.ps1`，`runCommand` 命令填 `resources\foo.ps1`、工作目录留空 → 能执行。
- 导出 `.ltbak` 含 `manifest.json` + `LTools.json` + `resources/` + `速记/`（按勾选）。
- 删除上述三项后导入 → 数据恢复，且各留一份 `*.bak-<ts>`。
- 导入一份含 `resources/evil.ps1` 且配置有引用动作的备份 → 弹警告、列出动作、用户取消则不改动。
- 导入非 LTools 备份 / 损坏 zip → 友好提示，不动现有数据。
- 构建 0 错 0 警。

---

## 13. 开放问题

- `README.txt` 措辞与是否英文双语（当前仅中文用户，单中文即可）。
- 是否在导出时排除 `resources/.bak-*` / `速记/.bak-*`（草案：排除，避免备份里嵌套旧备份）。
