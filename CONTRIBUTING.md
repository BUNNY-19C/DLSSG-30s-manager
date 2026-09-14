# 贡献指南

感谢愿意帮忙。本项目的核心是"安全地改动别人的游戏目录"，所以对正确性的要求高于对功能数量的要求。

先说清楚一件事：**本项目的代码、文案和文档由 AI 生成**，维护者负责真机实测与发布。这也是为什么测试写得比代码还多——AI 产出需要能自动验证的护栏。提 PR 时请以"能不能通过全量测试、结论有没有实测支撑"为准，而不是以文风或结构是否顺手为准。

## 环境

- .NET 8 SDK
- Windows（项目使用 WPF，且大量功能依赖 Windows API）
- 如需编译安装包：[Inno Setup 6](https://jrsoftware.org/isdl.php)

## 构建与测试

```bash
dotnet build -c Release

# 全量测试。未获取 Mod 文件时，依赖它们的用例会自动跳过
./test/Harness/bin/Release/net8.0-windows/Harness.exe

# 获取 Mod 文件（约 101 MB，写入项目的 mod 目录）
./test/Harness/bin/Release/net8.0-windows/Harness.exe --fetch

# 只扫描本机游戏并报告反作弊情况，不改动任何文件
./test/Harness/bin/Release/net8.0-windows/Harness.exe --scan "D:\Games\SomeGame"
```

测试会把数据目录隔离到临时位置（通过 `DLSSGMANAGER_HOME` 环境变量），不会碰你真实的 `library.json` 和 `manager.log`。

## 打包

```bash
# 单文件 exe（自包含，目标机器无需装 .NET）
dotnet publish src/DLSSGManager/DLSSGManager.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

安装包用 [Inno Setup 6](https://jrsoftware.org/isdl.php) 编译（`installer/languages/` 下的简体中文语言文件随仓库提供，Inno 安装包未内置）：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=1.7.3 installer\setup.iss
```

产物在 `dist/`。推 `v*` 标签（例如 `git tag v1.7.3 && git push origin v1.7.3`）会由 GitHub Actions 自动完成构建、测试、打包并创建 Release，附两个 exe 与 `SHA256SUMS.txt`。

## 改动前的建议

**先说清楚要改什么、为什么。** 尤其是涉及以下部分时，最好先开 Issue 讨论，因为这些设计的取舍不明显：

- `DeploymentService` 的删除判据（标了"只删自己的文件"，改动会直接影响用户数据安全）
- `AntiCheat` 的识别规则（误报会让用户错过可用的游戏，漏报会让用户踩坑）
- `ModSourceLocator` 的路径选择（安装版与绿色版的行为差异）
- `Shell` / `PathGuard`（外壳调用的信任边界）

## 提交改动

1. **补测试。** 修 bug 时先写一个能复现的用例；加功能时覆盖主要分支。测试在 `test/Harness/Program.cs`，按 `Section` 分组。

2. **跑全量测试。** 提交前确认没有失败项。

3. **写清改动理由。** 提交信息里说明*为什么*改，而不只是改了*什么*。如果修的是行为问题，附上复现方式。

4. 一个提交做一件事。不要把重构和新功能混在一起。

## 关于兼容性数据

`AntiCheat.cs` 里的反作弊特征表靠文件名匹配。如果你确认了某个新的反作弊，或发现某条规则误报，请附上：

- 游戏名
- 目录里实际存在的文件名（`dir` 的输出）
- 该文件是否有数字签名，签名者是谁

这些信息决定规则该匹配什么。

## 新增游戏兼容性记录

如果你实测了某款游戏，欢迎用[兼容性反馈](https://github.com/BUNNY-19C/DLSSG-30s-manager/issues/new?template=game_compatibility.yml)模板提交。**失败的案例同样有价值**——它能帮其他人省下折腾的时间。

## 不要做的事

- 不要提交 Mod 的二进制文件（`mod/` 已被 gitignore）。它们属于上游项目，授权不允许转发，原因见 [docs/mod-files.md](docs/mod-files.md)。
- 不要把 `AntiCheat` 的拦截改成可以静默绕过。绕过检测需要用户明确确认，这是有意的设计。
