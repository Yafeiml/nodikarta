<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# 脉络 · Nodicarta

[English](README.md) · 简体中文

我希望重启开发机调试程序时，负责回客户消息的 AI 还能照常工作。写代码、查资料、做运营、做客服，这些活本来就可以分到几台电脑上。麻烦在于，产品要帮谁解决什么问题、某个功能为什么这样设计，往往只有开发机上的那段聊天说得清。换到另一台电脑，又得从头讲一遍。

这就是我做脉络的初衷。把项目记录放在一台常开的 **Core** 服务器上，每台电脑上的 AI 都能按授权查到同一份资料。比如，开发这边修好了 CSV 导出时丢备注的问题，AI 记下「复测通过，还没发布」和对应的测试记录。哪怕我出差带走了开发机，运营电脑上的 AI 也能据此起草下一版的更新说明，客服电脑上的 AI 则知道，今天还不能跟客户说「已经修好了」。

## 它如何工作

- **Core** 运行在 Linux x86-64 主机上，用 PostgreSQL 保存项目知识、修订历史和版本化附件。管理员在网页上决定每台设备可以读写哪些项目。
- **Edge** 装在各台 Windows x64 电脑上。把电脑和 Core 配对，再把一个本机目录绑定到项目。在这个目录里开聊天，AI 客户端就能通过 Edge 提供的 MCP 工具查询或提交项目知识。

各台电脑做什么，由你安排。它们的目录名称和内容都可以不同。客服电脑可以只绑定一个空目录，给它这个项目的只读权限就够了。

## 快速开始

### 1. 启动 Core

选一台开发机关机后仍会在线的主机。在装有 Git、Docker 和 Compose 插件的 Linux x86-64 主机上执行以下命令。Compose 会拉取 `ghcr.io/yafeiml/nodikarta-core:1.0.0` 并启动 PostgreSQL。请保留部署目录，后续升级还会用到。

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

在这台主机上打开 `http://127.0.0.1:8080/`，用 Core 日志中的一次性首装码创建管理员账号。默认地址只在本机可用。给其他电脑配对前，先为它们配置能访问的 Core 地址。远程访问、备份和复用已有 PostgreSQL 的方法见[中文部署指南](deploy/public/README.zh-CN.md)。

### 2. 接入一台 Windows 电脑

1. 在 Core 的「项目」里创建项目。进入「设备 → 添加设备」，给第一台电脑授予该项目的「读写」权限，再生成配对码。配对码十分钟内有效，只能兑换一次。
2. 下载并运行 [Windows x64 安装包](https://github.com/Yafeiml/nodikarta/releases/download/v1.0.0/Nodicarta-Edge-1.0.0-win-x64-setup.exe)。打开 Edge，填入 Core 地址和配对码。
3. 在 Edge 选中项目，点「添加目录」，选择你准备在 AI 客户端里打开的文件夹。
4. 到「智能体接入」中接入 Codex，再从绑定的目录新建聊天。如果客户端提示信任工作区或重新加载 MCP，按提示操作。这里也有 Claude Code 和 Antigravity 的接入入口。

### 3. 换台电脑问同一个项目

可以用一个测试项目试试。在开发机的绑定目录里新建 `export-test.md`，把下面这条示例记录放进去。

> 当前正式版导出 CSV 时，客户备注会丢掉第二行。开发版已经修复，用两行备注复测通过，修复尚未发布。

在开发机的聊天里，请 AI 把结论记下来。

> 把这次 CSV 导出的复测结果和发布状态记到脉络，注明依据是 export-test.md，并告诉我 Core 保存的修订版本。

收到 Core 的保存回执后，把第二台电脑和 Core 配对，给它同一项目的「只读」权限。在 Edge 里绑定一个空目录，接入你要用的 AI 客户端。从这个目录开一个新聊天，问它一个客服会遇到的问题。

> 客户说导出的备注少了一行。今天我该怎么答复他？依据是什么？

看它能否分清「正式版仍有问题」和「开发版复测通过」，并指出依据是 `export-test.md`。如果手边只有一台电脑，也可以先在原绑定目录开一个新聊天试问。引用路径指向开发机上的那份记录。如果还要让别的电脑下载测试报告，可以把 PDF 作为项目附件发布。

## 日常使用

- **从已有项目开始。** 在绑定目录的聊天里说「初始化知识库，先给我看看准备整理哪些内容」，让 AI 从现有资料里找出决定和依据。
- **留住定稿时用的原件。** 纸质海报可以关联打样时确认的单色 Logo。下次活动上传了彩色新版，旧稿用的那一版仍然能下载。支持 PNG、JPEG、WebP、SVG、PDF，单个附件最大 8 MiB。
- **确认资料是否已经同步。** Core 暂时连不上时，支持离线排队的知识提交会留在本机，恢复连接后由 Edge 重试。收到 Core 回执后，其他获授权设备就能查到这一版。
- **让常用电脑登录后就绪。** 在 Edge「设置」中开启「登录时启动 Edge」，登录 Windows 后它会静默进入托盘，点击图标即可打开窗口。「自动更新 Edge」控制应用更新，已安装的技能则由「通用 Skills」里的开关控制。

## 从源码构建

构建 Core 时，在装有 Linux 容器引擎的开发机上，从仓库根目录执行 `docker build -t nodicarta-core:dev .`。

构建 Edge 时，在 Windows x64 上安装 .NET 10 SDK，再用 PowerShell 构建桌面和 CLI 项目。

```powershell
dotnet build src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj -c Release
dotnet build src/ProjectHub.Edge/ProjectHub.Edge.csproj -c Release
```

桌面 ZIP 还包含一个原生 MCP 启动器。用 `./deploy/windows/Publish-Edge.ps1 -Desktop` 打包时，需要 PowerShell 7 和目标为 `x86_64-w64-mingw32` 的 MinGW-w64 GCC。

[内置 Skills](skills/) · [Core 源码](src/ProjectHub.Core/) · [Edge 桌面源码](src/ProjectHub.Edge.Desktop/) · [Edge MCP 与 CLI 源码](src/ProjectHub.Edge/) · [MIT 许可证](LICENSE)
