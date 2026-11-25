# Run only in a disposable, offline x64 Windows VM. Revert its clean snapshot before each test.
# Supply the installed PowerShell 7 and Node runtime. No signing key or update server is used.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Candidate,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$CandidateSha256,
    [Parameter(Mandatory)][string]$Baseline,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$BaselineSha256,
    [Parameter(Mandatory)][guid]$GuestUuid,
    [Parameter(Mandatory)][string]$Evidence,
    [string]$Node = 'node.exe',
    [switch]$CheckStreamlinedPayload
)
#Requires -Version 7
$ErrorActionPreference = 'Stop'
function Require($condition, [string]$message) { if (!$condition) { throw $message } }
function Wait-For([scriptblock]$check, [string]$description, [int]$interval = 200) {
    $deadline = [datetime]::UtcNow.AddSeconds(65)
    do { $value = & $check; if ($value) { return $value }; Start-Sleep -Milliseconds $interval } until ([datetime]::UtcNow -ge $deadline)
    throw "Timed out waiting for $description"
}
function Digest([string]$file) { (Get-FileHash -LiteralPath $file).Hash }
function Run-Setup([string]$exe, [string]$arguments, [int]$ExpectedExit = 0) {
    $proc = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        Require ($proc.WaitForExit(150000)) 'Installer did not exit'
        Require ($proc.ExitCode -eq $ExpectedExit) "Expected installer exit $ExpectedExit, got $($proc.ExitCode)"
    } finally { $proc.Dispose() }
}
function Residents {
    @(Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($install+'\', [StringComparison]::OrdinalIgnoreCase) })
}
function Current-Desktop {
    foreach ($name in 'ProjectHub.Edge.exe','ProjectHub.Edge.Desktop.exe','NodicartaEdgeCli.dll','SHA256SUMS') {
        Require ([IO.File]::Exists((Join-Path $install $name))) "Missing root runtime file: $name"
    }
    foreach ($name in 'bin','active-release','Nodicarta.Edge.Launcher.exe','hostfxr.dll') {
        Require (!(Test-Path -LiteralPath (Join-Path $install $name))) "Unexpected runtime entry: $name"
    }
    Require (@(Get-ChildItem -LiteralPath $install -Directory -Force | Where-Object Name -cmatch '^[0-9a-f]{12}$').Count -eq 0) 'A version directory remains'
    Join-Path $install 'ProjectHub.Edge.Desktop.exe'
}
function No-Transaction {
    !(Test-Path -LiteralPath $journal) -and !(Test-Path -LiteralPath $backup)
}
function Check-StreamlinedPayload([switch]$UserAssets) {
    foreach ($name in 'release','LICENSE','THIRD-PARTY-NOTICES.md','WINDOWS_EDGE_DESKTOP.md','WINDOWS_EDGE_DESKTOP.en.md','Assets/Harnesses/NOTICE.txt') {
        Require (!(Test-Path -LiteralPath (Join-Path $install $name))) "Retired package item remains: $name"
    }
    if ($UserAssets) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $install 'Assets') -Recurse -File -Force)
        Require ($files.Count -eq 1 -and $files[0].FullName -eq $customAsset -and
            [IO.File]::ReadAllText($customAsset) -ceq 'keep the user asset') 'Update removed a user asset or left an obsolete managed asset'
    } else {
        $directories = @(Get-ChildItem -LiteralPath $install -Directory -Force)
        Require ($directories.Count -eq 1 -and $directories[0].Name -ceq 'skills') 'Clean install created an unexpected payload directory'
    }
    $notices = [IO.File]::ReadAllText((Join-Path $install 'LICENSES.txt'))
    foreach ($section in 'THIRD-PARTY-NOTICES.md','Assets/Harnesses/NOTICE.txt','Avalonia-LICENSE.txt','NSIS-COPYING.txt') {
        Require ($notices.Contains($section)) "Consolidated license is missing: $section"
    }
}
function Check-Resident([version]$version, [switch]$Visible) {
    $expected = Current-Desktop
    $p = Wait-For { @(Residents) | Where-Object { $_.Path -eq $expected -and (!$Visible -or $_.MainWindowHandle -ne 0) } } 'installed desktop'
    Require (@(Residents).Count -eq 1 -and ($p.MainWindowHandle -ne 0) -eq $Visible.IsPresent -and
        [version]$p.MainModule.FileVersionInfo.ProductVersion -eq $version) 'Unexpected desktop instance'
    $p
}
function Fixture-Message([string]$command) {
    if ($command) { $rpc.StandardInput.WriteLine($command) }
    $line = $rpc.StandardOutput.ReadLineAsync().WaitAsync([timespan]::FromSeconds(55)).GetAwaiter().GetResult()
    Require ($null -ne $line) 'MCP fixture exited'
    $line | ConvertFrom-Json
}
function Check-Pairing {
    $result = & $launcher --state $state projects | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 0 -and $result.projectId -eq $fixture.projectId) 'Lost paired project'
    $bindings = & $launcher --state $state bindings --local | ConvertFrom-Json
    Require ($LASTEXITCODE -eq 0 -and @($bindings).Count -eq 1 -and $bindings[0].localPath -eq $folder) 'Lost folder binding'
}

