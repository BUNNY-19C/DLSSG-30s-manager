# 交接文档

面向接手本项目的人（或未来的自己）。README 讲"怎么用"，这里讲"为什么这么做、改的时候会踩到什么"。

- 当前版本：**v1.12.0**（上游 0.4.1 适配，以及下载、主题、语言与视图模型改进）
- **上游 0.4.1**：配置、兼容迁移与驱动要求见 [0.4.1 适配记录](UPSTREAM-0.4.1.md)。旧版超分 On 在应用新版前要求主动选择显式模式；已有档位与各引擎倍率保留。
- 后续仅发布 Windows x64 自包含单文件绿色版，统一使用 `Portable` 发布配置；产物为 `publish/DLSSGManager.exe`。
- **v1.11.0 已适配上游 0.4.0**：完整包下载、目录化运行负载、单代理升级、新配置与恢复规则见 [0.4.0 适配记录](UPSTREAM-0.4.0.md)。
- **v1.12.0 重构内容**：
  - 下载链路：SourceForge 镜像源、并发测速排序、断点续传、离线 ZIP 导入、完整包/基础包选择
  - 主题系统：跟随 Windows 深浅色模式（"system" 选项 + WM_SETTINGCHANGE 监听）
  - 语言热切换：无闪烁的单帧更新（LocalizedText / Choice<T> / LocalizedString 架构）
  - MVVM 拆分：GameManagementViewModel / BatchViewModel / ProtectionViewModel + IBatchHost 接口
- 改进范围：见 [UI 落地清单](UI-IMPROVEMENTS.md)。
- 仓库：`github.com/BUNNY-19C/DLSSG-30s-manager`，打 tag 即发版
- **核心拆分重构（`refactor/core-split` 分支）**：.NET 10；领域代码移入 `DLSSGManager.Core`；部署拆为"计划 → 执行"；版本清单按哈希校验下载与判定归属；Mod 按版本存放；最小权限提权；崩溃恢复日志；命令行 `dlssgm`；135+ 测试与 CI 冒烟测试。下面第 3、4.3、4.6 节已按新结构改写。

---

## 1. 这个项目是什么

