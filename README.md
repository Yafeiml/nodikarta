<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# Nodicarta

English · [简体中文](README.zh-CN.md)

Nodicarta gives your AI clients a shared memory for each project. Save a decision with its evidence and the exact files it refers to. In a fresh chat, even on another computer, ask what was decided, why, and whether it still applies.

For example, a print poster can reference version 1 of a logo and the proof that explains the choice. Later, a display design can adopt version 2. Both decisions remain searchable, and either chat can retrieve the original file used for its own design.

## How it works

| Component | What it does |
| --- | --- |
| **Core** · Linux x86-64 container and PostgreSQL | Stores project knowledge, revision history and versioned attachments. Its web interface manages projects, devices and their read or write access. |
| **Edge** · Windows x64 desktop app | Pairs the device with Core, binds local folders to projects and exposes project tools through the Model Context Protocol (MCP). Eligible knowledge writes can queue locally while Core is offline. |
| **Your AI client** | Uses its own model to work with project knowledge. The bundled Skills cover project initialization, context recovery, evidence-backed updates, shared assets and feedback. |

Different folders on different computers can point to the same Core project. Each computer needs its own device grant and folder binding. When a chat opens in a bound folder, Edge supplies the tools allowed for that project.

## Quick start

### 1. Start Core

On a Linux x86-64 host with Git, Docker and the Compose plugin, copy the deployment files to a new directory you will keep across upgrades. Compose pulls the prebuilt `ghcr.io/yafeiml/nodikarta-core:1.0.0` image.

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

Use the one-time setup code from the Core log to create the administrator account at `http://127.0.0.1:8080/` on that host. The default deployment includes PostgreSQL. To connect a Windows PC to a separate server, configure a Core address reachable from that PC. The deployment guide covers remote access, using an existing database and backups. [Core deployment guide](deploy/public/README.md)

### 2. Pair a Windows device

1. In Core, open **Projects** and create a project. Under **Devices → Add device**, grant that project **read/write** access and generate a pairing code.
2. Download `Nodicarta-Edge-1.0.0-win-x64-setup.exe` from the [1.0.0 release](https://github.com/Yafeiml/nodikarta/releases/tag/v1.0.0) and run it as your Windows user. Open Edge and enter the Core address and device pairing code. The pairing code is single-use and expires after ten minutes.
3. Select the project in Edge, choose **Add folder** and pick the local folder you will open in your AI client.
4. Open **AI clients**, connect Codex, then start a **new chat from the bound folder**. Complete any workspace trust or MCP reload prompt in the client. Edge also offers setup for clients such as Claude Code and Antigravity.

Open **Settings** at the bottom of the Edge window to choose **Start Edge when I sign in**. With it enabled, signing in to Windows starts Edge in the notification area and leaves the main window closed. Click the tray icon to open it. **Update Edge automatically** controls application updates for the installed release; the separate **Skills** setting controls updates to your installed Skills.

### 3. Test a handoff between chats

In the bound folder, create `print-proof.md` with this example evidence.

> Poster proof A was approved with the monochrome logo. The gap between the strokes stayed legible at 18 mm.

In the first chat, save the decision.

> Save the approved poster decision to Nodicarta. Cite print-proof.md as evidence and give me the saved entry and revision.

Once the client reports a Core receipt, open a second chat from the same bound folder.

> Which logo did we approve for the printed poster, and why? Show me the source.

The second chat can use the saved decision and name its source. To try the same test across computers, grant a second device access to the project and bind an empty folder there. A source path identifies the original evidence; to make the file itself available for download, publish a supported attachment to the project.

## Working with an existing project

- **Bring existing work into memory.** Ask “Initialize this project's knowledge base” to extract decisions and references from the current folder. Ask for a preview first if you want to review the proposed entries.
- **Keep design originals.** Publish a PNG, JPEG, WebP, SVG or PDF (up to 8 MiB per file), then link its version to a decision. A later revision keeps the earlier original addressable.
- **Reconnect after time offline.** Edge retries queued knowledge writes when Core is reachable. A local queue acknowledgement means the write is pending; a Core receipt identifies the saved revision.

## Build from source

For a local Core build, run `docker build -t nodicarta-core:dev .` from the repository root on a development machine with a Linux container engine.

For Edge development on Windows x64, install the .NET 10 SDK. Build the desktop and CLI/MCP projects with PowerShell:

```powershell
dotnet build src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj -c Release
dotnet build src/ProjectHub.Edge/ProjectHub.Edge.csproj -c Release
```

The Windows distribution also includes a small native MCP launcher. Building the desktop ZIP with `./deploy/windows/Publish-Edge.ps1 -Desktop` requires PowerShell 7 and MinGW-w64 GCC targeting `x86_64-w64-mingw32`.

[Bundled Skills](skills/) · [Core source](src/ProjectHub.Core/) · [Edge desktop source](src/ProjectHub.Edge.Desktop/) · [Edge MCP and CLI source](src/ProjectHub.Edge/) · [MIT license](LICENSE)
