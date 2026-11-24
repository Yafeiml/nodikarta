#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Install','EnableStartup','DisableStartup','Uninstall')][string]$Action = 'Install',
    [string]$InstallDirectory = $(if ($env:DEVTOOLS_HOME) { Join-Path $env:DEVTOOLS_HOME 'projecthub-edge/desktop' }
        else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/Nodicarta/Edge' }),
    [string]$StateDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($InstallDirectory))
$shell = New-Object -ComObject WScript.Shell
$menu = Join-Path $shell.SpecialFolders.Item('Programs') 'Nodicarta Edge.lnk'
$startup = Join-Path $shell.SpecialFolders.Item('Startup') 'Nodicarta Edge.lnk'
$legacyMenu = Join-Path $shell.SpecialFolders.Item('Programs') 'ProjectHub Edge.lnk'
$legacyStartup = Join-Path $shell.SpecialFolders.Item('Startup') 'ProjectHub Edge.lnk'
$exeName = 'ProjectHub.Edge.Desktop.exe'
$managedTarget = '^' + [regex]::Escape($root + '\') + '[0-9a-f]{12}\\bin\\' + [regex]::Escape($exeName) + '$'
. (Join-Path $PSScriptRoot 'Test-EdgeRelease.ps1')
. (Join-Path $PSScriptRoot 'Edge-ActiveRelease.ps1')

function Read-Link([string]$Path, [switch]$IgnoreForeign) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    $link = $shell.CreateShortcut($Path)
    $target = $link.TargetPath
    if ($target -notmatch $managedTarget) {
        # A relocated installation inherits the existing menu and login choices.
        if ($Action -eq 'Install' -and [IO.Path]::IsPathFullyQualified($target) -and
            [IO.Path]::GetFileName($target) -eq $exeName -and $link.Description -in 'Nodicarta Edge','ProjectHub Edge' -and
            $link.Arguments -match '^(?:(?:--background\s+)?--state\s+(?:"[^"]+"|\S+)(?:\s+--background)?|--background)?$') { return $target }
        if ($IgnoreForeign) { return }
        throw "现有快捷方式属于其他安装，请核对目标：$Path"
    }
    return $target
}

function Write-Link([string]$Path, [string]$Target, [string]$Arguments) {
    $parent = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)).FullName
    $tmp = Join-Path $parent ([guid]::NewGuid().ToString('N') + '.lnk')
    try {
        $link = $shell.CreateShortcut($tmp)
        $link.TargetPath = $Target
        $link.IconLocation = "$Target,0"
        $link.WorkingDirectory = [IO.Path]::GetDirectoryName($Target)
        $link.Arguments = $Arguments
        $link.Description = 'Nodicarta Edge'
        $link.Save()
        [IO.File]::Move($tmp, $Path, $true)
    } finally { if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp } }
}

function Disable-LegacyTask {
    $legacy = Get-ScheduledTask -TaskPath '\' -TaskName "ProjectHub Edge ($env:USERNAME)" -ErrorAction SilentlyContinue
    if (!$legacy -or !$legacy.Settings.Enabled) { return }
    $actions = @($legacy.Actions)
    if ($actions.Count -ne 1 -or $actions[0].Arguments -notmatch '(?i)ProjectHub\.Edge\.exe[''"]?\s+run\s+--state\s') {
        throw '检测到同名的登录任务，但其启动命令不属于旧版 Edge。请核对 Windows 任务计划程序。'
    }
    $null = Disable-ScheduledTask -InputObject $legacy
}

