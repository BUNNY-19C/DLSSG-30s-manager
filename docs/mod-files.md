# 为什么仓库里没有 mod 文件

运行管理器需要上游 [dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 发布的文件：

```
mod/
├─ version.dll              ← 默认代理入口
├─ dlssg_sm86.ini           ← 默认配置
├─ altnative/               ← 四个备用入口
│  ├─ winmm.dll
│  ├─ dinput8.dll
│  ├─ winhttp.dll
│  └─ dxgi.dll
└─ config/presets/          ← 两档预设（可选）
   ├─ sm86-default.ini
   └─ sm86-performance.ini
```

`altnative/` 里还可以放**你自己添加的入口 DLL**（例如你自己编译的构建）：管理器不会从上游下载它们，但你在界面上点「添加代理 DLL…」时就会按原文件名放进这个目录，之后和自带入口一样可部署、可恢复。

0.3.0 发布 6 个入口：根目录的 `version.dll` 与 `alternatives\` 下的 5 个。管理器把源文件夹里的 `alternatives\` 映射到自己的 `altnative\`，本地布局不变。旧版本发布过的 `winhttp.dll` 会在更新时被清理掉（只删带本项目签名的文件），免得它看起来像是你自己加的入口。详见 [../extra-proxies/README.md](../extra-proxies/README.md) 与 README 的「入口名与自定义 DLL」。

这些文件**不提交到本仓库**，原因有两个。

## 1. 授权

它们是 mod 作者编译发布的二进制，不是本项目的代码。其 `THIRD_PARTY_NOTICES.txt` 里写明：

> The NVIDIA runtime DLL and extracted/recompiled NVIDIA kernel assets are separate
> third-party material from this user's local installation; they are not relicensed by LICENSE.md.

也就是 NVIDIA 运行时和从中提取、重编译的内核资源**不在该项目可再授权的范围内**。本仓库因此不转发这些材料——需要的人从上游获取即可。

## 2. 体积

五个 DLL 合计约 75 MB，放进 git 历史既臃肿，也意味着每次上游更新都要多一份副本。

## 怎么获取

**方法一：程序自动获取（推荐）**

安装包：安装阶段**不下载**（安装程序不联网，免得下到一半失败留下半成品）。装完后第一次启动管理器，它会自动检测上游最新版本并取回，日志里会写明版本号（例如「上游最新版本：Native 0.2.4」）。

图形界面：启动管理器，点工具条上的「**从 GitHub 更新 Mod 文件**」。程序会依次尝试多个源（codeload → GitHub API → raw → jsDelivr CDN），直到有一个成功，随后自动识别版本号并显示（例如「可用 · Native 0.2.4」）。

命令行：如果你只想要文件、暂时不构建界面，可以用测试工具里的同一个下载器：

```bash
cd test/Harness
dotnet build -c Release
./bin/Release/net8.0-windows/Harness.exe --fetch
```

它写入的目录与界面按钮一致，之后运行不带参数的测试即可执行全部用例。

如果检测到文件源不可用，程序会在运行输出里说明缺什么。

**方法二：手动放置**

从 <https://github.com/sdli1995/dlssg_for_sm86> 下载（Clone 或 Download ZIP），把根目录的 `version.dll`、`dlssg_sm86.ini`，以及 `altnative/`、`config/presets/` 两个目录复制到 `mod/` 下。保持原目录结构，程序按固定文件名和相对路径识别，无需额外配置。

`mod/` 的位置规则：

| 情况 | 位置 |
|---|---|
| 程序目录可写（大多数情况） | 程序旁的 `mod\` |
| 程序目录不可写（装在 `Program Files` 且未提权） | `%APPDATA%\DLSSGManager\mod` |

**方法三：指向别处**

程序也接受任意位置——把文件放好后，改 `%APPDATA%\DLSSGManager\library.json` 里的 `ModSourcePath` 指向该目录即可。

## 下载器的网络约束

内置下载器只访问白名单内的 HTTPS 地址，并且在请求前逐个校验解析出的 IP：

- 允许的域名：`github.com`、`codeload.github.com`、`raw.githubusercontent.com`、`api.github.com`、`cdn.jsdelivr.net`
- 拒绝环回、内网、CGNAT、链路本地、多播与保留地址
- 重定向的每一跳都重新校验
- 响应体积有上限，压缩包条目不允许逃出目标目录

每个源失败会重试一次再切换到下一个，因此个别端点中断或受限只会导致降级，不会让更新整体失败。

## 下载内容的校验

Mod 是会被放进游戏目录的原生 DLL，所以下载路径按不可信处理。落盘之前会验证：

1. **必须存在**：`version.dll`、`dlssg_sm86.ini` 与 `altnative/` 下的四个备用入口；
2. **必须签名**：每个 DLL 都要带项目证书的有效 Authenticode 签名。注意这验证的是签名与文件内容是否匹配——改动任意一个字节都会让签名失效，因此能挡住中间方替换文件；
3. **证书指纹必须一致**：记录值为 `A994735E6A7E9AA31FA926B3023B7C487DAB4850`（Native 0.2.4 的五个 DLL 共用）。

第 3 条对镜像源是硬性要求——镜像不是内容的权威，指纹不符即拒绝。对 GitHub 官方源则记录警告后放行，以免上游更换自签名证书后更新功能失效。

校验失败不会写入目标目录，程序会报告具体是哪个文件没通过。

