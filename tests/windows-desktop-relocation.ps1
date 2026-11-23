#requires -Version 7.0
param([Parameter(Mandatory)][string]$LegacyDesktop, [Parameter(Mandatory)][string]$NewDesktop, [string]$EvidenceFile)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name 'ProjectHub.Edge.Desktop' -ErrorAction SilentlyContinue) {
    throw 'Quit the existing desktop first. This acceptance test uses the real machine singleton.'
}
. (Join-Path $PSScriptRoot '../deploy/windows/Stop-EdgeTestDesktop.ps1')
$qa = [IO.Directory]::CreateTempSubdirectory('nodicarta-real-relocation-')
$profileDir = $qa.CreateSubdirectory('test-user').FullName
$state = Join-Path $qa.FullName 'custom state'
$otherState = Join-Path $qa.FullName 'ignored state'
$config = Join-Path $profileDir '.codex/config.toml'
$record = Join-Path $profileDir 'local/Nodicarta/desktop-location.json'
$children = [Collections.Generic.List[Diagnostics.Process]]::new()
function Copy-Desktop([string]$from,[string]$where) {
    $dir = $qa.CreateSubdirectory($where)
    Copy-Item -LiteralPath (Get-Item -LiteralPath $from).Directory.FullName -Destination (Join-Path $dir.FullName 'bin') -Recurse
    [IO.Path]::GetFullPath((Join-Path $dir.FullName 'bin/ProjectHub.Edge.Desktop.exe'))
}
function Start-Fixture([string]$exe,[string[]]$arguments) {
    $si = [Diagnostics.ProcessStartInfo]::new($exe)
    $si.UseShellExecute = $false
    $si.Environment['USERPROFILE'] = $si.Environment['HOME'] = $profileDir
    $si.Environment['LOCALAPPDATA'] = Join-Path $profileDir 'local'
    $si.Environment['APPDATA'] = Join-Path $profileDir 'roaming'
    $si.Environment['CODEX_HOME'] = Join-Path $profileDir '.codex'
    $si.Environment['NODICARTA_EDGE_STATE'] = ''
    $si.Environment['PROJECTHUB_EDGE_STATE'] = ''
    foreach ($key in 'NODICARTA_EDGE_STATE','PROJECTHUB_EDGE_STATE','OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT','CLAUDE_CONFIG_DIR','XDG_CONFIG_HOME','PI_CODING_AGENT_DIR','DSH_HOME') {
        $null = $si.Environment.Remove($key)
    }
    foreach ($arg in $arguments) { $si.ArgumentList.Add($arg) }
    $p = [Diagnostics.Process]::Start($si)
    $children.Add($p)
    $p
}
function Wait-For([string]$description,[scriptblock]$predicate) {
    $until = [datetime]::UtcNow.AddSeconds(40)
    do {
        if (& $predicate) { return }
        Start-Sleep -Milliseconds 125
    } while ([datetime]::UtcNow -lt $until)
    throw "Timed out: $description"
}
function Confirm-Exit($p) {
    if (!$p.WaitForExit(10000) -or $p.ExitCode -ne 0) { throw "Expected a clean exit from fixture PID $($p.Id)" }
}
function Registered-Cli {
    if (!(Test-Path -LiteralPath $config)) { return '' }
    $toml = [IO.File]::ReadAllText($config)
    $match = [regex]::Match($toml,'(?m)^command\s*=\s*("[^\r\n]+")\s*$')
    if ($match.Success) { return ($match.Groups[1].Value | ConvertFrom-Json) }
    ''
}
try {
    $a = Copy-Desktop $LegacyDesktop 'A'
    $b = Copy-Desktop $NewDesktop 'B'
    $c = Copy-Desktop $NewDesktop 'C'
    $registration = Start-Fixture $a @('--connect-codex','--state',$state)
    Confirm-Exit $registration
    $old = Start-Fixture $a @('--background','--state',$state)
    if (!$old.WaitForInputIdle(8000)) { throw 'Legacy resident failed to start' }
    $null = Start-Sleep -Milliseconds 850
    $current = Start-Fixture $b @('--state',$state)
    Wait-For 'old tray exit and first relocation' {
        $old.HasExited -and $old.ExitCode -eq 0 -and !$current.HasExited -and
            (Registered-Cli) -eq (Join-Path (Split-Path $b) 'ProjectHub.Edge.exe')
    }
    $duplicate = Start-Fixture $b @('--state',$otherState)
    Confirm-Exit $duplicate
    if ($current.HasExited -or (Test-Path -LiteralPath $otherState)) { throw 'Second state created another GUI' }
    $moved = Start-Fixture $c @()
    Wait-For 'native handover and second relocation' {
        $current.HasExited -and $current.ExitCode -eq 0 -and !$moved.HasExited -and
            (Registered-Cli) -eq (Join-Path (Split-Path $c) 'ProjectHub.Edge.exe')
    }
    $saved = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    if ($saved.StateDirectory -ne $state -or $saved.Executable -ne $c) { throw 'Lost custom state or current executable' }
    $staleLogin = Start-Fixture $b @('--background')
    Confirm-Exit $staleLogin
    if ($moved.HasExited) { throw 'A stale auto-start replaced the last explicit owner' }
    Stop-EdgeTestDesktop $moved
    Confirm-Exit $moved
    $result = [ordered]@{ version=(Get-Item -LiteralPath $c).VersionInfo.ProductVersion; previousVersion=(Get-Item -LiteralPath $a).VersionInfo.ProductVersion;
        legacyTrayExit=$old.ExitCode; nativeHandoverExit=$current.ExitCode; customStatePreserved=$true; otherStateCreated=$false;
        sameExecutableRepeatExit=$duplicate.ExitCode; staleLoginExit=$staleLogin.ExitCode; agentAutomaticallyRetargetedTwice=$true }
    if ($EvidenceFile) {
        $null=[IO.Directory]::CreateDirectory((Split-Path ([IO.Path]::GetFullPath($EvidenceFile))))
        $result | ConvertTo-Json | Set-Content -LiteralPath $EvidenceFile -Encoding utf8
    }
    $result | ConvertTo-Json -Compress
}
finally {
    foreach ($p in $children) {
        if (!$p.HasExited -and $p.Path.StartsWith($qa.FullName + '\',[StringComparison]::OrdinalIgnoreCase)) {
            try { Stop-EdgeTestDesktop $p } catch { $p.Kill(); $p.WaitForExit() }
        }
        $p.Dispose()
    }
    if ($qa.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and
        $qa.Name -like 'nodicarta-real-relocation-*') { Remove-Item -LiteralPath $qa.FullName -Recurse -Force }
}