$installLock = Enter-EdgeDesktopInstall
try {
$installed = (Read-Link $menu) ?? (Read-Link $legacyMenu -IgnoreForeign)
$autostart = (Read-Link $startup) ?? (Read-Link $legacyStartup -IgnoreForeign)
$menuArgs = ''
if ($installed) {
    $oldMenu = if ([IO.File]::Exists($menu)) { $menu } else { $legacyMenu }
    $menuArgs = $shell.CreateShortcut($oldMenu).Arguments
    if (!$StateDirectory -and $menuArgs -match '^--state\s+(?:"([^"]+)"|(\S+))$') {
        $StateDirectory = $Matches[1] ?? $Matches[2]
    }
}
if ($StateDirectory) {
    $StateDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($StateDirectory))
    $quotedState = $StateDirectory -replace '(\\+)$','$1$1'
    $menuArgs = '--state "' + $quotedState + '"'
}
$startupArgs = ("--background " + $menuArgs).TrimEnd()
function Remove-ManagedLink([string]$path) {
    if (Read-Link $path -IgnoreForeign) { Remove-Item -LiteralPath $path }
}
if ($Action -eq 'DisableStartup') {
    Remove-ManagedLink $startup
    Remove-ManagedLink $legacyStartup
    return
}
if ($Action -eq 'EnableStartup') {
    if (!$installed -or !(Test-Path -LiteralPath $installed -PathType Leaf)) { throw '请先安装桌面版。' }
    Disable-LegacyTask
    Write-Link $startup $installed $startupArgs
    Remove-ManagedLink $legacyStartup
    return
}
if ($Action -eq 'Uninstall') {
    $launcher = Join-Path $root 'ProjectHub.Edge.exe'
    $cliTarget = '^' + [regex]::Escape($root + '\') + '[0-9a-f]{12}\\bin\\ProjectHub\.Edge\.exe$'
    if (Get-Process -Name ProjectHub.Edge.Desktop,ProjectHub.Edge -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -match $managedTarget -or $_.Path -match $cliTarget -or $_.Path -eq $launcher }) {
        throw '请先退出 Edge 桌面程序，并关闭正在使用此安装的智能体连接，再卸载。'
    }
    $releases = @()
    if (Test-Path -LiteralPath $root) {
        $releases = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^[0-9a-f]{12}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName "bin/$exeName")) })
    }
    foreach ($dir in $releases) {
        $sum = Join-Path $dir.FullName 'SHA256SUMS'
        if ($dir.Parent.FullName -ne $root -or $dir.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
            @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -Force -Attributes ReparsePoint).Count -or
            @(Get-ChildItem -LiteralPath $dir.FullName -Force).Count -ne 2 -or
            !(Test-Path -LiteralPath $sum) -or !(Get-FileHash -LiteralPath $sum).Hash.StartsWith($dir.Name, [StringComparison]::OrdinalIgnoreCase)) {
            throw "版本目录含额外文件或无法确认归属，请核对：$($dir.FullName)"
        }
        Test-EdgeRelease (Join-Path $dir.FullName 'bin') $sum
    }
    $active = Get-EdgeActiveRelease $root
    if ($active -and $active -cnotin $releases.Name) { throw '活动版本的归属无法确认，已保留安装目录。' }
    if ([IO.File]::Exists($launcher)) {
        if ((Get-Item -LiteralPath $launcher -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
            !(Test-OwnedEdgeLauncher $root (Get-FileHash -LiteralPath $launcher).Hash)) { throw "无法确认启动命令的归属：$launcher" }
        [IO.File]::Delete($launcher)
    }
    if ($active) { [IO.File]::Delete((Join-Path $root 'active-release')) }
    foreach ($linkPath in $startup,$menu,$legacyStartup,$legacyMenu) { Remove-ManagedLink $linkPath }
    foreach ($dir in $releases) { Remove-Item -LiteralPath $dir.FullName -Recurse }
    return
}

$bin = Join-Path $PSScriptRoot 'bin'
$sum = Join-Path $PSScriptRoot 'SHA256SUMS'
if (!(Test-Path -LiteralPath (Join-Path $bin $exeName)) -or !(Test-Path -LiteralPath $sum)) {
    throw '请完整解压 Windows 桌面版 ZIP，并运行解压目录内的安装脚本。'
}
Test-EdgeRelease $bin $sum
$id = (Get-FileHash -LiteralPath $sum).Hash.Substring(0,12).ToLowerInvariant()
$release = Join-Path $root $id
$exe = Join-Path $release "bin/$exeName"
if (!(Test-Path -LiteralPath $release)) {
    $null = [IO.Directory]::CreateDirectory($root)
    $stage = [IO.Directory]::CreateDirectory((Join-Path $root ([guid]::NewGuid().ToString('N') + '.staging')))
    try {
        Copy-Item -LiteralPath $bin,$sum -Destination $stage.FullName -Recurse
        Test-EdgeRelease (Join-Path $stage.FullName 'bin') $sum
        try { [IO.Directory]::Move($stage.FullName, $release) }
        catch [IO.IOException] {
            if (!(Test-Path -LiteralPath $release -PathType Container)) { throw }
            Test-EdgeRelease (Join-Path $release 'bin') $sum
        }
    } finally {
        if ($stage.Parent.FullName -eq $root -and (Test-Path -LiteralPath $stage.FullName)) {
            Remove-Item -LiteralPath $stage.FullName -Recurse
        }
    }
} else { Test-EdgeRelease (Join-Path $release 'bin') $sum }

