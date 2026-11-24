# Releasing Nodicarta Edge

Windows and Linux release packages are built by GitHub Actions from the same Git tag. The maintainer qualifies the packages on their target systems and signs the Windows update manifest on the release workstation. The Core image has its own release workflow.

## Qualify the source and push a tag

Use a clean checkout of the public repository. Set both Edge project versions to the release version and publish the source commit to `main`. Keep local evidence outside the checkout or in its ignored `artifacts/` directory. Exercise installation, data preservation on upgrade, rollback, MCP access and Linux desktop startup before pushing the tag.

On the release workstation, supply the acceptance command for this release. The tag helper runs it locally, checks the commit and working tree again, and pushes a new tag only on success.

```powershell
$commit = git rev-parse HEAD
$tag = "v$version"
python tools/release/tag_edge.py --tag $tag --commit $commit -- pwsh -File $qualificationScript
```

The tag starts **Build Edge release candidates**. Its Windows job produces the Native AOT installer. Its Ubuntu job builds the Debian and portable packages. After both finish, the workflow creates a GitHub Release draft with the three packages. A maintainer can edit the release notes in the draft.

## Test the downloaded packages, sign and publish

From the same tagged checkout, authenticate to GitHub with a maintainer account. The release helper uses `GH_TOKEN`, `GITHUB_TOKEN`, GitHub CLI or Git Credential Manager. Run it with Python 3.11 or newer. Signing also requires Node.js, PowerShell 7 and the Windows account that holds the existing release key in CNG.

```powershell
$dir = Join-Path $env:TEMP "nodicarta-$tag-qualification"
python tools/release/edge_release.py fetch --tag $tag --commit $commit --dir $dir
```

Use the downloaded installer for an isolated Windows install, upgrade and rollback. The [installer regression test](../../tests/windows-draft-installer.ps1) runs against the candidate and a previous installer in a disposable Windows VM. Run the CLI/MCP and start the desktop from the downloaded Linux packages. Check the package versions and any regressions that prompted the release. Keep the downloads and their receipt together. If qualification fails, leave the release in draft and fix the source under a new tag.

After accepting those exact files, take the receipt SHA-256 printed by `fetch` and run:

```powershell
python tools/release/edge_release.py promote --tag $tag --commit $commit --dir $dir --accept $receiptSha256
```

Promotion checks the three local files against their GitHub asset IDs, sizes and hashes, signs the installer manifest locally and verifies the signature. It publishes the five assets as Latest after rechecking the draft, tag and previous Latest. The installation files are never rebuilt during promotion. To verify the public channel, run `node tools/release/verify_public.mjs $version`.

## Rehearsals

Use `edge-rehearsal-v<version>-<digits>` for a rehearsal of the version in the checkout. The official repository goes through the same hosted builds, download and local acceptance. Promotion signs a manifest with the separate `rehearsal` channel and leaves the release in draft. Installed stable clients continue to follow the current production release. The designated private rehearsal repository can exercise the GitHub Latest switch after receiving the same packages; it uses its own rehearsal signing key.

## 中文速查

1. 在干净的公开源码检出目录中确认提交及版本，先验收 Windows 和 Linux。通过 `tag_edge.py` 执行本机验收命令，成功后才推送新标签。
2. GitHub 编译同一提交，生成 Windows 安装器、Linux DEB 和便携包，放入发行草稿。
3. 用 `edge_release.py fetch` 下载原件。在本机验证 Windows 安装、升级、回滚，核对 Linux 桌面和 MCP，再确认下载回执的 SHA-256。
4. 用 `edge_release.py promote` 签署更新清单并发布。正式版本设为 Latest；正式仓里的演练标签只生成签名草稿。
