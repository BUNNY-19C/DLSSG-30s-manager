# DLSSG 30 系管理器

**简体中文** | [English](README.en.md)

[![Release](https://img.shields.io/github/v/release/BUNNY-19C/DLSSG-30s-manager?style=flat-square&label=下载)](https://github.com/BUNNY-19C/DLSSG-30s-manager/releases/latest)
[![License](https://img.shields.io/github/license/BUNNY-19C/DLSSG-30s-manager?style=flat-square)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D4?style=flat-square)](#)
[![GPU](https://img.shields.io/badge/GPU-RTX%2030%20%E7%B3%BB%20(SM86)-76B900?style=flat-square)](#)

> [!NOTE]
> **本项目由 AI 开发。** 代码、界面文案、文档、测试都由 AI 生成，维护者负责真机实测与发布。文中数字都来自实际运行，但 AI 产出难免有错漏——发现问题请[开 issue](../../issues)。

给 [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 做的图形化管理器：按游戏部署 mod、一键恢复，不用手工往游戏目录里拷 DLL。mod 本身是个 DLL 代理——把代理 DLL 和 `dlssg_sm86.ini` 放到游戏渲染 EXE 旁边，RTX 30 系（SM86）就能用上 DLSS 帧生成。

> [!IMPORTANT]
> **两个游戏特有事项**
>
> - **怪物猎人荒野**必须先装前置 [REFramework](https://github.com/praydog/REFramework)：下载它的 `MHWILDS.zip`，把 `dinput8.dll`、`openvr_api.dll`、`openxr_loader.dll`、`reframework\` 解压到游戏根目录。不装前置，装上 mod 后游戏必崩（实测）。
> - **绝区零**要在「代理入口」里选 `d3d12.dll`——`version.dll` 这类名字会被它的反作弊改名隔离。这个 DLL 管理器会自己下载。

## 下载

到 [Releases](../../releases/latest) 拿，两个文件任选，都不需要装 .NET：`DLSSGManager-*-setup.exe`（安装包，安装路径可选、带卸载）或 `DLSSGManager.exe`（绿色版，单文件）。

Mod 文件（约 101 MB）不随安装包分发，安装过程也不联网：**首次启动时程序会自动检测上游版本并取回**，之后点「下载 / 更新 Mod 文件」更新。

## 用法

1. 「扫描 Steam 库」或「添加游戏…」。程序靠 `nvngx_dlssg.dll` 认游戏，顺带扫一遍反作弊。
2. 选中游戏，点「部署到该游戏」。
3. 不想要了点「一键恢复」。批量操作在窗口底部。

> [!WARNING]
> **带内核级反作弊的游戏有账号风险。** 反作弊可能拦截并隔离代理 DLL，检测记录可能危及账号。程序会检测到并提示风险，是否部署由你决定。

## 说明

- **入口名**：默认 `version.dll`，另有 `winmm`、`dinput8`、`dbghelp`、`dxgi`、`d3d12` 五个备用。某个名字被别的 mod 占了，程序自动换一个。
- **配置**按游戏独立保存（启用帧生成、优化内核、渲染预设、倍率上限、日志级别），部署时写进 `dlssg_sm86.ini`。
- **恢复**只删签名和哈希都对得上的文件；被占用的原文件会先备份到 `%APPDATA%\DLSSGManager\restore\`。
- **手工装过**的可以被「接管」纳入管理，包括手工放进去的社区版 `d3d12.dll`（按哈希识别）。
- **显卡**：给 RTX 30 系（作者在 3080 Ti 上实测）；40/50 系原生支持帧生成，用不上。显存会涨，4K 约 +700–770 MiB。
- **出问题**先看游戏目录里的 `dlssg_sm86\logs`，或点「查看 Mod 日志」。界面支持深色/浅色与中英文切换。

## 从源码

需要 .NET 8 SDK；构建、测试、打包命令见 [CONTRIBUTING.md](CONTRIBUTING.md)。仓库不含 mod 二进制，首次运行会自动获取（原因见 [docs/mod-files.md](docs/mod-files.md)）。

## 授权

代码是 [MIT](LICENSE)。**MIT 不覆盖 dlssg_for_sm86 发布的任何文件**，也不覆盖 `extra-proxies/` 下的参考副本——那些属于各自的权利人，本项目只按需下载，不转发、不再授权。

使用前请读上游说明，尤其是杀软误报、显存占用和反作弊相关的限制。

## 参与

欢迎提交实测结果和改进，见 [CONTRIBUTING.md](CONTRIBUTING.md)。本程序只是 mod 的**部署工具**，帧生成本身的问题（画质、性能、兼容性）请找 [mod 作者](https://github.com/sdli1995/dlssg_for_sm86/issues)。