# An opted-in dispatch worker belongs to the interactive account. The controller
# verifies its full command and both profile locations before changing anything.
$dispatchEntry = Join-Path $PSScriptRoot 'User-Dispatch.ps1'
$startupFolder = $shell.SpecialFolders.Item('Startup')
if ([IO.File]::Exists($dispatchEntry)) {
    . (Join-Path $PSScriptRoot 'Edge-StatePaths.ps1')
    $states = @(if ($StateDirectory) { $StateDirectory } else {
        Join-Path (Get-EdgeKnownFolder LocalApplicationData) 'Nodicarta/Edge'
        Join-Path (Get-EdgeKnownFolder CommonApplicationData) 'Nodicarta/Edge'
    })
    $states += @(foreach ($candidate in Get-ChildItem -LiteralPath $startupFolder -File -Filter '*.lnk' -ErrorAction SilentlyContinue) {
        if ($candidate.Name -notmatch '^(?:ProjectHub|Nodicarta) Edge [0-9a-f]{16}\.lnk$') { continue }
        $link = $shell.CreateShortcut($candidate.FullName)
        if ([IO.Path]::GetFileName($link.TargetPath) -ne 'pwsh.exe' -or
            $link.Arguments -notmatch '^-NoProfile -NonInteractive -WindowStyle Hidden -File "([^"]+)" -Action Start -StateDirectory "([^"]+)" -EdgeExe "[^"]+"$') { continue }
        if ([IO.File]::Exists($Matches[1]) -and [IO.Path]::IsPathFullyQualified($Matches[2])) {
            $linkedState = Get-EdgeStatePlan $Matches[2]
            if (!$linkedState.Independent -or $linkedState.Requested -ne $linkedState.Legacy) { $linkedState.Requested }
        }
    })
    foreach ($dispatchState in $states | Select-Object -Unique) {
        $null = & $dispatchEntry -Action Migrate -StateDirectory $dispatchState -EdgeExe (Join-Path (Split-Path -LiteralPath $exe) 'ProjectHub.Edge.exe')
    }
}

$previousBuild = Get-EdgeActiveRelease $root
if (!$previousBuild -and $installed -match $managedTarget -and [IO.File]::Exists($installed)) {
    $previousBuild = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($installed)).Parent.Name
}
$previousLinks = Get-EdgeLinkSnapshot @($menu,$startup,$legacyMenu,$legacyStartup)
Install-EdgeLauncher $root $id
Set-EdgeActiveRelease $root $id
try {
    $refresh = [Diagnostics.ProcessStartInfo]::new($exe)
    $refresh.UseShellExecute = $false
    $refresh.CreateNoWindow = $true
    $refresh.ArgumentList.Add('--refresh-harness-registration')
    $refresh.ArgumentList.Add($root)
    if ($StateDirectory) {
        $refresh.ArgumentList.Add('--state')
        $refresh.ArgumentList.Add([IO.Path]::GetFullPath($StateDirectory))
    }
    $process = [Diagnostics.Process]::Start($refresh)
    try {
        $process.WaitForExit()
        if ($process.ExitCode) { throw '未能更新已有智能体的接入路径。' }
    } finally { $process.Dispose() }

    Disable-LegacyTask
    if (!$installed -or $autostart) { Write-Link $startup $exe $startupArgs }
    Write-Link $menu $exe $menuArgs
    Remove-ManagedLink $legacyMenu
    Remove-ManagedLink $legacyStartup
} catch {
    if ($previousBuild -and $previousBuild -ne $id) { Set-EdgeActiveRelease $root $previousBuild }
    Restore-EdgeLinks $previousLinks
    throw
}
[PSCustomObject]@{ Executable = $exe; AgentCommand = (Join-Path $root 'ProjectHub.Edge.exe'); StartMenuLink = $menu; StartupEnabled = [IO.File]::Exists($startup) }
} finally { $installLock.ReleaseMutex(); $installLock.Dispose() }