$machine = Get-CimInstance Win32_ComputerSystem
Require ($IsWindows -and $machine.Model -match 'VMware|Virtual Machine|VirtualBox|KVM|QEMU' -and
    [guid](Get-CimInstance Win32_ComputerSystemProduct).UUID -eq $GuestUuid -and $GuestUuid -ne [guid]::Empty) 'Run inside the selected disposable VM'
$reg = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/Nodicarta.Edge'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Nodicarta Edge.lnk'
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Nodicarta Edge.lnk'
foreach ($existing in $reg,$menu,$startup,($menu -replace 'Nodicarta Edge.lnk$','ProjectHub Edge.lnk'),($startup -replace 'Nodicarta Edge.lnk$','ProjectHub Edge.lnk'),
    (Join-Path $env:LOCALAPPDATA 'Nodicarta'),(Join-Path $env:LOCALAPPDATA 'ProjectHub/Edge')) {
    Require (!(Test-Path -LiteralPath $existing)) 'Restore a clean guest snapshot first'
}
Require (!(Get-Process -Name ProjectHub.Edge.Desktop,ProjectHub.Edge -ErrorAction SilentlyContinue)) 'An Edge process already belongs to this VM'
$Candidate = (Resolve-Path -LiteralPath $Candidate).Path
$Baseline = (Resolve-Path -LiteralPath $Baseline).Path
Require ((Digest $Candidate) -eq $CandidateSha256 -and (Digest $Baseline) -eq $BaselineSha256) 'Installer SHA-256 mismatch'
$new = [Diagnostics.FileVersionInfo]::GetVersionInfo($Candidate)
$old = [Diagnostics.FileVersionInfo]::GetVersionInfo($Baseline)
Require ($new.Comments -eq 'Nodicarta Edge public installer' -and $old.Comments -eq $new.Comments -and
    [version]$new.ProductVersion -gt [version]$old.ProductVersion) 'Expected official-configuration installers and a newer candidate'
$work = (New-TemporaryFile).FullName
Remove-Item -LiteralPath $work
$null = New-Item -ItemType Directory $work
$install = Join-Path $work 'Edge Install'
$state = Join-Path $work 'state'
$folder = (New-Item -ItemType Directory (Join-Path $work 'workspace')).FullName
$env:NODICARTA_EDGE_STATE = $env:PROJECTHUB_EDGE_STATE = $state
$manifest = Join-Path $install 'SHA256SUMS'
$launcher = Join-Path $install 'ProjectHub.Edge.exe'
$journal = Join-Path $install '.nodicarta-install-transaction.json'
$backup = Join-Path $install '.nodicarta-install-backup'
$receipt = Join-Path $install 'public-install.json'
$customAsset = Join-Path $install 'Assets/Harnesses/QA-local-user-note.txt'
$shell = New-Object -ComObject WScript.Shell

