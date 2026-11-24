# Nodicarta Edge for Windows

解压全部文件后，在 Windows x64 上打开 PowerShell 7。程序内含 .NET 运行时及 SQLite。准备好 Core 地址和管理员网页「设备管理」签发的一次性配对码。

## 当前用户试用

从解压目录执行以下命令。保留这份解压目录，以便同一账户后续启动。

```powershell
$edge = (Resolve-Path .\bin\ProjectHub.Edge.exe).Path
$state = Join-Path $env:LOCALAPPDATA 'Nodicarta\Edge'
& $edge --state $state pair --core 'https://core.example.com'
& $edge --state $state projects
& $edge --state $state bind --project '<项目 UUID>' --path 'D:\Projects\Example'
& $edge --state $state bindings
```

要解除单个本地目录，先用 `bindings` 核对项目、目录和该行的 `workspaceId`，再执行下列命令。若 Core 离线或项目授权已撤销，可用 `bindings --local` 读取本设备保存的目录记录。工作目录内的文件保留，可重新选择项目绑定。

```powershell
& $edge --state $state unbind --project '<项目 UUID>' --workspace '<workspaceId>' --path 'D:\Projects\Example'
```

按终端提示粘贴配对码。Core 根地址支持 HTTP 和 HTTPS。局域网测试可填写提供 Core 服务的主机地址，例如 `--core 'http://192.0.2.10:29480'`。目录绑定保存在本机，设备失去项目授权后，该项目会从有效绑定中消失。管理员移除又重新授予同一项目时，全局 Codex 接入需重新连接 MCP；显式指定绑定的客户端用 `bindings` 取得新的绑定 ID 并更新配置。运行 `status` 可查询设备和连接状态。

## 设备级客户端接入

从 Windows 桌面版的设备概览或托盘菜单打开「智能体接入」。常用客户端可逐项接入、复制配置，也可一键接入已安装的客户端。Codex 首次接入时还会安装通用 Skills。可在第三个标签「通用 Skills」查看已安装数量、检查版本和设置自动更新。其他支持本地 MCP 的客户端可在「其他客户端 · MCP」点「复制 MCP 配置」取得 JSON。Edge 按客户端提供的目录核对项目授权及工具范围，未绑定目录只能查询接入状态。

后续增加项目或目录时，只需绑定本机目录并从该目录打开聊天。清理重测、Skills 版本及升级行为见[桌面使用说明](../../docs/operations/WINDOWS_EDGE_DESKTOP.md)。

## 指定绑定的 MCP 客户端

在客户端新增 STDIO MCP 服务，程序填写上述 Edge 可执行文件的完整路径。参数依次填写 `mcp-stdio`、`--state`、状态目录、`--project`、项目 UUID、`--binding`、绑定 UUID。默认只读；要提交知识或反馈，可另加 `--mode readWrite`，并由管理员在网页授予该设备相应权限。

需要通过 CLI 启动回环 HTTP 时，使用同一组项目绑定参数，将子命令换成 `mcp-http`，加上 `--workspace <工作区 UUID>` 和 `--port <端口>`。可从 `bindings` 结果取得该目录的 `workspaceId`。在进程环境中设置独立的随机 `PROJECTHUB_MCP_TOKEN`（至少 32 个字符）。客户端连接 `http://127.0.0.1:<端口>/mcp` 并带上该 Bearer 凭据。每个监听固定服务一个目录，收紧目录范围会在下一次列举或调用工具时生效。

通过 `get_feedback` 可以按 ID 读回原反馈与结果。需要查询、领取或提交分析任务的客户端，再加 `--endpoint <本地已绑定端点 UUID>`；`list_tasks` 限于这个端点。`claim_analysis_task` 与 `submit_task_result` 还需要读写权限。Core 暂不可达时，知识和反馈提交可能返回 `queuedLocally`。Edge 的服务或前台 `run` 恢复连接后会重验授权并发送待同步内容，也可使用 `sync --project <UUID> --binding <UUID> --mode readWrite` 手动触发。

CLI 包的 `skills` 和桌面包的 `bin/skills` 随附 `nodicarta-init`（初始化资料）、`nodicarta-context`（恢复上下文）、`nodicarta-sync`（知识提交）、`nodicarta-inbox`（反馈和任务）、`nodicarta-assets`（共享素材）。接入后在已绑定目录打开 Codex 聊天，说「初始化项目资料」或点名 `$nodicarta-init`。也可说「初始化知识库」单独整理知识，或「初始化资料库」整理文档索引和可共享素材。其他客户端如需手工安装，将完整 Skill 文件夹放入客户端支持的发现目录。

## 普通文字反馈与分析

1. 开发端先登记一个人工领取端点，记下 Core 返回的 `endpointId`。这一步只需本项目的读写设备，无需绑定已有聊天。

   ```powershell
   & $edge --state $state register-endpoint --project '<项目UUID>' --binding '<开发端绑定UUID>' --name '开发端收件'
   ```

2. 客服端在自己的读写 MCP 中调用 `list_agent_endpoints`，核对同项目的目标。调用 `submit_feedback`，填写新的 UUID 作为 `requestId`、反馈原文 `content` 和目标 `targetEndpointId`。记下反馈和任务 ID。
3. 开发端的读写 MCP 启动参数配置 `--endpoint <刚登记的端点UUID>`，使用 `list_tasks` 定位任务。用 `claim_analysis_task` 携任务 ID 和新的请求 ID 领取。核对原文、完成只读分析，再用 `submit_task_result` 携同一任务 ID、新的请求 ID 及实际结论回传。
4. 客服端以原来的反馈 ID 调用 `get_feedback`，查看关联状态和开发端结果。同一次操作重试应沿用该操作原来的请求 ID。

## 随系统启动与当前用户派工

如需系统启动时保持 Core 连接，以管理员身份在包目录运行 `.\Install-Edge.ps1 -OwnerAccount '计算机名\桌面用户名'`。脚本给出安装的程序位置和状态目录。服务的默认数据目录为 `%ProgramData%\Nodicarta\Edge`。已有上述试用实例时，可传入原来的 `-StateDirectory $state` 继续使用同一份身份和绑定。Windows 服务使用虚拟账号 `NT SERVICE\ProjectHubEdge`。可通过 `Get-Service ProjectHubEdge` 和状态目录内的 `connection.json` 核对服务及最近一次连接。

指定已有 Codex 会话的绑定和派工由实际登录 Codex 的桌面用户执行。该用户先运行 `codex-link --project <UUID> --path <工作目录> --codex <codex.exe绝对路径> --thread <会话UUID> --name <端点名称>`，使用对应的 `--state`。绑定成功后，在**普通用户** PowerShell 中执行：

```powershell
.\User-Dispatch.ps1 -Action Enable -StateDirectory $state -EdgeExe $edge
.\User-Dispatch.ps1 -Action Status -StateDirectory $state
```

`Enable` 立即启动，并在当前用户下次登录时启动。只运行本次可用 `Start`。用 `Stop` 停止本次进程，`Disable` 同时取消当前用户登录启动。注销后需等待下次登录才能继续处理桌面会话。

升级服务时解压完整的新包，用同一个拥有者和状态目录再次运行安装脚本。当前用户的派工程序可先 `Disable`，升级后改用新程序路径重新 `Enable`。卸载系统服务执行 `.\Uninstall-Edge.ps1`；数据目录和此前版本仍可用于恢复。停用机器时在 Core 网页撤销设备。若轮换中断被标记为结果未知，先在网页核对并撤销旧设备，再以新配对码运行 `pair --replace`，为新身份重新绑定目录。