给 RTX 30 系（SM86）装 DLSS 帧生成的管理器。上游 [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 提供代理 DLL（内嵌 DLSSG 运行时 + SM86 后端），本项目负责：

- **找游戏**（扫描 Steam 库 / 任意目录）
- **按游戏配置**（Enabled / Optimized 档位 / Preset / 倍率 / 日志级别 → 写进 `dlssg_sm86.ini`）
- **部署与恢复**（代理 DLL + INI 放入渲染目录；恢复时只删能证明是本项目的文件）
- **下载上游负载**（多源、证书 pin、进度条、暂停/取消）

**不做的事**：不修改上游 DLL、不随管理器分发上游二进制、不引入第三方（如 pandaligx/RTX-FG-Manager）的文件。绿色版不含 mod，首次启动自动获取。

---

## 2. 五分钟上手

```bash
# 构建与测试（需要 .NET 10 SDK，版本见 global.json）
dotnet build
dotnet test

# 下载 Mod 文件（命令行版；数据目录与界面版共用）
dotnet run --project src/DLSSGManager.Cli -- fetch
dotnet run --project src/DLSSGManager.Cli -- verify      # 按版本清单逐文件核对

# 构建免安装绿色版（自带 .NET）
dotnet publish src/DLSSGManager/DLSSGManager.csproj -c Release -p:PublishProfile=Portable
dotnet publish src/DLSSGManager.Cli/DLSSGManager.Cli.csproj -c Release -p:PublishProfile=Portable
```

**发版流程**：`git tag v1.9.4 && git push origin main --tags` → GitHub Actions（`.github/workflows/release.yml`）自动构建绿色版与 SHA256SUMS 并挂到 Release。工作流会把 tag 号通过 `-p:Version` 注入程序集，所以界面里的版本徽章自动正确。

**本地开发要点**：`DLSSGMANAGER_HOME` 环境变量可把数据目录重定向到指定路径，与用户默认库（`%APPDATA%\DLSSGManager`）分开保存。

---

## 3. 代码地图

```
src/DLSSGManager.Core/              领域层（net10.0-windows，无 WPF）
├─ Deploy/
│  ├─ DeployPlanner.cs      读取游戏目录与记录，产出 DeployPlan；所有归属判定都在这里，不写任何文件
│  ├─ PlanExecutor.cs       唯一写游戏目录的代码：执行计划、构建部署记录、路径与链接校验、哈希复核
│  ├─ DeployPlan.cs         计划与步骤类型（可 JSON 序列化，供提权进程与崩溃日志使用）
│  ├─ FilePlanJournal.cs    预写日志 + PlanRecovery（中断的部署回滚、中断的恢复补完）
│  └─ DeploymentService.Operations.cs  Deploy/Restore 入口（同步版与带提权的异步版）
├─ DeploymentService.cs     归属判定原语（IsOurs / 签名 / 哈希）、状态检查 Evaluate、手工安装接管
├─ Payload/
│  ├─ Manifests/*.json      各上游版本的逐文件清单（大小、SHA-256、签名者、所属包），嵌入程序集
│  ├─ PayloadManifest.cs    清单模型与 PayloadCatalog
│  ├─ PayloadVerifier.cs    按清单校验暂存负载、发布到版本目录、核对已安装负载
│  └─ ModStore.cs           mod/versions/<版本> + current.json：原子切换、保留上一版、迁移旧布局
├─ Fetch/
│  ├─ DownloadSources.cs    下载源目录、选择与测速排序
│  ├─ PayloadHttp.cs        IHttpClientFactory + Resilience 管道
│  ├─ DownloadPolicy.cs     主机白名单 + 连接时地址校验（ConnectCallback）
│  ├─ HttpTransfer.cs       流式传输、重定向复检、上限、停顿检测、断点续传
│  ├─ ArchiveExtractor.cs   防穿越/防炸弹解压
│  └─ DownloadGate.cs       暂停/跳过
├─ ModFetcher.cs            下载编排（只协调上面各部分）
├─ Elevation/               提权：协议与校验、提权进程处理器、发起方
├─ ProjectSigning.cs        项目证书指纹
├─ Store.cs                 library.json（备份轮转、损坏隔离、只读保护）与 INI 渲染
├─ Models / ModSource / ModSourceLocator / Detection / AntiCheat / Gpu / Shell / Localization / Strings.*

src/DLSSGManager/                   WPF 界面（引用 Core）
├─ MainWindow.*.cs / ViewModels/    界面与视图模型；部署/恢复只调用 DeploymentService.DeployAsync/RestoreAsync
└─ App.xaml.cs                      启动；--fetch；--elevated <pipe> 提权助手模式（无窗口）

src/DLSSGManager.Cli/               命令行 dlssgm（list/add/status/deploy/restore/fetch/import/verify/recover）

tests/DLSSGManager.Core.Tests/      xUnit：部署往返、归属判定、计划、清单、下载、崩溃恢复、提权协议、CLI
tests/smoke/cli-smoke.ps1           对构建产物做端到端冒烟测试（CI 与发版都会跑）
mod/                                下载得到的上游负载，不入库
```

**分层原则**：`DLSSGManager.Core` **不得引用 WPF 类型**。界面层不直接写游戏目录：所有写入都走 Planner → Executor，需要管理员权限时由提权助手执行同一份计划。

---

## 4. 关键决策与"为什么"

改代码前先读这几条，都是踩过坑才写下来的：

### 4.1 界面

- **颜色一律 `DynamicResource`**。`StaticResource` 在元素创建时解析一次，切换主题后界面停在旧配置。
- **两套主题字典键必须一致**，切换主题时才能正确解析全部资源。
- **列表状态色需要显式重发**：`ThemeBrushConverter` 只在源属性通知时重算，切主题要调 `GameEntry.RaiseThemeColors()`。
- **对话框有自己的资源作用域**，主窗口的隐式样式传不进去，按钮/输入/滚动条样式要在每个 Window 里重声明。
- **主题跟随系统**（v1.12.0 新增）：`Theme.ApplyPreference("system")` 通过注册表读取 Windows 深浅色模式，MainWindow 在 `OnSourceInitialized` 中添加 `WM_SETTINGCHANGE` 消息钩子，监听 "ImmersiveColorSet" 变更并调用 `AfterThemeSwitch()`。
- **语言热切换无闪烁**（v1.12.0 重构）：
  - `LocalizedText` 持有 key+args，读时渲染为当前语言；`GameEntry.StatusMessage` 与 `DeploymentService.GameCheck.Detail` 现为 `LocalizedText?`，不再是固定字符串。
  - `Choice<T>` 泛型（替代 `Choice` / `TextChoice` record），下拉项通过 `INotifyPropertyChanged` 通知文本变更，无需重建列表。
  - `LocalizedString` 用于 ComboBoxItem.Content（WPF 会复制 Content 导致绑定失效），`App.xaml` 定义隐式 DataTemplate 渲染它。
  - 语言切换时所有文本在一帧内更新完毕（`Loc.SetLanguage` 发出通知 → 绑定与 `Choice<T>.Text` 重新求值），游戏详情不需要重新检查。

### 4.2 线程纪律（不要再退回 ContinueWith）

早期版本用 `Task.Run(...).ContinueWith(t => ... t.Result ...)`，两个真实缺陷：faulted 时异常被静默吞掉，且 `_busy` 标志卡死导致所有按钮失效直到重启。

**现在的形态**：所有耗时操作都是 `async void` 处理器 + `try/catch/finally`，`_busy` 在 finally 释放；重活 `await Task.Run(...)`，结果回 UI 线程应用。新增操作请照 `MainWindow.Actions.cs` 的形状写。全局兜底：`TaskScheduler.UnobservedTaskException` 落日志。

**`DeploymentService.Evaluate`（后台）与 `Apply`（UI）是分开的**——`Evaluate` 会做哈希与 WinVerifyTrust（每个候选入口一次 ~30MB DLL），绝不能在 UI 线程调用。

状态检查在没有部署记录时自动识别手工安装，并通过 `GameCheck.Adoption` 返回待接管记录；`Apply` 在 UI 线程登记，调用方随后保存库。单个检查、启动/刷新全部、扫描和添加目录共用此流程。接管不改写游戏文件、不生成原始备份，也不覆盖已有部署记录和备份；界面不再提供手动接管按钮。

### 4.3 部署所有权判定（数据安全的底线）

恢复时"删除"、部署时"不备份就覆盖"，都必须能证明文件是本项目的。判据按顺序（`DeploymentService.IsOurs`）：

1. 记录里的 SHA-256（`DeploymentInfo.Files`）
2. 已知版本清单里的项目构建（`PayloadCatalog.IsProjectBuild`，按字节判定）
3. 已知社区构建哈希（`ModFetcher.KnownCommunityBuildHashes`）
4. 项目 Authenticode 签名——WinVerifyTrust 确认完整，**且证书指纹在 `ProjectSigning.PinnedThumbprints` 中**。只用于清单出现之前的旧版本。

**不要用证书名称判定归属**：任何人都能自签 `CN=DLSSG`。旧版正是这样判定的，会把冒名 DLL 当成自己的覆盖或删除。NVIDIA 签名的运行时文件只认记录哈希——游戏自己也带这些文件。

**两段式写入**：`DeployPlanner` 只读，产出每一步；`PlanExecutor` 执行时再复核两件事——删除前文件哈希仍是计划时判定的值；写入的副本哈希等于计划时 Mod 文件的哈希。两次检查之间文件被换掉，就停止，不会误删或装错。

**备份必须结转**：重新部署会重建 `DeploymentInfo`，若不把 `prev.Backups` 带过来，第一次部署置换的外部文件（别人的 INI）就永远回不来了。

**代理入口集合**：`OwnedEntryNames(prev)` = 已知入口名 ∪ 记录 `Files`（排除 INI）——自定义命名的代理只活在记录里，漏了它恢复就删不干净。

### 4.4 代理数量与清理

当前 0.4.x 使用一个代理和完整运行目录，按 `ModSource.RequiresSingleProxy` 决定是否清理多余入口。仅清理能确认归属的文件，并保留原始备份与逐文件记录。旧版 0.3.x 的待机代理规则只适用于对应旧负载，不能作为新版安装建议。

### 4.5 游戏发现（Detection）

双标记扫描：`nvngx_dlssg.dll`（游戏自带 DLSS-G 负载）**或** `dlssg_sm86.ini`（本项目独有文件名，识别手装副本）。只看前者会漏掉不自带 DLSS-G、但已手工部署本项目的游戏。

`GenericFolderNames` 用于跳过无意义的目录名（Win64/Binaries/Client/…）取游戏名；上游标记命中后向上找 3 层找主程序。

### 4.6 下载安全模型（ModFetcher）

威胁模型是"**任何来源都不可信，只认内容**"：

1. 仅 HTTPS + **主机白名单**；重定向逐跳复检（最多 5 跳）。
2. **连接时校验地址**（`DownloadPolicy.ConnectAsync`）：自己解析 DNS，只连公网地址，且连的就是校验过的地址。连向用户配置的系统代理（例如 Clash 的 127.0.0.1:7897）放行，其他本地端口拒绝。旧版在发请求前单独解析一次，有检查/使用时间差，开代理时还整个跳过。
3. 响应体积上限（逐文件按清单大小封顶）、解压上限 1 GB / 4096 条目、条目不得逃逸目录或使用备用数据流。
4. **按版本清单逐字节校验**（`PayloadVerifier`）：每个文件的大小和 SHA-256 必须与 `Payload/Manifests/<版本>.json` 一致，GitHub 官方源也一样——tag 和 Release 附件都可以被替换。项目 DLL 额外校验签名（指纹固定）。文档类文件（README）只复制、不校验：Release 包里的 README 与仓库同 tag 的不同，且它们从不部署。
5. **安装是原子的**（`ModStore`）：在 `mod/versions/` 下的临时目录组装，完整后才改名到位并切换 `current.json`。失败或取消不影响正在使用的版本；上一版保留，可以回退。

重试与超时由 `PayloadHttp` 的 Resilience 管道负责（瞬时错误带抖动退避）；已收到响应头后连接中断，由 `HttpTransfer` 从已下载的字节处续传。测速请求不重试。

真实网络测试默认关闭：设置 `DLSSGMANAGER_LIVE_TESTS=1` 后会经由生产代码栈从 GitHub 下载并校验。

**SourcePickerDialog 新对话框**（替代主窗口嵌入式面板）：
- 包类型选择（完整/基础）+ 构建目录选择（310.9 / 310.1）
- 速度测试结果 + Retest 按钮
- 源列表显示实时速度（fastest / available / unavailable）
- 拖拽区 + Browse 按钮用于离线 ZIP 导入
- 嵌入式进度面板（Pause / Skip / Stop 按钮）
- 安装成功后 `Installed` 标志返回主窗口刷新

### 4.7 INI 模板与 schema 演进

`IniTemplate.Render` **只覆写模板里已存在的键**。上游换 schema 时（0.2.x 的 Router/KernelImage → 0.3.x 的 Enabled/Optimized/Preset），旧键自然不写、新键自然生效，不需要分支。`IsLegacySchema`（检测 `Router=`）决定界面显示哪组控件。

**按节写入**：`[Runtime] Mode` 保留上游模板值，`[SuperResolution] Mode` 写显式超分模式；`Optimized` / `Preset` 写 `[DLSSG]`，`FlowResolution` / `UseHudless` 写 `[XeSSFG]`，`FrameTiming` 写 `[Logging]`。节外同名键不被误写。

### 4.8 MVVM 视图模型拆分（v1.12.0）

引入 CommunityToolkit.Mvvm 8.4.2（`ObservableObject` / `RelayCommand`），三个独立视图模型替代 MainWindow 内联状态：

- **GameManagementViewModel**：单游戏配置状态（未保存/待应用/已应用）、保存/应用按钮启用逻辑、构建选择警告（310.1 限制检查）。
- **BatchViewModel**：批量操作（勾选集合、可见项计数、部署/恢复命令）、结果面板（成功/失败计数、逐项日志）、失败重试。
- **ProtectionViewModel**：反作弊检测状态（横幅显示）、部署按钮工具提示（内核级反作弊警告）、批量操作提示。

**IBatchHost 接口**：主窗口实现 `IEnumerable<GameEntry> VisibleGames { get; }` / `void RefreshGameView()` / `void ShowResult(bool, string)` / `void LogDetails(IEnumerable<string>)`，BatchViewModel 通过接口回调而不直接操作控件。MainWindow.Batch.cs 显式实现该接口（避免公开暴露内部方法）。

**好处**：
1. 单游戏详情与批量操作的状态逻辑分离，MainWindow.xaml.cs 不再包含按钮启用计算。
2. 视图模型可单独测试（模拟 `IBatchHost` 即可验证命令逻辑）。
3. 命令绑定替代事件处理器（`Command="{Binding Batch.DeployCommand}"` 而非 `Click="DeployAll_Click"`）。

配置面板只显示当前 schema 的键——显示另一组是死控件。

### 4.9 版本标记

0.3.0 起上游 INI 不再有 `; Native x.y.z.` 横幅，所以版本号靠两级：INI 横幅 → README 标题 → 下载时探测到的版本写进 `.manager-version` 标记文件（`--fetch` 路径也必须探测，否则徽章停在旧版本——修过一次）。

### 4.10 配置快照（"待应用"提示）

部署时把 `RenderDir` / `PreferredProxy` / `Profile` 序列化成 JSON 存进 `DeploymentInfo.AppliedConfiguration`，`ConfigurationState.NeedsApply` 拿它和当前配置比较。

- 比较前先把旧快照**按当前类型读回再序列化**：新增的 `GameProfile` 字段在旧快照里不存在，读回时取默认值，所以加设置不会把所有已部署游戏都标成待应用。
- 由此带来的约束：**新字段的默认值必须等于上游出厂默认**——旧部署的 INI 从没写过它，实际生效的就是上游默认。改了某个字段的含义或取值范围时，旧快照会被当成"未变"，需要单独处理。
- 没有快照的旧记录（v1.11.0 之前部署）一律显示待应用，不臆测其配置。

---

## 5. 上游更新了怎么办（适配手册）

上游更新时按这个清单逐项核对，**不要凭版本号猜测**：

```bash
# 1. 版本 + 提交历史
curl -s "https://api.github.com/repos/sdli1995/dlssg_for_sm86/commits?per_page=3" | python -c "..."
# 2. README 标题（版本探测依赖的正则）
curl -s "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/README.md" | head -2
# 3. 文件树（比对本项目 Payload 表的 SourcePath）
curl -s "https://api.github.com/repos/sdli1995/dlssg_for_sm86/git/trees/main?recursive=1" | python -c "..."
# 4. INI 键（schema 是否变化）
curl -s "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/dlssg_sm86.ini" | grep -E "^[A-Za-z]+="
# 5. 证书指纹（下载一个 DLL 验签）
curl -sL -o /tmp/v.dll "https://gh-proxy.com/https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/version.dll"
powershell -Command "(Get-AuthenticodeSignature 'C:\tmp\v.dll').SignerCertificate.Thumbprint"
```

对照点：

| 项目 | 代码位置 | 变了要改 |
|---|---|---|
| 文件清单 | `Payload/Manifests/<版本>.json` | **每个新版本都要加**：用官方发布包逐个算大小与 SHA-256（`source` 是上游路径，`destination` 是本地布局）；改 `ModFetcher.SupportedRelease`。布局与 INI 不变时只需这一步 |
| 入口名 | `ModSource.ProxyCandidates` / `KnownProxyNames` | 新增/删除代理名 |
| 证书指纹 | `ProjectSigning.PinnedThumbprints` | 上游换证书时追加（旧指纹保留，用于识别旧安装） |
| 版本探测 | `ModSource.ReadVersionFromText` / `ModFetcher.ReadVersionFromReadme` | 横幅/标题格式变化 |
| INI 键 | `IniTemplate.Values` | 新键需要界面控件 |
| 默认值 | `GameProfile` 字段默认值 | 上游调默认（0.3.3 把倍率默认从 5 调到 3） |

**历史经验**：0.3.4 修了 0.3.3 在 RTX 30 上的驱动崩溃、0.3.5 修了帧生成重建后的花屏——这类纯运行时修复**不需要改管理器代码**，只需让用户更新负载。判断标准是：布局/schema/入口/证书都没变，就只更新负载。

---

## 6. 开发环境陷阱

1. **Mimosa hook 拒绝 Bash 写 .cs**：所有 C#/XAML 改动必须用 Write/Edit 工具（hook 会在提交前做安全扫描）。
2. **.NET SDK 会把 commit hash 拼进 `InformationalVersion`**（`1.9.3+abc1234`），界面只取 `+` 前的部分。
3. **`ModSource` 是构造时快照**：导入自定义代理后必须重建实例才能看到新入口。
4. **`Stopwatch.GetTimestamp` 与 `TimeSpan` 不能混算**，用 `Stopwatch.GetElapsedTime()`。
5. **本地代理（Clash）抖动会导致 push 失败**——`git push` 报 443 连接失败时，提交和 tag 是安全的，稍后重试即可（Git 配置里 http.proxy 指向 127.0.0.1:7897）。

---

## 7. 已知限制 / 待办

按价值排序：

1. **310.1 构建变体现已支持（当前工作区）**：DLL 下载路径加 `310.1/`，INI 仍取上游根目录；本地使用 `mod/variants/310.1` 与默认根构建分开。`ManagementState.cs` 定义构建限制与配置快照，`MainWindow.Management.cs` 负责保存状态与关闭流程，`MainWindow.Batch.cs` 负责 IBatchHost 实现。旧记录没有应用快照时显示待应用，不臆测其配置。
2. **构造审查的 Optional 项**（未做，风险低）：
   - `ExtractArchive` 限制了条目数（500）、单条目大小（200MB）、总解压量（300MB），防止 zip 炸弹（v1.12.0 已实现）。
   - 保存失败与关闭保护已落地：Save 返回结果；UI 保留内存记录和重试提示，批量保存失败停止后续项；Closing 等待操作完成或取消下载后退出。此流程不覆盖强制结束进程或断电。
3. **反作弊规则表**与 GPU 设备 ID 区间是经验数据，遇到误判按实际游戏样本增补。
4. **测试覆盖**：`tests/DLSSGManager.Core.Tests` 覆盖 Core 与命令行（部署往返、归属判定、计划、清单与下载校验、断点续传、崩溃恢复、提权协议、库文件恢复、CLI），`tests/smoke/cli-smoke.ps1` 对构建产物做端到端检查，CI 与发版都会跑。未覆盖：WPF 界面与视图模型（有意不做界面自动化）、真正经过 UAC 的提权（需要人工点确认，提权处理器本身已经通过命名管道测过）。

---

## 8. 协作约定

- **issue 回复是给外部用户看的**：大白话讲原因、给可操作步骤、版本号只提最新；实现细节留在 commit message 与代码注释里。
- **README 一切从简**，游戏特有事项集中在页首的注意事项块。
- **不引入第三方二进制**；用户手装的文件只做识别与接管，不打包、不改写内容。
- 提交信息写清"为什么"（多数缺陷是语义变化引发的，只写"改了什么"后人会改回去）。
- 改动上游适配时，先跑第 5 节的核对清单，再决定改代码还是只更新负载。
