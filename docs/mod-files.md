# Mod 文件与获取方式

管理器从上游 [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86/releases/tag/0.4.0) 获取 **0.4.0** 负载。二进制不提交到本仓库，也不随管理器程序分发；上游和 NVIDIA 材料的权利仍归各自权利人。

## 当前布局

```text
mod/
├─ version.dll
├─ dlssg_sm86.ini
├─ altnative/                   # 上游 alternatives/ 下的五个备用入口
│  ├─ winmm.dll
│  ├─ dbghelp.dll
│  ├─ dinput8.dll
│  ├─ dxgi.dll
│  └─ d3d12.dll
└─ dlssg_sm86/
   ├─ backend/sm86_backend.dll
   ├─ runtime/nvngx_dlssg.dll
   ├─ kernels/fg_kernels.dll
   ├─ xefg/xefg_kernels.dll
   └─ sr/                      # 仅 Release 完整包包含
      ├─ nvngx_dlss.dll
      └─ sr_kernels.dll
```

部署到游戏时只选择**一个**代理，加上 INI 和运行目录；不会复制全部备用入口。

## 获取

首次启动自动下载，之后可点「下载 / 更新 Mod 文件」。默认优先尝试官方 Release 完整包及其加速源，下载约 77 MiB，含超分文件；逐文件源下载约 37 MiB，仅支持插帧与 XeSS，不能开启 INT8 超分。

下载与游戏应用分开：先更新本地负载，再选择游戏并应用。已有 310.1 负载保留在 `mod/variants/310.1/`，可本地使用或恢复；0.4.0 已停止提供该构建，管理器不再下载它。

命令行可运行同一下载器：

```powershell
dotnet run --project src/DLSSGManager -c Debug -- --fetch --silent
```

手动导入时，从上游 Release 下载完整 ZIP，把 `version.dll`、`dlssg_sm86.ini`、`dlssg_sm86/` 放入 `mod/`，把 `alternatives/` 的 DLL 放入 `mod/altnative/`。不要直接使用 GitHub 的源码 ZIP 来获取超分文件。

程序优先使用自身旁边的 `mod/`；不可写时使用 `%APPDATA%\DLSSGManager\mod`。源码检出从仓库根目录的 `mod/` 读取。自定义位置可在 `library.json` 的 `ModSourcePath` 中指定。

`altnative/` 也支持用户自行导入的代理。导入文件不由管理器背书，必须自行确认与当前运行目录兼容。

## 校验与恢复

- 所有请求必须为白名单主机上的 HTTPS；重定向逐跳复检。系统代理路径沿用代理感知策略。
- 响应体积有上限，ZIP 解压不能逃出临时目录。
- 必需文件齐全后，逐一验证 DLL 的 Authenticode 签名完整性。项目文件要求项目签名；NVIDIA 的两个运行库要求 NVIDIA Corporation 签名。
- 镜像中的项目文件还必须匹配证书指纹 `85BA66762F851E49148D706915D09026281418E6`。官方源在项目换证书时记录警告；NVIDIA 证书不使用这个项目指纹。
- 校验通过后才发布本地负载。版本与构建分别记在 `.manager-version`、`.manager-build`。

游戏中的运行文件逐一记录路径和哈希。恢复只删除可证明归属的文件，并还原备份；未知文件与无法识别的替换文件保留。删除日志仅清理新版的 `dlssg_sm86/logs/`，不递归删除整个运行目录。
