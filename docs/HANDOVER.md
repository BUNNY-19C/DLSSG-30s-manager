# 交接文档

面向接手本项目的人（或未来的自己）。README 讲"怎么用"，这里讲"为什么这么做、改的时候会踩到什么"。

- 当前版本：**v1.9.3**（v1.9.x 线：界面重做 + 下载进度 + 游戏适配）
- 测试：**449 项全绿**（`dotnet run --project test/Harness -- --self-test`）
- 仓库：`github.com/BUNNY-19C/DLSSG-30s-manager`，打 tag 即发版

---

## 1. 这个项目是什么

给 RTX 30 系（SM86）装 DLSS 帧生成的管理器。上游 [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 提供代理 DLL（内嵌 DLSSG 运行时 + SM86 后端），本项目负责：

- **找游戏**（扫描 Steam 库 / 任意目录）
- **按游戏配置**（Enabled / Optimized 档位 / Preset / 倍率 / 日志级别 → 写进 `dlssg_sm86.ini`）
- **部署与恢复**（代理 DLL + INI 放入渲染目录；恢复时只删能证明是本项目的文件）
- **下载上游负载**（多源、证书 pin、进度条、暂停/取消）

**不做的事**：不修改上游 DLL、不打包上游二进制进安装包、不引入第三方（如 pandaligx/RTX-FG-Manager）的文件。安装包不含 mod，首次启动自动获取。

---

## 2. 五分钟上手

```bash
# 构建（需要 .NET 8 SDK）
dotnet build src/DLSSGManager/DLSSGManager.csproj -c Debug

# 全量测试（449 项，含本地化/主题/URL 策略/部署全链路）
dotnet run --project test/Harness -c Debug -- --self-test

# 只测下载链路（会真实联网下载到 mod/，约 210 MB）
dotnet run --project test/Harness -c Debug -- --fetch

# 打包（需要 Inno Setup 6 的 ISCC 在 PATH）
# 命令见 CONTRIBUTING.md「打包」
```

**发版流程**：`git tag v1.9.4 && git push origin main --tags` → GitHub Actions（`.github/workflows/release.yml`）自动构建安装包 + 便携版 + SHA256SUMS 并挂到 Release。工作流会把 tag 号通过 `-p:Version` 注入程序集，所以界面里的版本徽章自动正确。

**本地开发要点**：`DLSSGMANAGER_HOME` 环境变量可把数据目录重定向到任意路径——测试和冒烟验证都靠它隔离，别污染用户真实库（`%APPDATA%\DLSSGManager`）。

---

## 3. 代码地图

```
src/DLSSGManager/
├─ ModFetcher.cs        970  下载：多源链、DNS/证书校验、进度、暂停门、解压、版本探测
├─ DeploymentService.cs 939  部署/恢复/接管/状态判定——唯一写游戏目录的地方
├─ MainWindow.xaml      827  界面布局（顶栏/信息条/侧边栏/卡片详情/进度条/日志条）
├─ MainWindow.xaml.cs   854  窗口装配、语言与主题切换、下载面板、GPU 行
├─ MainWindow.Actions.cs 561 部署/恢复/接管/批量/扫描/路径选择处理器
├─ Gpu.cs               548  显卡探测、架构判定、注册表显示名读写、INF 间接字符串解析
├─ Strings.zh/en.cs     424  两张字符串表（必须逐键对齐，有测试）
├─ Models.cs            306  GameEntry / GameProfile / DeploymentInfo / AppData
├─ Detection.cs         283  游戏发现：双标记扫描、渲染目录解析、主程序挑选
├─ ModSource.cs         282  本地负载：入口名、版本读取、导入自定义代理
├─ AntiCheat.cs         282  内核级反作弊识别、隔离副本查找
├─ Store.cs             249  library.json 读写与归一化、INI 模板渲染
├─ Shell.cs             231  打开目录/文件、启动游戏、提权重启
├─ ModSourceLocator.cs  191  负载目录解析（仓库检出 / 便携 / 用户目录）
├─ Localization.cs      115  Loc.T、语言归一化
├─ LocalizationAudit.cs 111  源码扫描：未定义键、占位符不一致
├─ Detection/Gpu 之外的工具：Native、OutputLog、Converters、Theme、ThemeKeys、Palette、Version
└─ Themes/Dark.xaml + Light.xaml   两套主题字典（键必须完全一致）

test/Harness/Program.cs  2297  控制台测试，449 项 Check
installer/setup.iss       202  Inno Setup（安装时不联网）
mod/                          下载得到的上游负载，不入库
```

**分层原则**：模型与领域层（Models / Gpu / Detection / DeploymentService / ModFetcher / AntiCheat / Store）**不得引用 WPF 类型**。harness 是控制台程序，按白名单编译这些文件——所以给 harness 用不了的代码不要放进模型层。

---

## 4. 关键决策与"为什么"

改代码前先读这几条，都是踩过坑才写下来的：

### 4.1 界面

- **颜色一律 `DynamicResource`**。`StaticResource` 在元素创建时解析一次，切换主题后界面停在旧配色。有测试扫描 XAML/cs 里的硬编码颜色。
- **两套主题字典键必须一致**（`ThemeKeys.All` 是权威清单，测试双向比对 + 对比度 4.5:1）。
- **列表状态色需要显式重发**：`ThemeBrushConverter` 只在源属性通知时重算，切主题要调 `GameEntry.RaiseThemeColors()`。
- **对话框有自己的资源作用域**，主窗口的隐式样式传不进去，按钮/输入/滚动条样式要在每个 Window 里重声明。
- 文字从代码设置的（下拉项、GPU 建议行）不跟随语言绑定，必须走 `BuildLocalizedCombos` / `RefreshCodeText`。

### 4.2 线程纪律（不要再退回 ContinueWith）

早期版本用 `Task.Run(...).ContinueWith(t => ... t.Result ...)`，两个真实缺陷：faulted 时异常被静默吞掉，且 `_busy` 标志卡死导致所有按钮失效直到重启。

**现在的形态**：所有耗时操作都是 `async void` 处理器 + `try/catch/finally`，`_busy` 在 finally 释放；重活 `await Task.Run(...)`，结果回 UI 线程应用。新增操作请照 `MainWindow.Actions.cs` 的形状写。全局兜底：`TaskScheduler.UnobservedTaskException` 落日志。

**`DeploymentService.Evaluate`（后台）与 `Apply`（UI）是分开的**——`Evaluate` 会做哈希与 WinVerifyTrust（每个候选入口一次 ~30MB DLL），绝不能在 UI 线程调用。

### 4.3 部署所有权判定（数据安全的底线）

恢复时"删除"必须能证明文件是本项目的，判据三层：

1. 记录里的 SHA-256（`DeploymentInfo.Files`）
2. 已知社区构建哈希（`ModFetcher.KnownCommunityBuildHashes`）
3. 项目 Authenticode 签名（`IsProjectSigned`，走 WinVerifyTrust + 证书主题匹配）

**备份必须结转**：重新部署会重建 `DeploymentInfo`，若不把 `prev.Backups` 带过来，第一次部署置换的外部文件（别人的 INI）就永远回不来了。有测试守着。

**代理入口集合**：`OwnedEntryNames(prev)` = 已知入口名 ∪ 记录 `Files`（排除 INI）——自定义命名的代理只活在记录里，漏了它恢复就删不干净。

### 4.4 代理共存语义（0.3.3 前后不同）

- **0.2.x 负载**：同目录两个代理 = 两条推理管线 = 崩，所以 `Deploy` 仍会删掉多余代理（判据是 `source.IsLegacySchema`）。
- **0.3.3+ 负载**：上游改成**待机机制**——先加载的干活，其余只转发导出，不装钩子。因此部署**保留**多余代理、日志逐个注明「保留待机」，并写入 `DeploymentInfo.Files`（不记录就清不掉）。

**`Evaluate` 的状态判定不分 schema**：只要发现多于一个自家代理，就在详情里附一句「另有 N 个待机转发」并保持 `Deployed`——0.3.3 之前那是故障，现在是正常态。要区分的话得看 `IsLegacySchema`，但当前没有这个需求。

**异环（Neverness to Everness）就是要三个文件一起放**（`d3d12.dll` + `dinput8.dll` + `dlssg_sm86.ini`），这是"不能收敛代理"的直接原因。

### 4.5 游戏发现（Detection）

双标记扫描：`nvngx_dlssg.dll`（游戏自带 DLSS-G 负载）**或** `dlssg_sm86.ini`（本项目独有文件名，识别手装副本）。只看前者会漏掉不自带 DLSS-G 的游戏——异环就是这样，它的目录还是深层 UE 结构 `Client\WindowsNoEditor\HT\Binaries\Win64`。

`GenericFolderNames` 用于跳过无意义的目录名（Win64/Binaries/Client/…）取游戏名；上游标记命中后向上找 3 层找主程序。

### 4.6 下载安全模型（ModFetcher）

威胁模型是"镜像不可信"：

1. 仅 HTTPS + **主机白名单**，且解析结果必须全为公网地址
2. **代理感知**：检测到系统代理时跳过本机 DNS 校验——否则 DNS 污染/hosts 加速的用户会被全部源拒绝（issue #1 的真实原因）
3. 重定向逐跳复检、响应体积上限、归档条目不得逃逸目标目录
4. 载荷校验：所有 DLL 必须带项目签名；**镜像源**还必须匹配证书 pin（`85BA6676…`；GitHub 官方端点不匹配只记警告，容忍上游换证书）
5. **校验通过前不写 mod 目录**——所以取消/失败都不会破坏已有负载

### 4.7 INI 模板与 schema 演进

`IniTemplate.Render` **只覆写模板里已存在的键**。上游换 schema 时（0.2.x 的 Router/KernelImage → 0.3.x 的 Enabled/Optimized/Preset），旧键自然不写、新键自然生效，不需要分支。`IsLegacySchema`（检测 `Router=`）决定界面显示哪组控件。

配置面板只显示当前 schema 的键——显示另一组是死控件。

### 4.8 版本标记

0.3.0 起上游 INI 不再有 `; Native x.y.z.` 横幅，所以版本号靠两级：INI 横幅 → README 标题 → 下载时探测到的版本写进 `.manager-version` 标记文件（`--fetch` 路径也必须探测，否则徽章停在旧版本——修过一次）。

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
| 下载路径 | `ModFetcher.Payload` | 上游挪目录（如 0.3.0 的 `altnative/`→`alternatives/`）只改这张表 |
| 入口名 | `ModSource.ProxyCandidates` / `KnownProxyNames` | 新增/删除代理名 |
| 证书 pin | `ModFetcher.PinnedCertThumbprint` | 上游换证书（镜像源会直接拒绝） |
| 版本探测 | `ModSource.ReadVersionFromText` / `ModFetcher.ReadVersionFromReadme` | 横幅/标题格式变化 |
| INI 键 | `IniTemplate.Values` | 新键需要界面控件 |
| 默认值 | `GameProfile` 字段默认值 | 上游调默认（0.3.3 把倍率默认从 5 调到 3） |

**历史经验**：0.3.4 修了 0.3.3 在 RTX 30 上的驱动崩溃、0.3.5 修了帧生成重建后的花屏——这类纯运行时修复**不需要改管理器代码**，只需让用户更新负载。判断标准是：布局/schema/入口/证书都没变，就只更新负载。

---

## 6. 测试与验收

- **单元/集成**：`test/Harness` 覆盖部署→恢复全链路、所有权判定、下载 URL 策略、INI 渲染、本地化审计、主题一致性、版本号、下载进度与暂停门。新增逻辑请加 Check（`Check("描述", 条件, 详情)`），中文描述与既有风格一致。
- **机器相关**的检查（NVIDIA 适配器、真实目录）用 `hasNvidia` / `_hasModFiles` 守卫，别让 CI 依赖本机环境。
- **GUI 冒烟**（改界面后必做）：
  1. 预置一个隔离库：`DLSSGMANAGER_HOME=<临时目录>` 写一份 `library.json`，指向临时假游戏目录（含 `.exe` + 标记文件）
  2. 启动 exe，UI 里实际点一遍部署/恢复/主题切换
  3. 截图可用 PowerShell 抓窗口（**注意 DPI**：进程需 `SetProcessDPIAware()`，否则 125% 缩放下只截到左上 80%——这个坑查了很久）

---

## 7. 开发环境陷阱

1. **harness 白名单编译**：新增 src 下的 .cs 文件后，要加进 `test/Harness/Harness.csproj` 的 `<Compile Include>`，否则测试工程看不到类型。
2. **Mimosa hook 拒绝 Bash 写 .cs**：所有 C#/XAML 改动必须用 Write/Edit 工具（hook 会在提交前做安全扫描）。
3. **.NET 8 SDK 会把 commit hash 拼进 `InformationalVersion`**（`1.9.3+abc1234`），界面只取 `+` 前的部分。
4. **`ModSource` 是构造时快照**：导入自定义代理后必须重建实例才能看到新入口（测试里踩过一次）。
5. **`Stopwatch.GetTimestamp` 与 `TimeSpan` 不能混算**，用 `Stopwatch.GetElapsedTime()`。
6. **本地代理（Clash）抖动会导致 push 失败**——`git push` 报 443 连接失败时，提交和 tag 是安全的，稍后重试即可（Git 配置里 http.proxy 指向 127.0.0.1:7897）。

---

## 8. 已知限制 / 待办

按价值排序：

1. **上游 310.1 构建变体未支持**：仓库里有 `310.1/` 目录（4X 保守版，与根目录的 310.9 是两套内嵌运行时）。管理器只分发根目录版本。要支持得加"构建选择"（Payload 表加一组目标 + 界面开关）。
2. **安全审计缺失**：Mimosa 扫描在 v1.8.4 之后多次不可用，那几次提交没有扫描结论。工具恢复后值得补跑一次深度扫描。
3. **构造审查的 Optional 项**（未做，风险低）：
   - `ZipFile.ExtractToDirectory` 不限制解压总量（恶意镜像可炸磁盘；签名校验仍在写盘前，属可用性问题）
   - `ModFetcher.Verify` 是 private，镜像拒绝/官方放行的分支只能靠真实下载触发（可下沉为 internal + 假 staging 测试）
   - `LibraryStore.Save` 失败只写日志，调用方按成功继续
   - 下载/部署中途关窗口：进程退出会丢弃续作（记录不落盘）——可加 `Closing` 确认
4. **反作弊规则表**与 GPU 设备 ID 区间是经验数据，遇到误判按实际游戏样本增补。

---

## 9. 协作约定

- **issue 回复是给外部用户看的**：大白话讲原因、给可操作步骤、版本号只提最新；实现细节留在 commit message 与代码注释里。
- **README 一切从简**，游戏特有事项集中在页首的注意事项块。
- **不引入第三方二进制**；用户手装的文件只做识别与接管，不打包、不改写内容。
- 提交信息写清"为什么"（多数缺陷是语义变化引发的，只写"改了什么"后人会改回去）。
- 改动上游适配时，先跑第 5 节的核对清单，再决定改代码还是只更新负载。
