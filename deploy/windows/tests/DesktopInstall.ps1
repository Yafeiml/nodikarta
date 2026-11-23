#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$work = [IO.Directory]::CreateTempSubdirectory('ProjectHub-DesktopInstall-')
$priorCodexProfile = $env:CODEX_HOME
$env:CODEX_HOME = Join-Path $work.FullName 'codex-profile'
$package = [IO.Directory]::CreateDirectory((Join-Path $work.FullName 'package'))
$bin = [IO.Directory]::CreateDirectory((Join-Path $package.FullName 'bin'))
$install = Join-Path $work.FullName 'installed desktop'
$realShell = Microsoft.PowerShell.Utility\New-Object -ComObject WScript.Shell
$folders = [pscustomobject]@{ Root = $work.FullName }
$folders | Add-Member ScriptMethod Item { param($folder) Join-Path $this.Root $folder }
$testShell = [pscustomobject]@{ SpecialFolders = $folders; Shell = $realShell }
$testShell | Add-Member ScriptMethod CreateShortcut { param($path) $this.Shell.CreateShortcut($path) }
function New-Object([string]$ComObject) {
    if ($ComObject -ne 'WScript.Shell') { throw 'Unexpected COM object.' }
    return $testShell
}
function Get-ScheduledTask($TaskPath,$TaskName,$ErrorAction) { return }
function Assert($Passed, $Message) { if (!$Passed) { throw $Message } }
try {
    $entry = Join-Path $package.FullName 'Install-EdgeDesktop.ps1'
    foreach ($name in 'Install-EdgeDesktop.ps1','Test-EdgeRelease.ps1','Edge-ActiveRelease.ps1') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../$name") -Destination $package.FullName
    }
    $exeName = 'ProjectHub.Edge.Desktop.exe'
    $testExe = Join-Path $bin.FullName $exeName
    $stub = Join-Path $work.FullName 'fixture.c'
    [IO.File]::WriteAllText($stub, '#include <stdlib.h>' + [Environment]::NewLine + 'int main(void) { return getenv("NODICARTA_TEST_INSTALL_FAILURE") ? 1 : 0; }')
    gcc -s $stub -o $testExe
    if ($LASTEXITCODE) { throw 'Could not build the fixture executable.' }
    Copy-Item -LiteralPath $testExe -Destination (Join-Path $bin.FullName 'ProjectHub.Edge.exe')
    Copy-Item -LiteralPath $testExe -Destination (Join-Path $bin.FullName 'Nodicarta.Edge.Launcher.exe')
    function New-TestVersion($value) {
        [IO.File]::WriteAllText((Join-Path $bin.FullName 'build.txt'), $value)
        $lines = Get-ChildItem -LiteralPath $bin.FullName -File | ForEach-Object { (Get-FileHash -LiteralPath $_.FullName).Hash + '  ' + $_.Name }
        [IO.File]::WriteAllLines((Join-Path $package.FullName 'SHA256SUMS'), [string[]]$lines)
    }
    New-TestVersion 'first'
    $first = & $entry -InstallDirectory $install
    $menu = Join-Path $work.FullName 'Programs/Nodicarta Edge.lnk'
    $startup = Join-Path $work.FullName 'Startup/Nodicarta Edge.lnk'
    $legacyMenu = Join-Path $work.FullName 'Programs/ProjectHub Edge.lnk'
    $legacyStartup = Join-Path $work.FullName 'Startup/ProjectHub Edge.lnk'
    Assert ($realShell.CreateShortcut($menu).TargetPath -eq $first.Executable) 'Start menu target'
    Assert ($realShell.CreateShortcut($menu).Arguments -eq '') 'Start menu opens window'
    Assert ($realShell.CreateShortcut($startup).Arguments -eq '--background') 'Login starts quietly'
    # Recreate the pre-launcher layout, then fail its first upgrade after the
    # new command exists. Registrations already migrated must still run v1.
    $active = Join-Path $install 'active-release'
    $firstBuild = [IO.Directory]::GetParent($first.Executable).Parent.Name
    [IO.File]::Delete($first.AgentCommand)
    [IO.File]::Delete($active)
    New-TestVersion 'first upgrade fails'
    $rejected = $false
    try {
        $env:NODICARTA_TEST_INSTALL_FAILURE='1'
        $null = & $entry -InstallDirectory $install
    } catch { $rejected = $true } finally { Remove-Item Env:NODICARTA_TEST_INSTALL_FAILURE }
    Assert ($rejected -and [IO.File]::ReadAllText($active) -ceq $firstBuild -and [IO.File]::Exists($first.AgentCommand) -and
        $realShell.CreateShortcut($menu).TargetPath -eq $first.Executable) 'First legacy upgrade must leave a working rollback target'
    New-TestVersion 'first'
    Move-Item -LiteralPath $menu -Destination $legacyMenu
    Move-Item -LiteralPath $startup -Destination $legacyStartup
    Assert ((& $entry -InstallDirectory $install).Executable -eq $first.Executable) 'Upgrade existing branded shortcuts'
    Assert ((Test-Path $menu) -and (Test-Path $startup) -and !(Test-Path $legacyMenu) -and !(Test-Path $legacyStartup)) 'Migrate both managed links'
    & $entry -InstallDirectory $install -Action DisableStartup
    Move-Item -LiteralPath $menu -Destination $legacyMenu
    New-TestVersion 'second'
    $second = & $entry -InstallDirectory $install
    Assert ($second.Executable -ne $first.Executable -and !(Test-Path -LiteralPath $startup)) 'Upgrade preserves opt out'
    $secondBuild = [IO.Directory]::GetParent($second.Executable).Parent.Name
    Assert ($second.AgentCommand -eq $first.AgentCommand -and [IO.File]::Exists($second.AgentCommand) -and
        [IO.File]::ReadAllText($active) -ceq $secondBuild) 'Upgrade preserves the launcher and activates the new CLI'
    & $entry -InstallDirectory $install -Action EnableStartup
    Assert ($realShell.CreateShortcut($startup).TargetPath -eq $second.Executable) 'Re-enable uses current version'
    New-TestVersion 'failure'
    $rejected = $false
    try {
        $env:NODICARTA_TEST_INSTALL_FAILURE='1'
        $null = & $entry -InstallDirectory $install
    } catch { $rejected = $true } finally { Remove-Item Env:NODICARTA_TEST_INSTALL_FAILURE }
    Assert ($rejected -and [IO.File]::ReadAllText($active) -ceq $secondBuild -and
        $realShell.CreateShortcut($menu).TargetPath -eq $second.Executable) 'Failed registration restores the active release'
    [IO.File]::WriteAllText($testExe, 'corrupt')
    $rejected = $false
    try { $null = & $entry -InstallDirectory $install } catch { $rejected = $true }
    Assert $rejected 'Reject corrupt archive payload'
    Assert ($realShell.CreateShortcut($menu).TargetPath -eq $second.Executable) 'Failed install preserves current target'
    $unrelated = Join-Path $install 'user-file.txt'
    [IO.File]::WriteAllText($unrelated, 'keep')
    & $entry -InstallDirectory $install -Action Uninstall
    Assert (!(Test-Path -LiteralPath $startup) -and !(Test-Path -LiteralPath $menu)) 'Uninstall removes both links'
    Assert (!(Test-Path -LiteralPath $first.Executable) -and !(Test-Path -LiteralPath $second.Executable)) 'Uninstall removes both releases'
    Assert (!(Test-Path -LiteralPath $second.AgentCommand) -and !(Test-Path -LiteralPath $active)) 'Uninstall removes the managed launcher and pointer'
    Assert ([IO.File]::Exists($unrelated)) 'Preserve unrelated files'
    'Desktop installer checks passed.'
} finally {
    $env:CODEX_HOME = $priorCodexProfile
    if ($work.Parent.FullName -eq [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -and $work.Name.StartsWith('ProjectHub-DesktopInstall-')) {
        Remove-Item -LiteralPath $work.FullName -Recurse
    }
}
