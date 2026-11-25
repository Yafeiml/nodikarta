#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts'),
    [switch]$Desktop,
    [switch]$NativeAot,
    [Version]$Version,
    [uri]$UpdateFeed,
    [string]$PublisherPublicKey,
    [switch]$TestChannel
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($Desktop) {
    $NativeAot = $true
    if (!$Version) {
        [xml]$desktopProject = Get-Content -Raw -LiteralPath (Join-Path $repo 'src/ProjectHub.Edge.Desktop/ProjectHub.Edge.Desktop.csproj')
        $Version = [Version]@($desktopProject.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    }
}
if ($NativeAot) {
    $gcc = $null
    $compilers = @(Get-Command gcc.exe -CommandType Application -All -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source)
    # GitHub's Windows image also ships a 32-bit toolchain.
    foreach ($path in ($compilers + "${env:SystemDrive}\mingw64\bin\gcc.exe" | Select-Object -Unique)) {
        if (![IO.File]::Exists($path)) { continue }
        $resources = Join-Path (Split-Path $path) 'windres.exe'
        if ((& $path -dumpmachine) -eq 'x86_64-w64-mingw32' -and [IO.File]::Exists($resources)) {
            $gcc = $path
            $windres = $resources
            break
        }
    }
    if (!$gcc) { throw 'Install x64 MinGW-w64 GCC and binutils, then put their bin directory on PATH.' }
}
$out = [IO.Directory]::CreateDirectory($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)).FullName
$package = Join-Path ([IO.Path]::GetTempPath()) ('projecthub-package-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $package
$bin = Join-Path $package 'bin'
$project = if ($Desktop) { 'ProjectHub.Edge.Desktop' } else { 'ProjectHub.Edge' }
$properties = @()
if ($Version) { $properties += "-p:Version=$Version" }
if ($NativeAot) {
    if (!$Version -or $Version.ToString() -notmatch '^\d+\.\d+\.\d+$') { throw 'Specify a three-part Native AOT release version.' }
    # The installer verifies the exact three-part product version in both Native AOT images.
    $properties += '-p:IncludeSourceRevisionInInformationalVersion=false'
    $properties += '-p:PublishAot=true', '-p:NativeDebugSymbols=false', '-p:DebugType=None', '-p:DebugSymbols=false'
}
if ($UpdateFeed -or $PublisherPublicKey -or $TestChannel) {
    if (!$Desktop -or !$Version -or !$UpdateFeed -or !$PublisherPublicKey) { throw 'A desktop release needs a version, feed and verification key together.' }
    $properties += "-p:NodicartaUpdateFeed=$($UpdateFeed.AbsoluteUri)", "-p:NodicartaUpdatePublicKey=$PublisherPublicKey",
        "-p:NodicartaUpdateTestChannel=$($TestChannel.IsPresent.ToString().ToLowerInvariant())", '-p:DebugType=None', '-p:DebugSymbols=false'
}
if ($NativeAot) {
    $work = [IO.Directory]::CreateTempSubdirectory('nodicarta-aot-').FullName
    try {
        [void][IO.Directory]::CreateDirectory($bin)
        if ($Desktop) {
            $guiOptions = @()
            if ($UpdateFeed) {
                $test = $TestChannel.IsPresent.ToString().ToLowerInvariant()
                $guiOptions += "-p:Description=Nodicarta/public-v1|$($UpdateFeed.AbsoluteUri)|$PublisherPublicKey|$test"
            }
            dotnet publish (Join-Path $repo "src/$project/$project.csproj") -c Release -r win-x64 --self-contained true -o $bin --artifacts-path (Join-Path $work 'gui-obj') @properties @guiOptions
            if ($LASTEXITCODE) { throw 'Native AOT desktop publish failed.' }
        }
        $native = Join-Path $work 'cli'
        dotnet publish (Join-Path $repo 'src/ProjectHub.Edge/ProjectHub.Edge.csproj') -c Release -r win-x64 --self-contained true -o $native --artifacts-path (Join-Path $work 'obj') @properties '-p:OutputType=Library' '-p:NativeLib=Shared'
        if ($LASTEXITCODE) { throw 'Native AOT CLI publish failed.' }
        foreach ($file in Get-ChildItem -LiteralPath $native -File) {
            $name = if ($file.Name -ceq 'ProjectHub.Edge.dll') { 'NodicartaEdgeCli.dll' } else { $file.Name }
            $to = Join-Path $bin $name
            if (!(Test-Path -LiteralPath $to)) { Copy-Item -LiteralPath $file.FullName -Destination $to }
            elseif ((Get-FileHash -LiteralPath $to).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Desktop and CLI published different native dependencies: $name"
            }
        }
        # The framework reference brings in the IIS hosting module and the legacy
        # framework-host config. Neither is used by the standalone Native AOT process.
        foreach ($unused in 'aspnetcorev2_inprocess.dll','ProjectHub.Edge.runtimeconfig.json') {
            [IO.File]::Delete((Join-Path $bin $unused))
        }
        $hostSources = Join-Path $repo 'src/ProjectHub.Edge.Launcher'
        $iconDir = Join-Path $repo 'src/ProjectHub.Edge.Desktop/Assets'
        $resource = Join-Path $work 'launcher-resources.o'
        # windres re-parses -I paths when running the preprocessor and splits paths containing spaces.
        Push-Location -LiteralPath $iconDir
        try {
            & $windres (Join-Path $hostSources 'windows-cli.rc') $resource
            if ($LASTEXITCODE) { throw 'Edge launcher resource compilation failed.' }
        } finally { Pop-Location }
        & $gcc -std=c11 -Os -municode -mwindows -s '-Wl,--no-insert-timestamp' (Join-Path $hostSources 'windows.c') $resource -o (Join-Path $bin 'ProjectHub.Edge.exe') -lshell32
        if ($LASTEXITCODE) { throw 'Native Edge launcher compilation failed.' }
    } finally {
        $tempRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
        $stagedWork = Get-Item -LiteralPath $work -Force
        if ($stagedWork.Parent.FullName -ne $tempRoot -or !$stagedWork.Name.StartsWith('nodicarta-aot-') -or
            $stagedWork.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected Native AOT temporary directory.' }
        Remove-Item -LiteralPath $stagedWork.FullName -Recurse -Force
    }
} else {
    dotnet publish (Join-Path $repo "src/$project/$project.csproj") -c Release -r win-x64 --self-contained true -o $bin @properties
    if ($LASTEXITCODE) { throw 'Edge 发布失败。' }
}
if ($Desktop) {
    if ($UpdateFeed) {
        $licenseDir = Join-Path $repo 'release/licenses/edge'
        $licenseFiles = @(Get-ChildItem -LiteralPath $licenseDir -Recurse -File | Sort-Object FullName)
        if (!$licenseFiles.Count) { throw 'Edge dependency licenses are missing.' }
        $notices = @((Join-Path $repo 'LICENSE'),(Join-Path $repo 'THIRD-PARTY-NOTICES.md'),
            (Join-Path $repo 'src/ProjectHub.Edge.Desktop/Assets/Harnesses/NOTICE.txt')) + $licenseFiles.FullName
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $sections = foreach ($notice in $notices) {
            '===== ' + [IO.Path]::GetRelativePath($repo, $notice).Replace('\','/') + ' ====='
            try { [IO.File]::ReadAllText($notice, $utf8) }
            catch [Text.DecoderFallbackException] {
                if ([IO.Path]::GetFileName($notice) -cne 'NSIS-ModernUI-LICENSE.txt') { throw }
                [Text.Encoding]::RegisterProvider([Text.CodePagesEncodingProvider]::Instance)
                [IO.File]::ReadAllText($notice, [Text.Encoding]::GetEncoding(1252))
            }
            ''
        }
        [IO.File]::WriteAllLines((Join-Path $bin 'LICENSES.txt'), [string[]]$sections, $utf8)
        if (@(Get-ChildItem -LiteralPath $bin -Filter '*.pdb' -File -Recurse).Count) { throw 'Public package contains debug symbols.' }
        $keyId = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($PublisherPublicKey)))
        $metadata = [ordered]@{releaseLine='public-v1';publisherKeyId=$keyId;channel='stable';rid='win-x64';version=$Version.ToString()}
        [IO.File]::WriteAllText((Join-Path $bin 'nodicarta-release.json'), ($metadata | ConvertTo-Json -Compress))
    }
    foreach ($file in 'Install-EdgeDesktop.ps1','Test-EdgeRelease.ps1','User-Dispatch.ps1','Edge-StatePaths.ps1') {
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
