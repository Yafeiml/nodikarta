<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# 脉络 · Nodicarta

[English](README.md) · 简体中文

脉络让不同 AI 客户端共用同一个项目的记忆。把一次决定、当时的依据和用到的文件版本存下来。换一台电脑、开一个新聊天，仍然可以问清当时选了什么、为什么选，以及这个决定今天是否适用。

例如，旧海报采用了第一版 Logo，印样单记录了选择它的理由。后来橱窗电子屏采用了第二版 Logo。两个决定都可以查到，各自引用的原件也可以按当时的版本下载。

## 它如何工作

| 组件 | 职责 |
| --- | --- |
| **Core** · Linux x86-64 容器及 PostgreSQL | 保存项目知识、修订历史和版本化附件。管理员在网页上创建项目、配对设备，并按项目授予读写权限。 |
| **Edge** · Windows x64 桌面客户端 | 连接 Core，把本机目录绑定到项目，通过模型上下文协议（MCP）向 AI 客户端提供工具。Core 离线时，支持排队的知识提交可暂存在本机。 |
| **你使用的 AI 客户端** | 用自己的模型整理、查询项目资料。随 Edge 提供的 Skills 涵盖项目初始化、找回上下文、保存知识和依据、共享素材及处理反馈。 |

不同电脑上的不同目录可以指向同一个 Core 项目。每台电脑分别取得设备授权、绑定本机目录。聊天从绑定目录打开后，Edge 按项目授权提供可用工具。

## 快速开始

### 1. 启动 Core

在装有 Git、Docker 和 Compose 插件的 Linux x86-64 主机上，把部署文件放入一个新目录，后续升级继续使用该目录。Compose 会拉取预先构建的镜像 `ghcr.io/yafeiml/nodikarta-core:1.0.0`。

```sh
git clone --branch v1.0.0 --depth 1 https://github.com/Yafeiml/nodikarta.git
sudo mkdir -p /home/docker/nodikarta
sudo cp -a nodikarta/deploy/public/. /home/docker/nodikarta/
cd /home/docker/nodikarta
sudo sh init.sh
sudo docker compose pull
sudo docker compose up -d --no-build
sudo docker compose logs --tail=50 core
```

在该主机打开 `http://127.0.0.1:8080/`，用 Core 容器日志中的首次安装码创建管理员账号。默认部署包含 PostgreSQL。若 Windows 电脑与服务器是两台设备，请为 Core 配置这台电脑可以访问的地址。远程访问、复用已有数据库和备份均见部署指南。[Core 部署指南](deploy/public/README.zh-CN.md)

### 2. 配对 Windows 设备

1. 在 Core 网页的「项目」中创建一个项目。进入「设备 → 添加设备」，为这个项目选择「读写」并生成配对码。
2. 在 [1.0.0 发布页](https://github.com/Yafeiml/nodikarta/releases/tag/v1.0.0)下载 `Nodicarta-Edge-1.0.0-win-x64-setup.exe`，用当前 Windows 账号运行。打开 Edge，填入 Core 地址和设备配对码。配对码有效期为十分钟，成功兑换后即失效。
3. 在 Edge 选中该项目，点击「添加目录」，选择你要在 AI 客户端打开的本机文件夹。
4. 打开「智能体接入」，接入 Codex，再**从已绑定的目录新建聊天**。如果客户端提示信任工作区或重新加载 MCP，按提示操作。Edge 也提供 Claude Code、Antigravity 等客户端的接入入口。

打开 Edge 主窗口底部的「设置」，可选择「登录时启动 Edge」。开启后，下次登录 Windows 时 Edge 会静默进入托盘，点托盘图标才打开主窗口。同页的「自动更新 Edge」控制安装版应用更新；「通用 Skills」中的开关单独控制本机 Skills 的更新。

### 3. 测试新聊天能否接手

在绑定目录内创建 `print-proof.md`，写入下面这条示例依据。

> A 版海报纸样采用单色 Logo。缩小到 18 毫米时，两条笔画之间的留白仍然清晰。

在第一个聊天里保存这个决定。

> 把这次海报定稿决定记到脉络，引用 print-proof.md 作为依据，告诉我已保存的条目和修订版本。

收到 Core 的保存回执后，从同一个绑定目录开一个新聊天，试着这样问。

> 上次纸质海报定稿用了哪个标志，为什么选它？给出依据。

新聊天可以找回刚才的决定并指出证据来源。要测试跨设备接手，可以给第二台电脑授权同一个项目，在那台电脑绑定一个空目录后再问。来源路径用于追溯原始资料；需要另一台电脑也能下载文件时，请将支持格式的原件发布为项目附件。

## 用在已有项目

- **整理已有资料。** 说「初始化知识库」，AI 会从当前目录的资料中整理决定和依据；需要先核对内容时，可以要求「先给我预览」。
- **保留设计原件。** 可以发布 PNG、JPEG、WebP、SVG 或 PDF，单个原件最大 8 MiB。决定可以引用附件的指定版本，发布新版本后仍可下载旧版。
- **离线后继续同步。** Core 恢复连接后，Edge 会重试已排队的知识提交。「已在本机排队」表示尚待同步，收到 Core 回执后即可按条目和修订版本查询。

## 从源码构建

要在本机构建 Core，请在有 Linux 容器引擎的开发机上，进入仓库根目录执行 `docker build -t nodicarta-core:dev .`。

要开发 Windows x64 客户端，请安装 .NET 10 SDK，再用 PowerShell 构建桌面端和 CLI/MCP。

```powershell
dotnet build src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj -c Release
dotnet build src/ProjectHub.Edge/ProjectHub.Edge.csproj -c Release
```

Windows 发行包还包含原生 MCP 启动器。用 `./deploy/windows/Publish-Edge.ps1 -Desktop` 构建桌面 ZIP 时，需要 PowerShell 7 和目标为 `x86_64-w64-mingw32` 的 MinGW-w64 GCC。

[内置 Skills](skills/) · [Core 源码](src/ProjectHub.Core/) · [Edge 桌面源码](src/ProjectHub.Edge.Desktop/) · [Edge MCP 与 CLI 源码](src/ProjectHub.Edge/) · [MIT 许可证](LICENSE)
