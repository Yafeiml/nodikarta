#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts'),
    [switch]$Desktop,
    [Version]$Version,
    [uri]$UpdateFeed,
    [string]$PublisherPublicKey,
    [switch]$TestChannel
)
$ErrorActionPreference = 'Stop'
$out = [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)).FullName
$package = Join-Path ([IO.Path]::GetTempPath()) ('projecthub-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $package
$bin = Join-Path $package 'bin'
$project = if ($Desktop) { 'ProjectHub.Edge.Desktop' } else { 'ProjectHub.Edge' }
$properties = @()
if ($Version) { $properties += "-p:Version=$Version" }
if ($UpdateFeed -or $PublisherPublicKey -or $TestChannel) {
    if (!$Desktop -or !$Version -or !$UpdateFeed -or !$PublisherPublicKey) { throw 'A desktop release needs a version, feed and verification key together.' }
    $properties += "-p:NodicartaUpdateFeed=$($UpdateFeed.AbsoluteUri)", "-p:NodicartaUpdatePublicKey=$PublisherPublicKey",
        "-p:NodicartaUpdateTestChannel=$($TestChannel.IsPresent.ToString().ToLowerInvariant())", '-p:DebugType=None', '-p:DebugSymbols=false'
}
dotnet publish (Join-Path $PSScriptRoot "../../src/$project/$project.csproj") -c Release -r win-x64 --self-contained true -o $bin @properties
if ($LASTEXITCODE) { throw 'Edge 发布失败。' }
if ($Desktop) {
    if ($UpdateFeed) {
        $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        foreach ($notice in 'LICENSE','THIRD-PARTY-NOTICES.md') { Copy-Item -LiteralPath (Join-Path $repo $notice) -Destination $bin }
        $licenseDir = Join-Path $repo 'release/licenses'
        if (!@(Get-ChildItem -LiteralPath (Join-Path $licenseDir 'edge') -Recurse -File).Count) { throw 'Edge dependency licenses are missing.' }
        $releaseDocs = [IO.Directory]::CreateDirectory((Join-Path $bin 'release')).FullName
        Copy-Item -LiteralPath $licenseDir -Destination $releaseDocs -Recurse
        foreach ($guide in 'WINDOWS_EDGE_DESKTOP.md','WINDOWS_EDGE_DESKTOP.en.md') {
            Copy-Item -LiteralPath (Join-Path $repo "docs/operations/$guide") -Destination $bin
        }
        if (@(Get-ChildItem -LiteralPath $bin -Filter '*.pdb' -File -Recurse).Count) { throw 'Public package contains debug symbols.' }
        $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($PublisherPublicKey)))
        $metadata = [ordered]@{releaseLine='public-v1';publisherKeyId=$keyId;channel='stable';rid='win-x64';version=$Version.ToString()}
        [IO.File]::WriteAllText((Join-Path $bin 'nodicarta-release.json'), ($metadata | ConvertTo-Json -Compress))
    }
    if ((gcc -dumpmachine) -ne 'x86_64-w64-mingw32') { throw '桌面启动器需要 Windows x64 MinGW-w64 GCC。' }
    gcc -std=c11 -Os -municode -s '-Wl,--no-insert-timestamp' (Join-Path $PSScriptRoot '../../src/ProjectHub.Edge.Launcher/windows.c') -o (Join-Path $bin 'Nodicarta.Edge.Launcher.exe')
    if ($LASTEXITCODE) { throw 'Edge 启动器构建失败。' }
    foreach ($file in 'Install-EdgeDesktop.ps1','Test-EdgeRelease.ps1','Edge-ActiveRelease.ps1','User-Dispatch.ps1','Edge-StatePaths.ps1') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $package
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../docs/operations/WINDOWS_EDGE_DESKTOP.md') -Destination (Join-Path $package 'README.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../docs/operations/WINDOWS_EDGE_DESKTOP.en.md') -Destination (Join-Path $package 'README.en.md')
} else {
    foreach ($file in 'Install-Edge.ps1','Uninstall-Edge.ps1','User-Dispatch.ps1','Edge-StatePaths.ps1','Test-EdgeRelease.ps1','README.md') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $package
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../skills') -Destination (Join-Path $package 'skills') -Recurse
}
$lines = Get-ChildItem $bin -File -Recurse | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash, [IO.Path]::GetRelativePath($bin, $_.FullName).Replace('\','/')
}
$manifest = Join-Path $package 'SHA256SUMS'
[IO.File]::WriteAllLines($manifest, [string[]]$lines)
$stagingZip = Join-Path $out ([Guid]::NewGuid().ToString('N') + '.tmp')
[IO.Compression.ZipFile]::CreateFromDirectory($package, $stagingZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $stagingZip).Hash
$id = $hash.Substring(0,12).ToLowerInvariant()
$name = if ($Desktop) { 'Nodicarta-Edge-Desktop' } else { $project.Replace('.', '-') }
$zip = Join-Path $out "$name-win-x64-$id.zip"
if (Test-Path -LiteralPath $zip) {
    if ((Get-FileHash $zip).Hash -ne $hash) { throw '现有文件与新发行包标识冲突。' }
    Remove-Item -LiteralPath $stagingZip
} else { Move-Item -LiteralPath $stagingZip -Destination $zip }
if ($Desktop) {
    [IO.File]::WriteAllText("$zip.sha256", "$hash  $([IO.Path]::GetFileName($zip))" + [Environment]::NewLine)
}
[PSCustomObject]@{ Archive = $zip; SHA256 = $hash; Directory = $package }