# Capture the exact candidate's installed bytes before exercising upgrade and rollback.
Run-Setup $Candidate "/S /D=$install"
$candidateDesktop = Check-Resident $new.ProductVersion
if ($CheckStreamlinedPayload) { Check-StreamlinedPayload }
$candidateManifest = Digest $manifest
$candidateImage = Digest $candidateDesktop.Path
$payload = Get-Content -LiteralPath (Join-Path $install 'nodicarta-release.json') -Raw | ConvertFrom-Json
Require ($payload.releaseLine -eq 'public-v1' -and $payload.channel -eq 'stable' -and $payload.publisherKeyId -eq
    '48702825F820B9C4F15772515EE04D79F4D9B8153E984FBE6A862C9B7F4B0783') 'Unexpected publisher or channel'
$cleanLauncher = Digest $launcher
Require ($shell.CreateShortcut($menu).TargetPath -eq $launcher -and
    $shell.CreateShortcut($startup).TargetPath -eq $launcher -and (No-Transaction)) 'Clean setup did not create fixed shortcuts'
Run-Setup (Join-Path $install 'Uninstall.exe') '/S'
$null = Wait-For { !(Test-Path $reg) -and !(Test-Path $manifest) -and !(Test-Path $menu) -and !(Test-Path $startup) } 'clean uninstall'
Run-Setup $Baseline "/S /D=$install"
$baselineProcess = Check-Resident $old.ProductVersion
$baseManifest = Digest $manifest
$baseLauncher = Digest $launcher
$baseDesktop = Digest $baselineProcess.Path
Require (Test-Path -LiteralPath $startup) 'Baseline did not create the login shortcut'

