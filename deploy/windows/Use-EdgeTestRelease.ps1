#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Archive,
    [string]$DesktopShortcut
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Edge-StatePaths.ps1')
. (Join-Path $PSScriptRoot 'Edge-ActiveRelease.ps1')
$shell = New-Object -ComObject WScript.Shell
if (!$DesktopShortcut) {
    $DesktopShortcut = Join-Path $shell.SpecialFolders.Item('Desktop') 'ProjectHub.Edge.Desktop.exe.lnk'
    if (!(Test-Path -LiteralPath $DesktopShortcut -PathType Leaf)) {
        $DesktopShortcut = Join-Path $shell.SpecialFolders.Item('Programs') 'Nodicarta Edge.lnk'
    }
}
$DesktopShortcut = (Get-Item -LiteralPath $DesktopShortcut).FullName
$link = $shell.CreateShortcut($DesktopShortcut)
$imageName = 'ProjectHub.Edge.Desktop.exe'
if ([IO.Path]::GetFileName($link.TargetPath) -ne $imageName) { throw "桌面测试快捷方式未指向 Edge：$DesktopShortcut" }
$oldTarget = $link.TargetPath
$oldDirectory = [IO.Path]::GetDirectoryName($oldTarget)
$originalArgs = $link.Arguments
$originalWorkingDirectory = $link.WorkingDirectory
$sessionId = (Get-Process -Id $PID).SessionId
$installRoot = if ($env:DEVTOOLS_HOME) { Join-Path $env:DEVTOOLS_HOME 'projecthub-edge/desktop' }
    else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs/Nodicarta/Edge' }
$oldRelease = [IO.DirectoryInfo]::new($oldDirectory).Parent
if ([IO.Path]::GetFileName($oldDirectory) -eq 'bin' -and $oldRelease.Name -cmatch '\A[0-9a-f]{12}\z') {
    $oldSum = Join-Path $oldRelease.FullName 'SHA256SUMS'
    $menuUsesRoot = @(foreach ($name in 'Nodicarta Edge.lnk','ProjectHub Edge.lnk') {
        $registered = Join-Path $shell.SpecialFolders.Item('Programs') $name
        if ([IO.File]::Exists($registered) -and $shell.CreateShortcut($registered).TargetPath -eq $oldTarget) { $registered }
    })
    if ($menuUsesRoot.Count -and [IO.File]::Exists($oldSum) -and (Get-FileHash -LiteralPath $oldSum).Hash.StartsWith($oldRelease.Name, [StringComparison]::OrdinalIgnoreCase)) {
        $installRoot = $oldRelease.Parent.FullName
    }
}
$expectedTargets = @($oldTarget)
foreach ($name in 'Nodicarta Edge.lnk','ProjectHub Edge.lnk') {
    $menu = Join-Path $shell.SpecialFolders.Item('Programs') $name
    if (Test-Path -LiteralPath $menu) { $expectedTargets += $shell.CreateShortcut($menu).TargetPath }
}

