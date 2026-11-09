# 贡献指南

感谢愿意帮忙。本项目的核心是"安全地改动别人的游戏目录"，所以对正确性的要求高于对功能数量的要求。

先说清楚一件事：**本项目的代码、文案和文档由 AI 生成**，维护者负责需求确认与发布。提 PR 时请说明实际行为、改动原因及影响范围。

**接手开发先读 [docs/HANDOVER.md](docs/HANDOVER.md)**：代码地图、关键决策的来龙去脉（为什么必须 DynamicResource、为什么不能用 ContinueWith、代理共存语义在 0.3.3 前后有何不同）、上游更新时的核对清单，以及一批踩过的坑。

## 环境

- .NET 10 SDK（版本由 `global.json` 固定）
- Windows（项目使用 WPF，且大量功能依赖 Windows API）

## 构建与测试

```bash
dotnet build -c Release
dotnet test -c Release

# 获取 Mod 文件（命令行版，数据与界面版共用）
dotnet run --project src/DLSSGManager.Cli -- fetch

# 命令行冒烟测试：在临时目录里部署再恢复，确认游戏目录逐字节复原
powershell -File tests/smoke/cli-smoke.ps1 -Cli src/DLSSGManager.Cli/bin/Release/net10.0-windows/dlssgm.exe
```

**改动 `src/DLSSGManager.Core` 必须跑通测试。** 这个工具会写入和删除用户游戏目录里的文件，测试是唯一的自动防线。新增会写游戏目录的行为时，同时补一个"部署→恢复后目录逐字节一致"的往返用例。

设置 `DLSSGMANAGER_LIVE_TESTS=1` 会额外运行真实联网下载测试。

## 构建绿色版

后续只发布 Windows x64 自包含单文件绿色版，统一使用 `Portable` 发布配置。

```bash
# 单文件 exe（自包含，目标机器无需装 .NET）
dotnet publish src/DLSSGManager/DLSSGManager.csproj -c Release -p:PublishProfile=Portable
```

产物为 `publish/DLSSGManager.exe`，无需安装或另装 .NET。命令行版用同样的方式发布 `src/DLSSGManager.Cli`，产物为 `publish/dlssgm.exe`。推 `v*` 标签会由 GitHub Actions 先跑测试和冒烟测试，再构建两个程序并创建 Release，附 `SHA256SUMS.txt`。

## 改动前的建议

**先说清楚要改什么、为什么。** 尤其是涉及以下部分时，最好先开 Issue 讨论，因为这些设计的取舍不明显：

- `DeployPlanner` 的归属判定（决定哪些文件可以不备份就覆盖、哪些可以删除，改动会直接影响用户数据安全）
- `PlanExecutor` / `ElevatedProtocol`（唯一写游戏目录的代码，以及提权边界上的校验）
- `Payload/Manifests/*.json`（版本清单里的哈希决定下载是否被接受）
- `AntiCheat` 的识别规则（误报会让用户错过可用的游戏，漏报会让用户踩坑）
- `Shell` / `PathGuard`（外壳调用的信任边界）

## 适配上游新版本

布局和 INI 结构不变时，只需新增一份清单：用官方发布包核对后，在 `src/DLSSGManager.Core/Payload/Manifests/` 下添加 `<版本>.json`（每个文件的大小与 SHA-256），并更新 `ModFetcher.SupportedRelease`。`dlssgm verify` 可以核对本地负载与清单是否一致。

## 提交改动

1. **写清改动理由。** 提交信息里说明*为什么*改，而不只是改了*什么*。如果修的是行为问题，附上复现方式。

2. 一个提交做一件事。不要把重构和新功能混在一起。

## 关于兼容性数据

`AntiCheat.cs` 里的反作弊特征表靠文件名匹配。如果你确认了某个新的反作弊，或发现某条规则误报，请附上：

- 游戏名
- 目录里实际存在的文件名（`dir` 的输出）
- 该文件是否有数字签名，签名者是谁

这些信息决定规则该匹配什么。

## 新增游戏兼容性记录

如果你实测了某款游戏，欢迎用[兼容性反馈](https://github.com/BUNNY-19C/DLSSG-30s-manager/issues/new?template=game_compatibility.yml)模板提交。**失败的案例同样有价值**——它能帮其他人省下折腾的时间。

## 不要做的事

- 不要绕过版本清单接受下载：任何来源（包括 GitHub 官方）的文件都必须与清单哈希一致。
- 不要把写游戏目录的逻辑放回界面层或 `PlanExecutor` 之外。
- 不要提交 Mod 的二进制文件（`mod/` 已被 gitignore）。它们属于上游项目，授权不允许转发，原因见 [docs/mod-files.md](docs/mod-files.md)。
- 不要把 `AntiCheat` 的拦截改成可以静默绕过。绕过检测需要用户明确确认，这是有意的设计。
