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
| 配置写失败（被占用 / 共享冲突） | `File.Copy` 失败即抛出 → 触发回滚恢复 `.bak`，并向用户报错（未做重试，复用 `AppConfig.Load` 的重试仅作用于读取侧） |
| 跨卷移动 | `CopyDir`（递归 `File.Copy`）而非 `Directory.Move`，兼容 `%TEMP%` 与 `DataRoot` 不同卷 |
| `resources/` 与 `速记/` 同名 `.bak` 已存在 | `.bak-<ts>` 后追加 `-<n>` 序号避免冲突 |
| 备份体积过大 | 仅导出 > 50 MB 时弹确认；导入不设硬阈值（由警告文案 + 可执行文件清单提示） |

---

## 10. 文件清单

| 文件 | 改动 |
|---|---|
| `Core/AppPaths.cs` | 改：新增 `DataRoot` / `ResourceDir` / `RepoRoot` / `IsDevLayout`；`DefaultWorkdir` 用 `DataRoot` |
| `Core/ConfigLocator.cs` | 改：`RepoRoot` / `IsDevLayout` 委托 `AppPaths`，删重复 |
| `Core/BackupService.cs` | 新：`Export` / `Inspect` / `Import` / `Cleanup` + 清单 + 安全扫描 + 回滚 |
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

## 13. 测试用例

> 编号约定：`R*` = 资源目录 / 目录路由，`E*` = 导出，`I*` = 导入，`G*` = 通用 / 构建。预期均为手测可观察结果。

| 编号 | 场景 | 前置 / 步骤 | 预期 |
|---|---|---|---|
| R01 | dev 下数据根 | dev 构建运行 | `DataRoot` = 仓库根；`resources\` 出现并含 `README.txt`；动作留空工作目录时 cwd = 仓库根 |
| R02 | prod 下数据根 | 发布包解压后运行 | `DataRoot` = exe 同级；`resources\` 首启生成；`README.txt` 写入 |
| R03 | 资源目录首启创建 | 删除 `resources\` 后启动 | 自动重建目录 + `README.txt`；不抛异常 |
| R04 | README 不覆盖 | 已有自定义 `README.txt` 再启动 | 文件内容保持不变 |
| R05 | 打开资源目录按钮 | 动作编辑器点「资源目录」 | 资源管理器打开 `resources\`（不存在则先创建） |
| R06 | 相对路径执行脚本 | `resources\test.ps1` 输出一行；`runCommand` 命令填 `resources\test.ps1`、工作目录留空 | 脚本执行，输出可见（`direct` 或带终端） |
| R07 | launchApp 默认工作目录 | `launchApp` 不填工作目录、Target 用相对路径脚本 | 在 `DataRoot` 下解析执行 |
| E01 | 导出默认全选 | 勾选资源目录 + 速记 → 导出 | `.ltbak` 内含 `manifest.json` + `LTools.json` + `resources/` + `速记/` |
| E02 | 导出取消资源目录 | 取消勾选资源目录 → 导出 | 包内无 `resources/`；`manifest.includes` 不含 `resources` |
| E03 | 导出取消速记 | 取消勾选速记 → 导出 | 包内无 `速记/`；`includes` 不含 `notes` |
| E04 | 导出跳过旧备份 | `resources\` 下放 `foo.bak-20260101` 文件 → 导出 | 该文件不进入包 |
| E05 | 导出体积确认 | 制造 > 50 MB 资源后导出 | 弹「约 N MB，确定导出」确认；取消则不生成文件 |
| E06 | 导出覆盖已有文件 | 选已存在的 `.ltbak` 路径导出 | 旧文件被替换，无残留 `.part` |
| E07 | 导出过程禁用按钮 | 导出进行中再点「导出…」 | 不响应（`_backupBusy`） |
| I01 | 导入整体替换 | 导入全选备份 | 配置 / 资源目录 / 速记均被替换；各留 `*.bak-<ts>`；菜单 / 动作 / 超级面板列表刷新 |
| I02 | 导入含可执行文件 | 备份 `resources\` 含 `.ps1/.exe` 且配置引用 → 导入 | 弹警告，显示可执行文件数 + 引用动作清单；确认后替换 |
| I03 | 导入无可执行文件 | 备份无可执行文件 → 导入 | 警告显示「0 个可执行文件」；仍需确认 |
| I04 | 导入取消 | 警告框点取消 | 现有数据零改动；临时目录被清理 |
| I05 | 导入备份不含速记 | 备份 `includes` 无 `notes` → 导入 | 速记目录保持原样，不改名也不留 `.bak` |
| I06 | 非备份 zip | 导入任意 zip（无 `manifest.json`） | 报「不是有效的 LTools 备份」；不动现有数据 |
| I07 | 备份来自更新版本 | `manifest.formatVersion=2` | 报「请升级 LTools 后再导入」；不动现有数据 |
| I08 | 配置损坏 | `LTools.json` 内容非法 JSON → 导入 | 报「LTools.json 无法解析：…」；不动现有数据 |
| I09 | zip slip 越界条目 | 构造含 `..\evil` 条目的 zip → 导入 | 解压抛异常 → 友好报错；不动现有数据 |
| I10 | 同秒重复导入 | 同一秒内两次导入 | 第二次 `.bak-<ts>-1`，不覆盖第一次的 `.bak` |
| I11 | 导入失败回滚 | 导入资源目录时人为占用目标 → 失败 | 已写入的配置回滚为原文件；资源目录恢复；报错且无数据丢失 |
| I12 | 导入后导航恢复 | 在「备份与恢复」页导入 → 关闭设置 → 重开 | 停留在「备份与恢复」页（`LastNavPage=backup`） |
| G01 | 往返一致性 | 导出 → 删除三项 → 导入 | 配置 / 资源目录 / 速记内容与导出前一致（除 `.bak`） |
| G02 | 构建 | `dotnet build -p:NoWin32Manifest=true` | 0 错 0 警 |
| G03 | README 发布不含 | 检查发布 zip | 不含 `resources\`（首启生成） |

> zip slip 用例可这样构造（PowerShell，仅在测试机执行）：
> ```powershell
> $z = "$env:TEMP\slip.zip"; Remove-Item $z -ErrorAction Ignore
> Add-Type -AssemblyName System.IO.Compression.FileSystem
> $a = [System.IO.Compression.ZipFile]::Open($z,'Create')
> $e = $a.CreateEntry("../evil.txt"); (New-Object IO.StreamWriter $e.Open()).WriteLine("x"); $a.Dispose()
> ```
> 然后用「导入…」选 `$z`，预期 I09。

---

## 14. 开放问题

- `README.txt` 措辞与是否英文双语（当前仅中文用户，单中文即可）。
- 是否在导出时排除 `resources/.bak-*` / `速记/.bak-*`（草案：排除，避免备份里嵌套旧备份）。
