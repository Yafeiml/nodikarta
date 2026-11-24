# Nodicarta Edge for Windows

## Install and open Edge

Download `Nodicarta-Edge-1.1.1-win-x64-setup.exe` from the project's GitHub Releases and run it under your Windows account. The desktop and MCP/CLI use Native AOT. The installer defaults to `%LOCALAPPDATA%\Programs\Nodicarta\Edge`. Open **Nodicarta Edge** from Start.

Open **Settings** to the left of **Language** in the title bar to manage sign-in startup and automatic Edge updates. Sign-in startup is enabled on a new installation and opens only the tray icon. Click the icon to open the window. The installer preserves your selection across upgrades.

The release installation checks the stable channel automatically. When a newer release is available, close to the tray to allow Edge to verify the publisher's signature and install the update. Shared Skills have a separate update preference on the Skills tab. To migrate from a higher-numbered development build, start the public setup and confirm the migration. To uninstall, use Windows **Installed apps**. Device data is retained.

Edge Desktop stores its device identity, linked folders, unsent records and language preference in `%LOCALAPPDATA%\Nodicarta\Edge`. When upgrading, quit the old desktop app and close Edge processes using its data folder. The new version moves the entire previous default folder, `%LOCALAPPDATA%\ProjectHub\Edge`, including archived Core profiles. If both locations already hold data, the migration stops and preserves both. A custom data folder keeps its original location.

After migration, Explorer may still show the former `ProjectHub\Edge` entry as an NTFS junction. The database files live under `Nodicarta\Edge`. Use this new location for backups.

To select a custom data folder, pass `--state "D:\EdgeData"` or set `NODICARTA_EDGE_STATE`. Existing configurations using `PROJECTHUB_EDGE_STATE` are also supported.

## Pair a device and bind a folder

Ask your Core administrator for the Core root URL and a fresh pairing code with access to your project. Enter the URL, device name, and complete code on the pairing screen. Select an authorized project and add its local folder.

To recover after local data loss or replace a computer, use the update button beside the original device on Core's device page. Enter that code on the replacement Edge and confirm the handover. Core activates the successor and retires the original together, after Edge saves its candidate credentials. Cancellation or expiry before confirmation leaves the original device active. If the connection drops, reopen Edge with the same data folder. Historical authorship stays with the original identity. Bind the replacement computer's own folders after confirmation.

Open **Language ▲** (语言) in the upper-right corner and choose **Auto** (自动), **中文** (Chinese), or **English**. Auto uses the current Windows user's display language. Simplified and Traditional Chinese display languages use the Simplified Chinese interface, and all other display languages use English. A manual choice takes effect immediately and persists across restarts. Choose Auto again to follow Windows. The lower-right corner of the main window shows the running desktop release version.

To change the Core address, select the double-arrow button beside the Core status. Check the local folder and unsent record counts, enter the target URL, and pair with a new code issued by that Core. Edge opens a separate local database for the new pairing. Bind your folders again under the target projects and reconnect open AI clients. Previous bindings, pending records, and endpoint settings stay in the original database. The same steps apply when your Core changes domain. Exit portable copies older than 1.1, which can still use the original device identity. To revoke that device's credentials, use the original Core's device management page.

## Connect or disconnect an AI client

Open **AI clients** from the overview or tray. Connect an installed client from **Popular clients**, or choose **Connect all installed**. In **Other clients · STDIO**, choose **Copy setup instructions** to paste the complete MCP configuration into your agent's chat, or **Copy STDIO configuration** to configure the client manually. Open a chat from a linked folder and reload the client's MCP connection. The **Shared Skills** tab manages the shared Skills and their updates.

Open Claude Desktop once before connecting it in Edge. After connecting, completely quit and reopen Claude. If Claude Code is also installed, each has its own connection. A Claude Desktop session can access project tools when it supplies a single linked local folder. For Pi Desktop, install the Pi engine (0.99 or later), connect in Edge, then select the Pi engine and open a linked folder in Pi Desktop.

To upgrade, run the new installer against the same installation folder. The built-in clients share its fixed `ProjectHub.Edge.exe` entry point. Edge migrates managed settings that reference an older release in that folder. Clients stay connected across subsequent upgrades, and new MCP sessions use the active release. A portable copy launched directly from an extracted ZIP uses the executable in that copy.

Windows runs one resident Edge desktop at a time. Opening it again brings up the existing window. Opening it from a new location takes over the current user's session and updates that device's managed client connections automatically. Edge preserves custom settings, registrations with a different data folder, and registrations it cannot verify.

Skills 1.6 use the names `nodicarta-init`, `nodicarta-context`, `nodicarta-assets`, `nodicarta-inbox` and `nodicarta-sync`. Ask naturally using “脉络” in Chinese or “nodicarta” in another language. Managed Skills update automatically by default. Edge respects a saved preference to turn updates off, recognizes older managed names and preserves personal edits.

To disconnect a client, choose **Disconnect** on its row and confirm. Restarting or upgrading Edge keeps it disconnected. Other MCP servers, custom settings and shared Skills are preserved. Manually pasted configurations in other clients must be removed there. Restart an existing client or reload MCP so it picks up the change.