function Get-RecordedDesktopState {
    $local = $env:LOCALAPPDATA
    if (!$local -or ![IO.Path]::IsPathFullyQualified($local)) { $local = Get-EdgeKnownFolder LocalApplicationData }
    $record = Get-Item -LiteralPath (Join-Path $local 'Nodicarta/desktop-location.json') -ErrorAction SilentlyContinue
    if (!$record -or $record.Length -gt 524288 -or ($record.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
    try {
        $data = [IO.File]::ReadAllText($record.FullName) | ConvertFrom-Json -ErrorAction Stop
        if ([IO.Path]::IsPathFullyQualified($data.Executable) -and [IO.Path]::IsPathFullyQualified($data.StateDirectory)) { return $data.StateDirectory }
    } catch { return }
}

function Get-TestState([string]$Arguments) {
    $parts = [regex]::Match($Arguments.Trim(), '^(?:(?:--background\s+)?--state\s+(?:"(?<path>[^"]+)"|(?<path>[^"\s]+))(?:\s+--background)?|--background)?$')
    if (!$parts.Success) { throw '无法确认 Edge 的启动参数和数据目录。' }
    $state = $parts.Groups['path'].Value
    if (!$state) {
        foreach ($variable in 'NODICARTA_EDGE_STATE','PROJECTHUB_EDGE_STATE') {
            $current = [Environment]::GetEnvironmentVariable($variable)
            $persisted = [Environment]::GetEnvironmentVariable($variable,'User') ?? [Environment]::GetEnvironmentVariable($variable,'Machine')
            if ($current -and (!$persisted -or (Get-EdgeStatePlan $current).Path -ne (Get-EdgeStatePlan $persisted).Path)) {
                return Get-EdgeStatePlan $current
            }
        }
        $state = (Get-RecordedDesktopState) ?? $env:NODICARTA_EDGE_STATE ?? $env:PROJECTHUB_EDGE_STATE ??
            [Environment]::GetEnvironmentVariable('NODICARTA_EDGE_STATE','User') ?? [Environment]::GetEnvironmentVariable('PROJECTHUB_EDGE_STATE','User') ??
            (Join-Path (Get-EdgeKnownFolder LocalApplicationData) 'Nodicarta/Edge')
    }
    Get-EdgeStatePlan $state
}
$statePlan = Get-TestState $originalArgs
$testState = $statePlan.Path
function Find-TestDesktop {
    foreach ($item in Get-CimInstance Win32_Process -Filter "Name='$imageName' AND SessionId=$sessionId") {
        $argsMatch = [regex]::Match($item.CommandLine, '^\s*(?:"[^"]*"|\S+)\s*(?<args>.*)$')
        if (!$argsMatch.Success) { continue }
        $commandArgs = $argsMatch.Groups['args'].Value
        if ($commandArgs -match '^--(connect-codex|disconnect-codex|refresh-(codex|harness)-registration)(?:\s|$)') { continue }
        if ((Get-TestState $commandArgs).Path -ne $testState) { continue }
        $process = Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue
        if ($process -and $process.Path -eq $item.ExecutablePath) { $process }
    }
}

$zip = (Get-Item -LiteralPath $Archive).FullName
$digest = (Get-Content -LiteralPath "$zip.sha256" -Raw).Trim()
if ($digest -notmatch '^([a-fA-F0-9]{64})  (.+)$' -or $Matches[2] -cne [IO.Path]::GetFileName($zip) -or
    (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $Matches[1]) { throw '桌面发行包 SHA-256 校验失败。' }
$stage = [IO.Directory]::CreateTempSubdirectory('nodicarta-test-release-').FullName
$newLink = Join-Path ([IO.Path]::GetDirectoryName($DesktopShortcut)) (([guid]::NewGuid().ToString('N')) + '.lnk')
$stoppedPrevious = $false
$migrationCompleted = $false
$rollbackLink = Join-Path $stage 'previous.lnk'
$userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$deliveryLock = [Threading.Mutex]::new($false, "Local\Nodicarta.Edge.TestRelease.$userSid")
$locked = $false
$installLock = $null
$previousLinks = $null
$installed = $null
$installerStarted = $false
try {
    try { $locked = $deliveryLock.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw '另一项 Edge 测试版交付正在运行。' }
    $installLock = Enter-EdgeDesktopInstall
    $previousBuild = Get-EdgeActiveRelease $installRoot
    if (!$previousBuild -and $oldRelease.Parent.FullName -eq $installRoot -and $oldRelease.Name -cmatch '\A[0-9a-f]{12}\z') { $previousBuild = $oldRelease.Name }
    $previousLinks = Get-EdgeLinkSnapshot @(
        foreach ($folder in 'Startup','Programs') {
            foreach ($name in 'Nodicarta Edge.lnk','ProjectHub Edge.lnk') { Join-Path $shell.SpecialFolders.Item($folder) $name }
        }
        $DesktopShortcut
    )
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $stage)
    . (Join-Path $PSScriptRoot 'Test-EdgeRelease.ps1')
    $manifest = Join-Path $stage 'SHA256SUMS'
    Test-EdgeRelease (Join-Path $stage 'bin') $manifest
    $build = (Get-FileHash -LiteralPath $manifest).Hash.Substring(0,12).ToLowerInvariant()
    $exe = [IO.Path]::GetFullPath((Join-Path $installRoot "$build/bin/$imageName"))
    $current = @(Find-TestDesktop)
    if ($current.Count -gt 1 -or ($current.Count -and $current[0].Path -notin ($expectedTargets + $exe))) {
        throw '同一数据目录正在使用另一个桌面副本，请核对当前测试入口。'
    }
    [IO.File]::Copy($DesktopShortcut, $rollbackLink)
    if ($current.Count -and $current[0].Path -ne $exe) {
        . (Join-Path $PSScriptRoot 'Stop-EdgeTestDesktop.ps1')
        Stop-EdgeTestDesktop $current[0]
        $stoppedPrevious = $true
    }
    $installer = Join-Path $stage 'Install-EdgeDesktop.ps1'
    $parameters = @{ InstallDirectory=$installRoot }
    if ((Get-Command $installer).Parameters.ContainsKey('StateDirectory')) { $parameters.StateDirectory = $testState }
    $installerStarted = $true
    $installed = & $installer @parameters
    if ($installed.Executable -ne $exe) { throw '安装器返回的程序路径与已校验的版本不一致。' }
    # Older packages predate the stable command. Their payload is still a valid target.
    if (!$installed.AgentCommand -and $previousBuild -and [IO.File]::Exists((Join-Path $installRoot 'ProjectHub.Edge.exe'))) {
        Set-EdgeActiveRelease $installRoot $build
    }
    Invoke-EdgeStateMigration (Join-Path (Split-Path -LiteralPath $exe) 'ProjectHub.Edge.exe') $statePlan
    $migrationCompleted = $true
    [IO.File]::Copy($rollbackLink, $newLink)
    $updated = $shell.CreateShortcut($newLink)
    $updated.TargetPath = $exe
    $launchArgs = $originalArgs
    if ($statePlan.Legacy -and $originalArgs -match '(?:^|\s)--state\s') {
        $launchArgs = [regex]::Replace($originalArgs, '--state\s+(?:"[^"]+"|[^"\s]+)', { '--state "'+$testState+'"' })
        $updated.Arguments = $launchArgs
    }
    if (!$originalWorkingDirectory -or $originalWorkingDirectory -eq $oldDirectory) { $updated.WorkingDirectory = [IO.Path]::GetDirectoryName($exe) }
    if ($updated.IconLocation -in '',',0' -or $updated.IconLocation.StartsWith("$oldTarget,", [StringComparison]::OrdinalIgnoreCase)) {
        $updated.IconLocation = "$exe,0"
    }
    $updated.Save()
    if (!$current.Count -or $current[0].HasExited) {
        if ($launchArgs -notmatch '(?:^|\s)--state(?:\s|$)') {
            $launchArgs = ("--state `"$testState`" " + $launchArgs).TrimEnd()
        }
        # Explorer starts the tray app outside the build terminal's process tree.
        $launchPath = Join-Path $stage 'launch.lnk'
        [IO.File]::Copy($newLink, $launchPath)
        $launch = $shell.CreateShortcut($launchPath)
        $launch.Arguments = $launchArgs
        $launch.Save()
        Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $launchPath + '"') -WindowStyle Hidden
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 350
        $active = @(Find-TestDesktop | Where-Object Path -eq $exe)
        if ($active.Count -eq 1 -and $active[0].WaitForInputIdle(500)) { break }
    } until ([DateTime]::UtcNow -ge $deadline)
    if ($active.Count -ne 1 -or $active[0].HasExited -or !$active[0].WaitForInputIdle(500)) { throw "新版桌面程序未成功启动：$exe" }
    if ($shell.CreateShortcut($DesktopShortcut).TargetPath -notin @($oldTarget,$exe)) { throw '交付过程中桌面快捷方式已被修改。' }
    [IO.File]::Move($newLink, $DesktopShortcut, $true)
    if ($shell.CreateShortcut($DesktopShortcut).TargetPath -ne $active[0].Path) { throw '桌面快捷方式未指向已运行的版本。' }
    [pscustomobject]@{ Version=$active[0].MainModule.FileVersionInfo.ProductVersion; Build=$build; Executable=$exe;
        ProcessId=$active[0].Id; StateDirectory=$testState; DesktopShortcut=$DesktopShortcut; Archive=$zip }
} catch {
    if ($installerStarted -and $previousBuild -and [IO.File]::Exists((Join-Path $installRoot 'ProjectHub.Edge.exe')) -and
        (Get-EdgeActiveRelease $installRoot) -eq $build) {
        Set-EdgeActiveRelease $installRoot $previousBuild
    }
    if ($installed) {
        Restore-EdgeLinks $previousLinks
        if ($stoppedPrevious -and $exe -ne $oldTarget) {
            foreach ($failed in @(Find-TestDesktop | Where-Object Path -eq $exe)) { Stop-EdgeTestDesktop $failed }
        }
    }
    if ($stoppedPrevious -and !(Find-TestDesktop) -and (Test-Path -LiteralPath $oldTarget -PathType Leaf)) {
        if ($migrationCompleted -and $statePlan.Legacy) {
            $rollback = $shell.CreateShortcut($rollbackLink)
            $rollback.Arguments = '--state "' + $testState + '"' + $(if ($originalArgs -match '(?:^|\s)--background(?:\s|$)') { ' --background' })
            $rollback.Save()
        }
        Start-Process -FilePath (Join-Path $env:WINDIR 'explorer.exe') -ArgumentList ('"' + $rollbackLink + '"') -WindowStyle Hidden
        $restoreDeadline = [datetime]::UtcNow.AddSeconds(15)
        while (!(Find-TestDesktop | Where-Object Path -eq $oldTarget) -and [datetime]::UtcNow -lt $restoreDeadline) {
            Start-Sleep -Milliseconds 250
        }
    }
    throw
} finally {
    if ($installLock) { $installLock.ReleaseMutex(); $installLock.Dispose() }
    if ($locked) { $deliveryLock.ReleaseMutex() }
    $deliveryLock.Dispose()
    if ([IO.File]::Exists($newLink)) { [IO.File]::Delete($newLink) }
    $stageInfo = Get-Item -LiteralPath $stage
    if ($stageInfo.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $stageInfo.Name -like 'nodicarta-test-release-*') {
        Remove-Item -LiteralPath $stageInfo.FullName -Recurse -Force
    }
}
