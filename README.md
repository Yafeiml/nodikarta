<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# Nodicarta

English · [简体中文](README.zh-CN.md)

I want to be able to reboot my development PC without taking customer support offline. One computer can run an AI assistant that writes code. Others can research an issue, draft release notes or answer customers. But if the reasons behind a feature live in a chat on my PC, I end up explaining them all over again. Sometimes that means going all the way back to who we're building for and what they need.

That's why I built Nodicarta. It keeps a shared project record on a **Core** server that the other computers can reach. Say a developer fixes a bug that drops notes from CSV exports. The development assistant records that the fix passed testing but hasn't shipped. I can take the laptop on a trip. The assistant on the operations PC has what it needs to draft the next release notes, and the one handling support can tell a customer what to expect from the version they're using today.

## How it works

- **Core** runs on a Linux x86-64 host with PostgreSQL. It stores project knowledge, revision history and versioned attachments. In the web interface, you choose which projects each device can read or write.
- **Edge** runs on each Windows x64 PC. Pair the PC with Core and bind a local folder to a project. From a chat in that folder, your AI client uses Edge's Model Context Protocol (MCP) tools to look up or contribute project knowledge.

You decide which work runs on each computer. The folders can have different names and contents. A support PC can start with an empty folder and read-only access to the project.

## Quick start

### 1. Start Core

Choose a host that will stay up when your development PC is off. On Linux x86-64 with Git, Docker and the Compose plugin, run the following commands. Compose pulls `ghcr.io/yafeiml/nodikarta-core:1.0.0` and starts PostgreSQL. Keep the deployment directory for future upgrades.

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

Open `http://127.0.0.1:8080/` on that host. Use the one-time setup code from the Core log to create your administrator account. The default address is local to the host. Before pairing another computer, give it a Core address it can reach. The deployment guide covers remote access, backups and using an existing PostgreSQL database. [Core deployment guide](deploy/public/README.md)

### 2. Connect a Windows PC

1. In **Projects**, create a project. In **Devices → Add device**, give the first PC **read/write** access to that project and generate a pairing code. The code is valid for ten minutes and can be used once.
2. Download and run the [Windows x64 installer](https://github.com/Yafeiml/nodikarta/releases/download/v1.0.0/Nodicarta-Edge-1.0.0-win-x64-setup.exe). Open Edge, enter the Core address and pair with the code.
3. In Edge, select the project and choose **Add folder**. Pick the folder you'll open in your AI client.
4. Under **AI clients**, connect Codex, then start a new chat from the bound folder. Accept the client's workspace trust or MCP reload prompt if one appears. Edge also has setup entries for Claude Code and Antigravity.

### 3. Ask from a second computer

Try this in a test project. On the development PC, create `export-test.md` in the bound folder with the following sample result.

> In the stable release, CSV export drops the second line of a customer's notes. The fix passed the two-line test in the development build. It hasn't shipped yet.

Ask the development assistant to save the finding.

> Record the CSV export test result and release status in Nodicarta. Cite export-test.md and tell me which revision Core saved.

Once Core confirms the save, pair a second PC with Core and grant it **read-only** access to the same project. In Edge, bind an empty local folder and connect the AI client you'll use there. Start a fresh chat from that folder with a support question.

> A customer says their exported notes are missing a line. What can I tell them today, and what are we basing that on?

The reply should distinguish the released version from the tested fix and cite `export-test.md`. You can also try this in a new chat on the first PC. The citation points to the note on the development PC. To share a downloadable test report, publish a PDF as a project attachment.

## Using it day to day

- **Start with an existing project.** From a bound folder, ask “Initialize this project's knowledge base. Show me a preview first.” The assistant can use your existing material to identify decisions and their sources.
- **Keep the file that went with the decision.** A print poster can point to the monochrome logo approved on its proof. Publish a color logo for a later campaign and the original is still downloadable by version. Supported attachments are PNG, JPEG, WebP, SVG and PDF, up to 8 MiB each.
- **Check what has synced.** If Core is unreachable, Edge can queue eligible knowledge writes on the PC and retry when Core is back. Once you have a Core receipt, the saved revision is available to other authorized devices.
- **Set up a PC that stays on duty.** In Edge **Settings**, **Start Edge when I sign in** starts Edge in the Windows notification area. Click the tray icon to open the window. **Update Edge automatically** controls app updates. The **Skills** tab has its own update setting.

## Build from source

For Core, run `docker build -t nodicarta-core:dev .` from the repository root on a development machine with a Linux container engine.

For Edge, install the .NET 10 SDK on Windows x64 and build the desktop and CLI projects in PowerShell.

```powershell
dotnet build src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj -c Release
dotnet build src/ProjectHub.Edge/ProjectHub.Edge.csproj -c Release
```

The desktop ZIP also includes a native MCP launcher. To build it with `./deploy/windows/Publish-Edge.ps1 -Desktop`, use PowerShell 7 and MinGW-w64 GCC targeting `x86_64-w64-mingw32`.

[Bundled Skills](skills/) · [Core source](src/ProjectHub.Core/) · [Edge desktop source](src/ProjectHub.Edge.Desktop/) · [Edge MCP and CLI source](src/ProjectHub.Edge/) · [MIT license](LICENSE)
