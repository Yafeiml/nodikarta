<picture>
  <source media="(prefers-color-scheme: dark)" srcset="src/ProjectHub.Core/wwwroot/brand-mark.svg">
  <img src="src/ProjectHub.Edge.Desktop/Assets/edge.svg" width="64" height="64" alt="">
</picture>

# Nodicarta

English · [简体中文](README.zh-CN.md)

**If ten AI agents each do their job well, will their work still add up to the product you meant to build?**

When you work with one assistant, you can explain a change of direction in one conversation. Once researchers, developers, writers and support staff each have an agent on a different machine, someone has to explain the change to all of them. Leave out the words “pilot customers only” and the engineers can ship exactly what you asked for while a beautifully written announcement promises it to everyone.

I built Nodicarta so **the project could have a memory of its own**. Who are we building for? What did we promise them? What did we learn that made us change our minds? People and agents can record those decisions with the evidence and the exact files they relied on. Months later, someone working with a different model on another machine can draw on those earlier trade-offs to make a call nobody thought to spell out.

Imagine building a field notebook. Its first manual said photos stayed on the device. During a field trial, surveyors asked to send *a few photos they picked* to a colleague, so the team added sharing. Now a design agent has to decide whether inviting a teammate should upload the whole album by default. The trial notes and the two decisions give the designer good reason to leave sharing up to the photographer. How long a shared photo should be kept is still an open question for the team.

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

### 3. Give a new agent a decision to make

Try this in a test project. In a chat on the development PC, ask the assistant to record the original decision.

> For the first release of Field Notebook, photos stay on the device because field crews sometimes photograph private land. That's what the first manual tells users. Save the decision and its reason to Nodicarta, and tell me which entry and revision Core saved.

Once Core confirms the save, give the same chat the outcome of the field trial.

> Surveyors in the pilot needed to send selected photos to colleagues. The team approved sharing photos the user picks and confirms. Everything else stays on the device by default. Revise the same entry with the pilot feedback as evidence and keep the original revision.

On a second PC with **read-only** access to the project, bind an empty folder in Edge and connect your AI client. Start a fresh chat in that folder and ask this question without copying over the two earlier prompts.

> I'm designing the screen for inviting a colleague to a field crew. What should happen to photos already on the device when the colleague joins? Why did the first manual say photos stayed on the device? Do we have enough evidence to set a retention period for shared photos? Cite the revisions behind your answer.

Look for three things in the answer. Sharing stays in the user’s hands, the old manual made sense at the time, and the team has yet to decide on a retention period. You can also try this in a fresh chat from the original bound folder.

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
