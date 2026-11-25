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
$launcher = Join-Path $root 'ProjectHub.Edge.exe'
$desktop = Join-Path $root 'ProjectHub.Edge.Desktop.exe'
$shell = New-Object -ComObject WScript.Shell
$menu = Join-Path $shell.SpecialFolders.Item('Programs') 'Nodicarta Edge.lnk'
$startup = Join-Path $shell.SpecialFolders.Item('Startup') 'Nodicarta Edge.lnk'
. (Join-Path $PSScriptRoot 'Test-EdgeRelease.ps1')

if ($Action -in 'Install','Uninstall') {
    $bin = Join-Path $PSScriptRoot 'bin'
    Test-EdgeRelease $bin (Join-Path $PSScriptRoot 'SHA256SUMS')
    $worker = [Diagnostics.ProcessStartInfo]::new((Join-Path $bin 'ProjectHub.Edge.Desktop.exe'))
    $worker.UseShellExecute = $false
    $worker.CreateNoWindow = $true
    $operation = if ($Action -eq 'Install') { 'developer' } else { 'developer-uninstall' }
    foreach ($arg in '--install-release',$root,$operation,'standard') { $worker.ArgumentList.Add($arg) }
    if ($StateDirectory -and $Action -eq 'Install') { $worker.ArgumentList.Add([IO.Path]::GetFullPath($StateDirectory)) }
    $process = [Diagnostics.Process]::Start($worker)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -eq 75) { throw '当前仍有 Edge 命令行或 MCP 会话。会话结束后可重试安装。' }
        if ($process.ExitCode) { throw 'Edge 安装操作失败，请查看 %TEMP%\Nodicarta-Edge-Setup.log。' }
    } finally { $process.Dispose() }
    if ($Action -eq 'Uninstall') { return }
    [pscustomobject]@{ Executable=$desktop; AgentCommand=$launcher; StartMenuLink=$menu; StartupEnabled=[IO.File]::Exists($startup) }
    return
}

if ([IO.File]::Exists($startup)) {
    $existing = $shell.CreateShortcut($startup)
    if ($existing.TargetPath -ne $launcher -or $existing.Description -ne 'Nodicarta Edge') {
        throw '现有登录启动项属于其他安装。'
    }
}
if ($Action -eq 'DisableStartup') {
    [IO.File]::Delete($startup)
    return
}

Test-EdgeRelease $root (Join-Path $root 'SHA256SUMS') -Installed
$entry = $shell.CreateShortcut($menu)
if ($entry.TargetPath -ne $launcher -or $entry.Description -ne 'Nodicarta Edge') { throw '请先安装桌面版。' }
$arguments = $entry.Arguments
if ($StateDirectory) {
    $state = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($StateDirectory)) -replace '(\\+)$','$1$1'
    $arguments = '--state "' + $state + '"'
}
$temp = Join-Path ([IO.Path]::GetDirectoryName($startup)) ([guid]::NewGuid().ToString('N') + '.lnk')
try {
    $link = $shell.CreateShortcut($temp)
    $link.TargetPath = $launcher
    $link.IconLocation = "$launcher,0"
    $link.WorkingDirectory = $root
    $link.Arguments = ("--background " + $arguments).TrimEnd()
    $link.Description = 'Nodicarta Edge'
    $link.Save()
    [IO.File]::Move($temp, $startup, $true)
} finally { [IO.File]::Delete($temp) }
