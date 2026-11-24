# 脉络 Edge · Linux x86_64 桌面版

## 启动

Ubuntu 或 Debian 桌面可在下载目录运行以下命令，再从应用菜单打开「脉络 Edge」。从 Linux 预览版升级时，请先关闭旧版窗口。

```sh
sudo apt install ./nodicarta-edge_1.1.0_amd64.deb
```

其他使用 glibc 2.38 或更高版本的 x86_64 桌面可将便携包解压到固定位置，以当前桌面用户启动。

```sh
tar -xzf Nodicarta-Edge-1.1.0-linux-x64.tar.gz
./Nodicarta-Edge-1.1.0-linux-x64/ProjectHub.Edge.Desktop
```

桌面程序和 MCP/CLI 均使用 .NET 10 Native AOT 编译，用户无需安装 .NET。图形界面使用 X11，Wayland 会话需要 XWayland。便携包依赖系统的 ICU、OpenSSL 3、字体、Fontconfig 和 X11 客户端库。中文显示需要含中文字形的字体。

## 测试

1. 在已有的脉络 Core「设备管理」中生成一次性配对码。在 Linux 窗口填写该 Core 的地址和配对码。Core 在另一台机器上时，请使用从 Linux 设备可以访问的地址。
2. 选择授权项目，添加已有的本机目录。可以选取目录，也可以直接输入绝对路径。
3. 选择「复制 MCP 配置」，粘贴到支持本地 STDIO 的智能体，在绑定目录中启动该客户端并重新加载连接。本版本提供手动配置，便携包移动后请重新复制配置。
4. 关闭窗口会退出桌面程序。重新打开可检查配对与目录绑定是否保留。Linux 版通过手动安装更新。

请使用同一个普通账户配对和运行 Edge，将状态保存在本机 Linux 文件系统。设置了绝对路径 `XDG_STATE_HOME` 时，数据位于 `$XDG_STATE_HOME/Nodicarta/Edge`。默认使用 `~/.local/state/Nodicarta/Edge`；首次启动时，若上级目录允许其他账户写入且原位置没有数据，Edge 会使用 `~/.Nodicarta/Edge`。升级后继续使用已有数据的位置。

## English

When upgrading from a Linux preview, close its window first. Install the Debian package with the command above and open **Nodicarta Edge** from the applications menu. For the portable archive, extract it to a permanent location and run **ProjectHub.Edge.Desktop** as your desktop user. The desktop app and MCP/CLI are Native AOT executables for x86_64 and require glibc 2.38 or later. The GUI uses X11 or XWayland.

Create a pairing code in your Core, enter its address in Edge, select an authorized project and add its local folder. **Copy MCP config** gives you a local STDIO configuration to paste into your client. Start the client in the linked folder. Closing Edge exits the desktop app. Install the next Linux release manually.

An explicit absolute `XDG_STATE_HOME` stores data in `$XDG_STATE_HOME/Nodicarta/Edge`. By default, Edge uses `~/.local/state/Nodicarta/Edge`. On first start, if other users can write to its parent directories and the original location is empty, Edge uses `~/.Nodicarta/Edge`. Subsequent launches use the existing data location.