$start = [Diagnostics.ProcessStartInfo]::new((Get-Command $Node -CommandType Application).Source)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $start.RedirectStandardOutput = $true
foreach ($arg in (Join-Path $PSScriptRoot 'windows-draft-session.cjs'),$launcher,$state) { $start.ArgumentList.Add($arg) }
$rpc = [Diagnostics.Process]::Start($start)
try {
    $fixture = Fixture-Message
    $fixture.pairing | & $launcher --state $state pair --core $fixture.url | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Pairing failed'
    & $launcher --state $state bind --project $fixture.projectId --path $folder | Out-Null
    Require ($LASTEXITCODE -eq 0) 'Binding failed'
    Check-Pairing

    $baselineImage = $baselineProcess.Path
    $baselineProcess.Kill()
    $baselineProcess.WaitForExit()
    [IO.File]::Move($baselineImage, (Join-Path $work 'removed-desktop.exe'))
    Run-Setup $Baseline "/S /D=$install"
    $baselineProcess = Check-Resident $old.ProductVersion
    Require ($baselineProcess.Path -eq $baselineImage -and (Digest $manifest) -eq $baseManifest -and
        (Digest $baselineImage) -eq $baseDesktop -and (No-Transaction)) 'Repair did not restore the missing desktop'
    Check-Pairing

    $baselineProcess.Kill()
    $baselineProcess.WaitForExit()
    $existing = (Resolve-Path -LiteralPath $install).Path
    Require ([IO.Path]::GetDirectoryName($existing) -eq $work) 'The old installation is outside the temporary QA directory'
    [IO.Directory]::Move($existing, (Join-Path $work 'removed-program-directory'))
    Require (!(Test-Path -LiteralPath $install) -and (Test-Path -LiteralPath $reg) -and
        (Test-Path -LiteralPath $menu) -and (Test-Path -LiteralPath $startup)) 'Could not reproduce stale registration and links'
    Run-Setup $Baseline "/S /D=$install"
    $baselineProcess = Check-Resident $old.ProductVersion
    $login = $shell.CreateShortcut($startup)
    Require ($login.TargetPath -eq $launcher -and $login.Arguments -eq "--background --state `"$state`"") 'Reinstall lost the login choice or its custom state'
    Check-Pairing

    # Run the installed shortcut through Explorer/ShellExecute as a user would.
    $baselineProcess.Kill()
    $baselineProcess.WaitForExit()
    Start-Process -FilePath $menu
    $visible = Check-Resident $old.ProductVersion -Visible
    $visible.Kill()
    $visible.WaitForExit()
    Start-Process -FilePath $startup -WindowStyle Hidden
    $baselineProcess = Check-Resident $old.ProductVersion

    $baseReceipt = Digest $receipt
    $baseMenu = Digest $menu
    $baseStartup = Digest $startup
    if ($CheckStreamlinedPayload) {
        $retiredNotice = Join-Path $install 'Assets/Harnesses/NOTICE.txt'
        $retiredGuide = Join-Path $install 'WINDOWS_EDGE_DESKTOP.md'
        Require ((Test-Path -LiteralPath $retiredNotice) -and (Test-Path -LiteralPath (Join-Path $install 'release/licenses/edge'))) 'The baseline did not install the retired payload'
        [IO.File]::WriteAllText($customAsset, 'keep the user asset')
        $original = [IO.File]::ReadAllBytes($retiredGuide)
        try {
            [IO.File]::AppendAllText($retiredGuide, 'user modification')
            $modified = Digest $retiredGuide
            Run-Setup $Candidate "/S /UPDATE /D=$install" -ExpectedExit 1
            Require (!$baselineProcess.HasExited -and (Digest $retiredGuide) -eq $modified -and
                (Digest $manifest) -eq $baseManifest -and (No-Transaction)) 'Setup overwrote a modified legacy file or started a transaction'
        } finally { [IO.File]::WriteAllBytes($retiredGuide, $original) }
    }
    Require ((Fixture-Message connect).ok) 'Old MCP connection failed'
    Run-Setup $Candidate "/S /UPDATE /D=$install" -ExpectedExit 75
    Require (!$baselineProcess.HasExited -and (Digest $manifest) -eq $baseManifest -and
        (Digest $launcher) -eq $baseLauncher -and (No-Transaction) -and (Fixture-Message probe).ok) 'Update interrupted an active MCP session or touched the installed files'
    Require ((Fixture-Message disconnect).connections -eq 0) 'Could not disconnect the baseline MCP fixture'
    $null = Wait-For { !(Get-Process -Name ProjectHub.Edge -ErrorAction SilentlyContinue | Where-Object Path -eq $launcher) } 'MCP handles to close'

    foreach ($startupEnabled in $true,$false) {
        if (!$startupEnabled) { [IO.File]::Delete($startup) }
        # The real installer requires the candidate to stay alive after signaling ready.
        $failing = Start-Process -FilePath $Candidate -ArgumentList "/S /UPDATE /D=$install" -WindowStyle Hidden -PassThru
        try {
            $starting = Wait-For {
                if ($failing.HasExited) { throw 'Setup exited before the candidate health check' }
                @(Residents) | Where-Object { $_.Id -ne $baselineProcess.Id } | Select-Object -First 1
            } 'candidate startup' 20
            Require ((Test-Path -LiteralPath $journal) -and (Digest $starting.Path) -eq $candidateImage) 'Unexpected candidate process'
            $starting.Kill()
            Require ($starting.WaitForExit(10000) -and $failing.WaitForExit(90000) -and $failing.ExitCode -eq 1) 'Unhealthy candidate did not fail setup'
        } finally { $failing.Dispose() }
        $restored = Check-Resident $old.ProductVersion
        $loginPreserved = if ($startupEnabled) { (Digest $startup) -eq $baseStartup } else { !(Test-Path $startup) }
        Require ($baselineProcess.HasExited -and $restored.Id -ne $baselineProcess.Id -and
            (Digest $manifest) -eq $baseManifest -and (Digest $restored.Path) -eq $baseDesktop -and (No-Transaction) -and
            (Digest $launcher) -eq $baseLauncher -and (Digest $menu) -eq $baseMenu -and $loginPreserved -and (Digest $receipt) -eq $baseReceipt -and
            (Get-ItemPropertyValue $reg DisplayVersion) -eq $old.ProductVersion) 'Incomplete rollback'
        Check-Pairing
        $baselineProcess = $restored
    }
    Run-Setup $Candidate "/S /UPDATE /D=$install"
    $next = Check-Resident $new.ProductVersion
    $updatedImage = $next.Path
    $link = $shell.CreateShortcut($menu)
    Require ($restored.HasExited -and (Digest $manifest) -eq $candidateManifest -and (Digest $next.Path) -eq $candidateImage -and
        (No-Transaction) -and $link.TargetPath -eq $launcher -and (Digest $launcher) -eq $cleanLauncher -and !(Test-Path $startup) -and
        (Get-ItemPropertyValue $reg DisplayVersion) -eq $new.ProductVersion) 'Incomplete upgrade'
    if ($CheckStreamlinedPayload) { Check-StreamlinedPayload -UserAssets }
    Check-Pairing
    Require ((Fixture-Message connect).connections -eq 1 -and (Fixture-Message probe).ok) 'New MCP session failed after upgrade'

    $extra = Join-Path $install 'user-file.txt'
    [IO.File]::WriteAllText($extra, 'keep')
    Run-Setup (Join-Path $install 'Uninstall.exe') '/S'
    $null = Wait-For {
        !(Test-Path -LiteralPath $manifest) -and !(Test-Path -LiteralPath $menu) -and !(Test-Path -LiteralPath $reg) -and
        !(Get-Process -Name ProjectHub.Edge -ErrorAction SilentlyContinue | Where-Object Path -eq $launcher)
    } 'uninstall to end this installed MCP and remove the managed payload'
    Require ([IO.File]::ReadAllText($extra) -ceq 'keep') 'Uninstall altered an unrelated user file'
    Run-Setup $Candidate "/S /D=$install"
    $null = Check-Resident $new.ProductVersion
    if ($CheckStreamlinedPayload) { Check-StreamlinedPayload -UserAssets }
    Check-Pairing
} finally {
    $rpc.StandardInput.Close()
    if (!$rpc.WaitForExit(10000)) { $rpc.Kill($true); $rpc.WaitForExit() }
    $rpc.Dispose()
}
[ordered]@{
    schema=1; checkedAtUtc=[datetime]::UtcNow.ToString('o'); windowsBuild=[Environment]::OSVersion.Version.ToString()
    candidate=$new.ProductVersion; baseline=$old.ProductVersion; installerSha256=$CandidateSha256.ToLowerInvariant()
    baselineSha256=$BaselineSha256.ToLowerInvariant(); installedDesktopSha256=$candidateImage
    standardInstaller=$true; candidateCleanInstall=$true; publisherKeyId=$payload.publisherKeyId; installedDesktopPath=$updatedImage
    upgradeArgument='/UPDATE'; rollbackTrigger='Terminated the candidate desktop during the installer health check'
    restoredOriginalMenuBytes=$true; presentLoginShortcutPreserved=$true; absentLoginShortcutPreserved=$true; oldResidentRestarted=$true
    fixedLauncherSha256=$cleanLauncher; flatPayloadVerified=$true; fixedShortcutVerified=$true
    missingCurrentDesktopRepaired=$true; staleRegistrationAndShortcutsRepaired=$true; explorerShortcutStartsGui=$true
    pairedIdentityAndBindingPreserved=$true; inFlightMcpDeferredUpdate=$true; freshMcpAfterUpgrade=$true
    uninstallClosedOwnedMcpAndKeptUserFiles=$true
    streamlinedPayloadChecked=[bool]$CheckStreamlinedPayload
} | ConvertTo-Json | Set-Content -LiteralPath $Evidence -Encoding utf8
