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
    [string]$Node = 'node.exe'
)
#Requires -Version 7
$ErrorActionPreference = 'Stop'
function Require($condition, [string]$message) { if (!$condition) { throw $message } }
function Wait-For([scriptblock]$check, [string]$description) {
    $deadline = [datetime]::UtcNow.AddSeconds(65)
    do { $value = & $check; if ($value) { return $value }; Start-Sleep -Milliseconds 200 } until ([datetime]::UtcNow -ge $deadline)
    throw "Timed out waiting for $description"
}
function Digest([string]$file) { (Get-FileHash -LiteralPath $file).Hash }
function Run-Setup([string]$exe, [string]$arguments, [switch]$ExpectFailure) {
    $proc = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try {
        Require ($proc.WaitForExit(150000)) 'Installer did not exit'
        Require (($proc.ExitCode -ne 0) -eq $ExpectFailure.IsPresent) "Unexpected installer exit code $($proc.ExitCode)"
    } finally { $proc.Dispose() }
}
function Residents {
    @(Get-Process -Name ProjectHub.Edge.Desktop -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($install+'\', [StringComparison]::OrdinalIgnoreCase) })
}
function Current-Desktop {
    $id = [IO.File]::ReadAllText($pointer)
    Require ($id -cmatch '^[0-9a-f]{12}$') 'Invalid active release'
    Join-Path $install "$id/bin/ProjectHub.Edge.Desktop.exe"
}
function Check-Resident([version]$version) {
    $expected = Current-Desktop
    $p = Wait-For { @(Residents) | Where-Object Path -eq $expected } 'installed desktop'
    Require (@(Residents).Count -eq 1 -and $p.MainWindowHandle -eq 0 -and [version]$p.MainModule.FileVersionInfo.ProductVersion -eq $version) 'Unexpected desktop instance'
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
$pointer = Join-Path $install 'active-release'
$launcher = Join-Path $install 'ProjectHub.Edge.exe'
$journal = Join-Path $install '.nodicarta-install-transaction.json'
$receipt = Join-Path $install 'public-install.json'

# First prove clean setup, then retain the verified package to inject only an OS launch failure.
Run-Setup $Candidate "/S /D=$install"
$null = Check-Resident $new.ProductVersion
$candidateId = [IO.File]::ReadAllText($pointer)
$savedPackage = Join-Path $work $candidateId
Copy-Item -LiteralPath (Join-Path $install $candidateId) -Destination $savedPackage -Recurse
$payload = Get-Content -LiteralPath (Join-Path $savedPackage 'bin/nodicarta-release.json') -Raw | ConvertFrom-Json
Require ($payload.releaseLine -eq 'public-v1' -and $payload.channel -eq 'stable' -and $payload.publisherKeyId -eq
    '48702825F820B9C4F15772515EE04D79F4D9B8153E984FBE6A862C9B7F4B0783') 'Unexpected publisher or channel'
Run-Setup (Join-Path $install 'Uninstall.exe') '/S'
$null = Wait-For { !(Test-Path $reg) -and !(Test-Path $pointer) -and !(Test-Path $menu) -and !(Test-Path $startup) } 'clean uninstall'
Run-Setup $Baseline "/S /D=$install"
$baselineProcess = Check-Resident $old.ProductVersion
$baseId = [IO.File]::ReadAllText($pointer)
$baseReceipt = Digest $receipt
$baseMenu = Digest $menu
Require (Test-Path -LiteralPath $startup) 'Baseline did not create the login shortcut'
[IO.File]::Delete($startup)
Copy-Item -LiteralPath $savedPackage -Destination (Join-Path $install $candidateId) -Recurse

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
    Require ((Fixture-Message connect).ok) 'Old MCP connection failed'
    $image = Join-Path $install "$candidateId/bin/ProjectHub.Edge.Desktop.exe"
    $acl = Get-Acl -LiteralPath $image
    $original = Get-Acl -LiteralPath $image
    $deny = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.AccessControl.FileSystemRights]::ExecuteFile,[Security.AccessControl.AccessControlType]::Deny)
    $acl.AddAccessRule($deny)
    try {
        Set-Acl -LiteralPath $image -AclObject $acl
        Run-Setup $Candidate "/S /UPDATE /D=$install" -ExpectFailure
    } finally { Set-Acl -LiteralPath $image -AclObject $original }
    $restored = Check-Resident $old.ProductVersion
    Require ($baselineProcess.HasExited -and $restored.Id -ne $baselineProcess.Id -and
        [IO.File]::ReadAllText($pointer) -eq $baseId -and !(Test-Path $journal) -and
        (Digest $menu) -eq $baseMenu -and !(Test-Path $startup) -and (Digest $receipt) -eq $baseReceipt -and
        (Get-ItemPropertyValue $reg DisplayVersion) -eq $old.ProductVersion) 'Incomplete rollback'
    Check-Pairing
    Require ((Fixture-Message probe).ok) 'Old MCP session failed after rollback'
    Run-Setup $Candidate "/S /UPDATE /D=$install"
    $next = Check-Resident $new.ProductVersion
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($menu)
    Require ($restored.HasExited -and [IO.File]::ReadAllText($pointer) -eq $candidateId -and !(Test-Path $journal) -and
        $link.TargetPath -eq $next.Path -and !(Test-Path $startup) -and (Get-ItemPropertyValue $reg DisplayVersion) -eq $new.ProductVersion) 'Incomplete upgrade'
    Check-Pairing
    Require ((Fixture-Message connect).connections -eq 2 -and (Fixture-Message probe).ok) 'Old or new MCP session lost after upgrade'
} finally {
    $rpc.StandardInput.Close()
    if (!$rpc.WaitForExit(10000)) { $rpc.Kill($true); $rpc.WaitForExit() }
    $rpc.Dispose()
}
[ordered]@{
    schema=1; checkedAtUtc=[datetime]::UtcNow.ToString('o'); windowsBuild=[Environment]::OSVersion.Version.ToString()
    candidate=$new.ProductVersion; baseline=$old.ProductVersion; installerSha256=$CandidateSha256.ToLowerInvariant()
    baselineSha256=$BaselineSha256.ToLowerInvariant(); installedDesktopSha256=Digest $next.Path
    standardInstaller=$true; candidateCleanInstall=$true; publisherKeyId=$payload.publisherKeyId
    upgradeArgument='/UPDATE'; rollbackTrigger='Windows denied ExecuteFile on the candidate desktop only'
    restoredOriginalMenuBytes=$true; absentLoginShortcutPreserved=$true; oldResidentRestarted=$true
    pairedIdentityAndBindingPreserved=$true; inFlightMcpAcrossRollbackAndUpgrade=$true; freshMcpAfterUpgrade=$true
} | ConvertTo-Json | Set-Content -LiteralPath $Evidence -Encoding utf8
