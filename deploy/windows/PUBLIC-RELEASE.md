# Windows release

Nodicarta Edge ships a per-user Windows x64 installer containing the Native AOT desktop and MCP/CLI. Building it uses PowerShell 7, the .NET 10 SDK, Visual Studio with the Desktop development with C++ workload and Windows SDK, x64 MinGW-w64 GCC and windres, and NSIS 3 with Unicode support. End users run the setup executable.

## Build and sign

Run the following command on the release workstation. It publishes the desktop and MCP/CLI together, includes the licenses, builds the installer and signs its update manifest. Artifacts are written under `artifacts/edge-public/stable/<version>`.

```powershell
.\deploy\windows\Publish-EdgePublic.ps1 -Version 1.1.0
```

The public verification key is `release/edge-update-public.pem`. The corresponding signing key is opened by name from the current user's Windows CNG Software Key Storage Provider. To re-sign an approved, unchanged installer, run:

```powershell
.\deploy\windows\Sign-EdgeRelease.ps1 -Installer .\artifacts\edge-public\stable\1.1.0\Nodicarta-Edge-1.1.0-win-x64-setup.exe -Version 1.1.0
```

The maintainer publishes these three files on the stable GitHub Release tagged `v1.1.0`:

* `Nodicarta-Edge-1.1.0-win-x64-setup.exe`
* `nodicarta-edge-stable-win-x64.json`
* `nodicarta-edge-stable-win-x64.json.sig`

Stage the complete set before making the release latest. Keep each versioned installer available at its original URL. Every release designated latest on the shared repository must provide the two stable feed assets. The client reads `https://github.com/Yafeiml/nodikarta/releases/latest/download/nodicarta-edge-stable-win-x64.json` and its sibling signature. The signature is Base64 of the 64-byte IEEE P1363 ECDSA P-256 signature over the exact UTF-8 manifest bytes, using SHA-256. The manifest binds the product, channel, OS/architecture, version, byte count, installer URL and SHA-256. A change of publisher key requires an explicit trust transition. Windows Authenticode signing of an installer, when used, must precede manifest signing.

The installed MCP command keeps the same path across releases. A 1.0.x launcher loads the release-local file named `hostfxr.dll`. In Native AOT releases this is a small Nodicarta compatibility library that loads `NodicartaEdgeCli.dll` in the same process. Existing STDIO sessions finish on their original version and new sessions use the selected release.

## Qualification

For an isolated two-version trial, use a loopback HTTP feed and an independent ephemeral P-256 key pair outside the source tree. Pass `-TestFeed`, `-TestPublicKeyFile`, `-TestPrivateKeyFile` and a temporary `-OutputDirectory` to the publish command for each version. The test installer uses its own shell folders and installation record under `NODICARTA_EDGE_SETUP_TEST_HOME`. Test manifests point to the versioned installer under `v<version>/` on the loopback server. Keep those assets on the test channel.

Before publishing, verify the actual installed version and startup preference, the fixed MCP command across an update, corrupted signatures and installers, rollback after a failed desktop startup, and retention of linked directories and unsent work. After publishing, download the feed, signature and installer from the public URLs and verify the same signed bytes.
