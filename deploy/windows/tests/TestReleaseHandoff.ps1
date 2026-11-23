#requires -Version 7.0
param([Parameter(Mandatory)][string]$OldExecutable, [Parameter(Mandatory)][string]$Archive, [switch]$DefaultMigration, [string]$RollbackArchive)
$ErrorActionPreference = 'Stop'
$oldExe = (Get-Item -LiteralPath $OldExecutable).FullName
$zip = (Get-Item -LiteralPath $Archive).FullName
$entry = Join-Path $PSScriptRoot '../Use-EdgeTestRelease.ps1'
$work = [IO.Directory]::CreateTempSubdirectory('nodicarta-handoff-')
$state = Join-Path $work.FullName 'existing Edge state'
$testLocal = Join-Path $work.FullName 'local'
$testMachine = Join-Path $work.FullName 'machine'
if ($DefaultMigration) { $state = Join-Path $testLocal 'ProjectHub/Edge' }
$expectedState = if ($DefaultMigration) { Join-Path $testLocal 'Nodicarta/Edge' } else { $state }
$homeDir = Join-Path $work.FullName 'profile'
$devtools = Join-Path $work.FullName 'devtools'
$testArgs = '--background --state "' + $state + '"'
$expectedArgs = '--background --state "' + $expectedState + '"'
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$shell = Microsoft.PowerShell.Utility\New-Object -ComObject WScript.Shell
$folders = [pscustomobject]@{ Root = $work.FullName }
$folders | Add-Member ScriptMethod Item { param($folder) Join-Path $this.Root $folder }
$testShell = [pscustomobject]@{ SpecialFolders = $folders; Shell = $shell }
$testShell | Add-Member ScriptMethod CreateShortcut { param($path) $this.Shell.CreateShortcut($path) }
function New-Object([string]$ComObject) {
    if ($ComObject -ne 'WScript.Shell') { throw 'Unexpected COM object' }
    $testShell
}
function Get-ScheduledTask($TaskPath, $TaskName, $ErrorAction) {}
function Get-CimInstance($ClassName, $Filter) {
    # Keep every user's pre-existing Edge process outside the fixture's reach.
    CimCmdlets\Get-CimInstance -ClassName $ClassName -Filter $Filter | Where-Object ProcessId -in $owned.Id
}
function Start-TestApp([string]$file, [string]$arguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($file)
    $info.UseShellExecute = $false
    $info.Arguments = $arguments
    $info.WorkingDirectory = Split-Path -LiteralPath $file
    $process = [Diagnostics.Process]::Start($info)
    $owned.Add($process)
    $process
}
function Start-Process($FilePath, $ArgumentList, $WindowStyle) {
    if ([IO.Path]::GetFileName($FilePath) -ne 'explorer.exe') { throw 'Unexpected launch application' }
    $link = $testShell.CreateShortcut($ArgumentList.Trim('"'))
    if (!$link.TargetPath.StartsWith($devtools + '\', [StringComparison]::OrdinalIgnoreCase) -and $link.TargetPath -ne $oldExe) { throw 'Unexpected shortcut target' }
    if ($link.Arguments -ne $expectedArgs) { throw 'Shortcut lost the fixture state' }
    $null = Start-TestApp $link.TargetPath $link.Arguments
}
function Assert($condition, $message) { if (!$condition) { throw $message } }

$settings = @{
    DEVTOOLS_HOME=$devtools; USERPROFILE=$homeDir; HOME=$homeDir; CODEX_HOME="$homeDir\.codex"
    APPDATA="$homeDir\AppData\Roaming"; LOCALAPPDATA="$homeDir\AppData\Local"; XDG_CONFIG_HOME="$homeDir\.config"
    DSH_HOME="$homeDir\.dsh"; PI_CODING_AGENT_DIR="$homeDir\.pi\agent"; CLAUDE_CONFIG_DIR="$homeDir\.claude"
    OPENCODE_CONFIG_DIR="$homeDir\.config\opencode"; OPENCODE_CONFIG=$null; OPENCODE_CONFIG_CONTENT=$null
}
$previous = @{}
try {
    if ($DefaultMigration) {
        foreach ($name in 'Use-EdgeTestRelease.ps1','Stop-EdgeTestDesktop.ps1','Edge-StatePaths.ps1','Test-EdgeRelease.ps1','Edge-ActiveRelease.ps1') {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$name") -Destination $work.FullName
        }
        $entry = Join-Path $work.FullName 'Use-EdgeTestRelease.ps1'
        Add-Content -LiteralPath (Join-Path $work.FullName 'Edge-StatePaths.ps1') -Value (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Isolated-StatePaths.ps1'))
    }
    foreach ($key in $settings.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key)
        [Environment]::SetEnvironmentVariable($key, $settings[$key])
    }
    $desktop = [IO.Directory]::CreateDirectory((Join-Path $work.FullName 'Desktop'))
    $shortcut = Join-Path $desktop.FullName 'ProjectHub.Edge.Desktop.exe.lnk'
    $sourceLink = $testShell.CreateShortcut($shortcut)
    $sourceLink.TargetPath = $oldExe
    $sourceLink.Arguments = $testArgs
    $sourceLink.WorkingDirectory = Split-Path -LiteralPath $oldExe
    $sourceLink.Save()
    $first = Start-TestApp $oldExe $testArgs
    $db = Join-Path $state 'edge.sqlite3'
    $deadline = [datetime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath (Join-Path $state 'connection.json')) -and [datetime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    Assert ((Test-Path -LiteralPath $db) -and !$first.HasExited) 'The old Edge did not start'
    $nonce = [guid]::NewGuid().ToString('N')
    $seed = "const db=new (require('node:sqlite').DatabaseSync)(process.argv[1]); db.exec('CREATE TABLE handoff_fixture(value TEXT)'); db.prepare('INSERT INTO handoff_fixture VALUES (?)').run(process.argv[2]); db.close()"
    & node -e $seed $db $nonce
    Assert ($LASTEXITCODE -eq 0) 'Could not seed the fixture database'
    $result = & $entry -Archive $zip -DesktopShortcut $shortcut
    Assert ($first.WaitForExit(1000) -and $first.ExitCode -eq 0) 'The correct old instance must quit normally'
    Assert ($result.StateDirectory -eq $expectedState -and $result.ProcessId -ne $first.Id) 'First launch used the wrong state or process'
    $newLink = $testShell.CreateShortcut($shortcut)
    Assert ($newLink.TargetPath -eq $result.Executable -and $newLink.Arguments -eq $expectedArgs) 'Updated shortcut is incorrect'
    $other = Start-TestApp $result.Executable ('--background --state "' + (Join-Path $work.FullName 'other state') + '"')
    Assert ($other.WaitForExit(5000) -and $other.ExitCode -eq 0) 'A second resident started with another state directory'
    $repeat = & $entry -Archive $zip -DesktopShortcut $shortcut
    Assert ($repeat.ProcessId -eq $result.ProcessId -and $owned.Count -eq 3) 'Rerun replaced an active instance'
    if ($RollbackArchive) {
        $rollback = & $entry -Archive $RollbackArchive -DesktopShortcut $shortcut
        $install = [IO.Directory]::GetParent($rollback.Executable).Parent.Parent.FullName
        $activeFile = Join-Path $install 'active-release'
        Assert ($rollback.Build -ne $result.Build -and [IO.File]::ReadAllText($activeFile) -ceq $rollback.Build -and
            $testShell.CreateShortcut($shortcut).TargetPath -eq $rollback.Executable) 'Legacy rollback must activate the older CLI and desktop together'
        $again = & $entry -Archive $zip -DesktopShortcut $shortcut
        Assert ([IO.File]::ReadAllText($activeFile) -ceq $result.Build -and $again.Executable -eq $result.Executable) 'Could not upgrade after legacy rollback'
    }
    if ($DefaultMigration) { Assert ((Get-Item -LiteralPath $state).LinkTarget -eq $expectedState) 'Missing legacy compatibility junction' }
    $retained = & node -e "const db=new (require('node:sqlite').DatabaseSync)(process.argv[1],{readOnly:true}); console.log(db.prepare('SELECT value FROM handoff_fixture').get().value); db.close()" (Join-Path $expectedState 'edge.sqlite3')
    Assert ($LASTEXITCODE -eq 0 -and $retained -ceq $nonce) 'The original state database was not preserved'
    [pscustomobject]@{ OldVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($oldExe).ProductVersion; OldPid=$first.Id; OldExit=$first.ExitCode
        NewVersion=$result.Version; NewPid=$result.ProcessId; RepeatPid=$repeat.ProcessId; SecondResidentBlocked=$true; OriginalDatabaseKept=$true }
} finally {
    foreach ($p in $owned) {
        try { if (!$p.HasExited) { $p.Kill(); $p.WaitForExit() } } finally { $p.Dispose() }
    }
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('nodicarta-handoff-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
