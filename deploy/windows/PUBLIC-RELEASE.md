# Windows release

For a public release, follow the [Edge release workflow](../../docs/operations/EDGE_RELEASE.md). GitHub builds the candidates. The release workstation tests the downloaded installer and signs its update manifest. The commands below also support a local build for qualification.

Nodicarta Edge ships a per-user Windows x64 installer containing the Native AOT desktop and MCP/CLI. Building it uses PowerShell 7, the .NET 10 SDK, Visual Studio with the Desktop development with C++ workload and Windows SDK, x64 MinGW-w64 GCC and windres, and NSIS 3 with Unicode support. End users run the setup executable.

## Build and sign

Build the candidate with the SDK used by the Linux build (10.0.404 for Edge 1.1.0). Use the release version from the checked-out desktop and CLI projects. The default configuration embeds the repository's public update key and the stable feed URL.

```powershell
.\deploy\windows\Build-EdgeCandidate.ps1 -Version 1.1.0 -OutputDirectory .\candidate
```

The result is `candidate/Nodicarta-Edge-1.1.0-win-x64-setup.exe`. Qualify these exact bytes on the release workstation, then pass that installer to `Sign-EdgeRelease.ps1`. The hosted build needs only the public key. On `windows-2025`, the runner image supplies Visual C++, the Windows SDK and x64 MinGW-w64. Use NSIS 3.11 in the release job. The scripts locate `gcc.exe` and its sibling `windres.exe` from the same x64 toolchain, and locate `makensis.exe` on PATH or under the standard NSIS installation directory.

Existing local releases can still build and sign on the release workstation with the command below. It writes the installer and signed manifest under `artifacts/edge-public/stable/<version>`.

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

For a GitHub draft rehearsal, test the original installer downloaded from the draft first. After it passes, run `Sign-EdgeRelease.ps1` locally with that installer, its version, and `-RehearsalTag edge-rehearsal-v1.1.0-123`. The numeric suffix identifies the rehearsal and the version must match the installer. The signed URL points to the exact rehearsal tag under `Yafeiml/nodikarta`. The release coordinator uploads the returned `Manifest` and `Signature` files (`nodicarta-edge-rehearsal-win-x64.json` and its `.sig`) only to that draft.

Restore a clean, offline x64 Windows VM with PowerShell 7 and Node. Copy in the original draft installer, a **lower-version** public installer, and `tests/windows-draft-installer.ps1` with its sibling `windows-draft-session.cjs` and `windows-mcp.cjs`. Verify the locally computed candidate hash against the draft and the baseline hash against the published release. Run from the checkout root inside that disposable VM:

```powershell
./tests/windows-draft-installer.ps1 -Candidate <draft-exe> -CandidateSha256 <verified-draft-sha256> -Baseline <older-public-exe> -BaselineSha256 <published-sha256> -GuestUuid <vm-uuid> -Evidence <receipt.json>
```

The receipt covers installation in public mode, an actual `/UPDATE`, recovery from a failed update, MCP sessions, pairing and directory bindings. Exercise automatic downloads and signature verification separately with the test channel below.

For an isolated two-version trial, use a loopback HTTP feed and an independent ephemeral P-256 key pair outside the source tree. Pass `-TestFeed`, `-TestPublicKeyFile`, `-TestPrivateKeyFile` and a temporary `-OutputDirectory` to the publish command for each version. The test installer uses its own shell folders and installation record under `NODICARTA_EDGE_SETUP_TEST_HOME`. Test manifests point to the versioned installer under `v<version>/` on the loopback server. Keep those assets on the test channel.

Before publishing, verify the actual installed version and startup preference, the fixed MCP command across an update, corrupted signatures and installers, rollback after a failed desktop startup, and retention of linked directories and unsent work. After publishing, download the feed, signature and installer from the public URLs and verify the same signed bytes.
