<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# 脉络 · Nodicarta

[English](README.md) · 简体中文

**如果十个智能体都把自己的任务做对了，最后做出来的还是你想做的那个产品吗？**

和一个 AI 做项目，改主意只要解释一次。等研发、调研、运营和客服各有自己的智能体，分散在几台电脑上，改一次决定，就得有人去每个地方讲清楚为什么改、改了哪一部分。只要漏掉一句「目前只对试点用户开放」，研发可以把功能做得完全正确，运营也可以写出一篇好公告，用户读到的却是团队从未打算许下的承诺。

我做脉络，是想让**项目有一份自己的记忆**。我们在为谁解决什么问题，曾经向用户承诺过什么，什么证据让我们改了主意，这些判断值得跟着项目留下来。参与者可以把决定、依据和当时采用的文件版本记在一起。几个月后，即使换了人、设备或模型，新加入的智能体也能用前人做过的取舍回答一个过去没人问过的问题。

比如，你在做一款野外调查工具，第一版手册写着「照片只存在本机」。试点时，调查员想把**自己选中的几张**发给同事，于是团队加了分享功能。半年后，另一个智能体开始设计群组邀请。新同事一加入，就该把整本相册传给他吗？读过两次决定和试点记录，它就有理由保留一个关键的设计选择，让拍照的人决定分享哪几张。至于分享出去的照片保存多久，团队还没定，值得单独讨论。

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

### 3. 让新加入的智能体做一次判断

在一个测试项目里，从开发机的绑定目录打开聊天，把第一版产品的取舍记下来。

> 这是「野外手记」项目的首版决定。调查照片可能拍到私人地块，所以照片只保存在本机，第一版手册按这条规则编写。请把决定和原因记到脉络，告诉我 Core 保存的条目和修订版本。

收到 Core 的回执后，在同一聊天里补上试点后的变化。

> 试点时，调查员需要把选中的照片发给同组同事。团队已决定允许用户手动选择照片并确认分享，其余照片默认留在本机。请把试点反馈作为依据，将刚才的条目修订为现行决定，并保留首版历史。

现在换一台只有该项目读取权限的电脑。在 Edge 中绑定一个空目录，接入 AI 客户端，从这个目录开一个新聊天。只把下面这个设计问题交给它。

> 我在设计邀请同事加入调查小组的界面。邀请完成后，该怎么处理设备里已经拍好的照片？第一版手册为什么说照片只存在本机？如果还要定一个共享照片的保存期限，项目里有足够的依据吗？请列出你参考的修订。

看它会不会保留用户主动选择分享的权利，能不能解释旧手册当年的依据，又能否说清保存期限还没有定。手边只有一台电脑时，也可以从同一绑定目录另开聊天试问。

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
